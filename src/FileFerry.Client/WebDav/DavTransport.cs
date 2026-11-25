using System.Net;
using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Client.WebDav;

/// <summary>
/// WebDAV HTTP transport: <see cref="HttpClient"/> + Basic auth with a "live" token provider +
/// expansion of <see cref="RemotePath"/> into URLs. Shared by <see cref="WebDavRemoteStore"/>
/// (the <c>files/</c> collection) and <see cref="ChunkedV2Uploader"/> (the <c>uploads/</c>
/// collection): authentication and failure classification live in one place.
/// Network failures become <see cref="ErrorKind.Network"/> right here (_1).
/// </summary>
internal sealed class DavTransport
{
    /// <summary>
    /// Cap of a metadata answer (PROPFIND bodies): every honest directory listing fits — the
    /// refusal protects against an inflated or hostile body, compressed included (the cap counts
    /// decoded bytes, not wire bytes). File content is not capped here: it streams past the parser.
    /// </summary>
    internal const int MetadataBodyCapBytes = 8 * 1024 * 1024;

    /// <summary>
    /// PROPFIND body: the set of properties the engine needs from each entry.
    /// </summary>
    internal const string PropfindBody =
        """<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop>"""
        + """<d:resourcetype/><d:getetag/><d:getcontentlength/><d:getlastmodified/>"""
        + """</d:prop></d:propfind>""";

    /// <summary>
    /// Root of the user's DAV files (with the terminal "/"); paths are counted relative to it.
    /// </summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// Root of chunked-UPLOAD v2: the sibling collection <c>…/dav/uploads/{user}/</c>
    /// (two segments above the files root: backend-php + files).
    /// </summary>
    public Uri UploadsBaseAddress { get; }

    private readonly HttpClient _httpClient;
    private readonly string _userName;
    private readonly Func<CancellationToken, Task<string?>> _tokenProvider;

    /// <summary>
    /// Transport for requests to <paramref name="baseAddress"/> on behalf of <paramref name="userName"/>;
    /// the token is asked for every request (_2 — no secret caching anywhere).
    /// </summary>
    public DavTransport(HttpClient httpClient, Uri baseAddress, string userName, Func<CancellationToken, Task<string?>> tokenProvider)
    {
        _httpClient = httpClient;
        BaseAddress = baseAddress;
        _userName = userName;
        _tokenProvider = tokenProvider;
        UploadsBaseAddress = new Uri(baseAddress, $"../../uploads/{Uri.EscapeDataString(userName)}/");
    }

    /// <summary>
    /// URL of an object in the files collection (root = the base address itself).
    /// </summary>
    public Uri UrlFor(RemotePath path) =>
        path.IsRoot ? BaseAddress : new Uri(BaseAddress, path.ToEncodedPath());

    /// <summary>
    /// URL in the uploads collection from a transaction-relative path (no leading "/").
    /// </summary>
    public Uri UploadsUrlFor(string transferRelativePath) => new(UploadsBaseAddress, transferRelativePath);

    /// <summary>
    /// Executes an authenticated request; on <c>401</c> it refreshes the token and retries exactly once.
    /// <paramref name="beforeRetry"/> runs before the retry (e.g. rewind the body); returning
    /// <see langword="false"/> cancels the retry (a non-seekable body cannot be rewritten).
    /// Cancellation of <paramref name="ct"/> propagates upward; the remaining failures become
    /// <see cref="Result{TValue, TError}"/>.
    /// </summary>
    public async Task<Result<HttpResponseMessage, SyncError>> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default,
        Func<bool>? beforeRetry = null)
    {
        try
        {
            var response = await SendOnceAsync(requestFactory, completion, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && (beforeRetry is null || beforeRetry()))
            {
                response.Dispose();
                response = await SendOnceAsync(requestFactory, completion, ct).ConfigureAwait(false);
            }

            return Result<HttpResponseMessage, SyncError>.Success(response);
        }
        catch (Exception exc) when (IsNetworkFailure(exc, ct))
        {
            return Result<HttpResponseMessage, SyncError>.Failure(
                SyncError.Of(ErrorKind.Network, $"{requestFactory().Method} request never reached the server: {exc.Message}", exc));
        }
    }

    /// <summary>
    /// Maps the HTTP code of an expected failure onto a <see cref="SyncError"/>;
    /// <see cref="SyncError.IsTransient"/> follows from the category automatically).
    /// </summary>
    public static SyncError ToSyncError(string operation, string target, HttpResponseMessage response)
    {
        var code = response.StatusCode;
        var kind = code switch
        {
            HttpStatusCode.Unauthorized => ErrorKind.AuthExpired,
            HttpStatusCode.Forbidden => ErrorKind.PermissionLost,
            HttpStatusCode.NotFound => ErrorKind.NotFound,
            HttpStatusCode.Conflict => ErrorKind.Conflict,
            HttpStatusCode.PreconditionFailed => ErrorKind.Conflict,
            HttpStatusCode.InsufficientStorage => ErrorKind.StorageFull,
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => ErrorKind.Network,
            >= HttpStatusCode.InternalServerError => ErrorKind.Network,
            _ => ErrorKind.Unknown,
        };
        return SyncError.Of(kind, $"{operation} '{target}' → HTTP {(int)code} ({response.ReasonPhrase ?? code.ToString()})");
    }

    /// <summary>
    /// Reads a metadata answer as text up to <see cref="MetadataBodyCapBytes"/> — counting the
    /// bytes the caller actually receives (an inflated gzip body is caught decoded, not on the
    /// wire). Over the cap the answer is refused with <see cref="ErrorKind.Unknown"/>; nothing
    /// beyond the cap is ever materialised.
    /// </summary>
    public static async Task<Result<string, SyncError>> ReadBodyCappedAsync(
        HttpResponseMessage response, string what, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var collected = new MemoryStream();
        var block = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MetadataBodyCapBytes)
            {
                return Result<string, SyncError>.Failure(SyncError.Of(
                    ErrorKind.Unknown,
                    $"{what} answered with a body over the {MetadataBodyCapBytes / (1024 * 1024)} MiB cap — the answer was refused."));
            }

            collected.Write(block, 0, read);
        }

        return Result<string, SyncError>.Success(Encoding.UTF8.GetString(collected.ToArray()));
    }

    /// <summary>
    /// A network failure or cancellation by someone else's token (not our <paramref name="ct"/>).
    /// <see cref="System.Net.WebException"/> is an Android native-handler quirk (verbs outside
    /// its list): such a failure stays in Results instead of escaping upward.
    /// </summary>
    private static bool IsNetworkFailure(Exception exc, CancellationToken ct) =>
        exc is HttpRequestException or System.Net.WebException
            || (exc is OperationCanceledException && !ct.IsCancellationRequested);

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        using var request = requestFactory();
        var token = await _tokenProvider(ct).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_userName}:{token}")));
        return await _httpClient.SendAsync(request, completion, ct).ConfigureAwait(false);
    }
}

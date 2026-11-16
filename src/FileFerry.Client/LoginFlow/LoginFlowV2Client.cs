using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileFerry.Client.Retry;
using FileFerry.Client.WebDav;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.Client.LoginFlow;

/// <summary>
/// Nextcloud <b>Login Flow v2</b> client (dev-manual "Login flow v2"):
/// POST <c>index.php/login/v2</c> returns a WebView URL and a poll-endpoint+token pair;
/// until the user approves in the browser, poll returns <b>404</b> ("still waiting"),
/// after consent — exactly one 200 with <c>loginName</c> + <c>appPassword</c>
/// ("server" in that response is a URL string). 400 is tolerated as pending on legacy builds.
/// Secrets (poll token, app password) never reach <c>SyncError</c> messages.
/// </summary>
public sealed class LoginFlowV2Client : ILoginFlow
{
    private readonly HttpClient _httpClient;
    private readonly LoginFlowOptions _options;
    private readonly ITaskDelay _delay;

    /// <summary>
    /// Creates the client; <paramref name="delay"/> is the pause mechanics (deterministic in tests).
    /// </summary>
    /// <param name="httpClient">Client without baseline authorization headers (the flow runs pre-auth).</param>
    /// <param name="options">Protocol timings (poll interval, max wait); <c>null</c> — <see cref="LoginFlowOptions.Defaults"/>.</param>
    /// <param name="delay">Pause mechanics (a fake in tests); <c>null</c> — real <see cref="TaskDelay"/>.</param>
    public LoginFlowV2Client(HttpClient httpClient, LoginFlowOptions? options = null, ITaskDelay? delay = null)
    {
        _httpClient = httpClient;
        _options = options ?? LoginFlowOptions.Defaults;
        _delay = delay ?? new TaskDelay();
    }

    /// <inheritdoc />
    public async Task<Result<LoginChallenge, SyncError>> StartAsync(Uri serverBaseUrl, CancellationToken ct)
    {
        var initUrl = new Uri(Normalize(serverBaseUrl), "index.php/login/v2");
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, initUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception exc) when (IsNetworkFailure(exc, ct))
        {
            return Result<LoginChallenge, SyncError>.Failure(Fail("Login init", exc));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return Result<LoginChallenge, SyncError>.Failure(
                    DavTransport.ToSyncError("POST login/v2", initUrl.Host, response));
            }

            var init = await ReadJsonAsync<InitResponse>(response, ct).ConfigureAwait(false);
            if (init is null || init.Login is null || init.Poll?.Token is null || init.Poll.Endpoint is null)
            {
                return Result<LoginChallenge, SyncError>.Failure(SyncError.Of(
                    ErrorKind.Unknown, $"{initUrl.Host}: the login/v2 response does not look like Nextcloud (poll/login missing)."));
            }

            return Result<LoginChallenge, SyncError>.Success(new LoginChallenge(
                new Uri(init.Login),
                new Uri(init.Poll.Endpoint),
                init.Poll.Token,
                init.Server?.Name ?? initUrl.Host,
                init.Server?.Version ?? string.Empty));
        }
    }

    /// <inheritdoc />
    public async Task<Result<LoginPollOutcome, SyncError>> PollAsync(LoginChallenge challenge, CancellationToken ct)
    {
        var (maybeResponse, failure) = await SendPollAsync(challenge, ct).ConfigureAwait(false);
        if (failure is not null || maybeResponse is null)
        {
            return Result<LoginPollOutcome, SyncError>.Failure(failure
                ?? SyncError.Of(ErrorKind.Unknown, "The login poll returned neither a response nor a failure."));
        }

        var response = maybeResponse;
        using (response)
        {
            // 404 is the standard "not yet approved" per dev-manual v2; 400 is tolerated for legacy.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return Result<LoginPollOutcome, SyncError>.Success(LoginPollOutcome.Pending);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Result<LoginPollOutcome, SyncError>.Failure(
                    DavTransport.ToSyncError("POST login poll", challenge.PollEndpoint.Host, response));
            }

            var poll = await ReadJsonAsync<PollResponse>(response, ct).ConfigureAwait(false);
            if (poll?.LoginName is null || poll.AppPassword is null)
            {
                return Result<LoginPollOutcome, SyncError>.Failure(SyncError.Of(
                    ErrorKind.Unknown, $"{challenge.PollEndpoint.Host}: the confirmation arrived without credentials."));
            }

            return Result<LoginPollOutcome, SyncError>.Success(LoginPollOutcome.Authorized(new LoginCredentials(
                ServerOf(challenge),
                poll.LoginName,
                poll.AppPassword,
                ServerNameOf(poll.Server) ?? challenge.ServerName,
                ServerVersionOf(poll.Server) ?? challenge.ServerVersion)));
        }
    }

    /// <inheritdoc />
    public async Task<Result<LoginCredentials, SyncError>> CompleteAsync(LoginChallenge challenge, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await PollAsync(challenge, ct).ConfigureAwait(false);
            if (outcome.IsSuccess && outcome.Value.Status == LoginPollStatus.Authorized)
            {
                return Result<LoginCredentials, SyncError>.Success(outcome.Value.Credentials!);
            }

            // A transient poll failure (429/5xx/network) is no reason to cancel the flow: keep waiting.
            var temporary = outcome.IsFailure && outcome.Error.IsTransient;
            if (outcome.IsFailure && !temporary)
            {
                return Result<LoginCredentials, SyncError>.Failure(outcome.Error);
            }

            if (waited >= _options.MaxWait)
            {
                return Result<LoginCredentials, SyncError>.Failure(temporary
                    ? outcome.Error
                    : SyncError.Of(ErrorKind.Network, "The user did not confirm the sign-in in time — the flow was closed."));
            }

            var pause = temporary ? _options.PollInterval * _options.RateLimitBackoffFactor : _options.PollInterval;
            await _delay.DelayAsync(pause, ct).ConfigureAwait(false);
            waited += pause;
        }
    }

    /// <summary>
    /// One poll request; 429-aware at the call site: the pause is stretched by the backoff
    /// factor, the remaining failure statuses surface as errors.
    /// </summary>
    private async Task<(HttpResponseMessage? Response, SyncError? Failure)> SendPollAsync(
        LoginChallenge challenge, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, challenge.PollEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = challenge.PollToken }),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception exc) when (IsNetworkFailure(exc, ct))
        {
            return (null, Fail("Login poll", exc));
        }

        return (response, null);
    }

    /// <summary>
    /// Options define the base URL only via <see cref="StartAsync"/> — here we rebuild the
    /// host prefix from the poll endpoint.
    /// </summary>
    private static Uri ServerOf(LoginChallenge challenge)
    {
        var endpoint = challenge.PollEndpoint;
        var path = endpoint.AbsolutePath;
        var marker = "/index.php/login/v2/poll";
        var basePath = path.EndsWith(marker, StringComparison.Ordinal) ? path[..^marker.Length] : string.Empty;
        return new UriBuilder(endpoint) { Path = string.IsNullOrEmpty(basePath) ? "/" : basePath, Query = string.Empty }.Uri;
    }

    private static Uri Normalize(Uri serverBaseUrl)
        => new(serverBaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default; // an HTML page instead of JSON (proxy error etc.) — treat as "not Nextcloud"
        }
    }

    private static bool IsNetworkFailure(Exception exc, CancellationToken ct) =>
        exc is HttpRequestException or System.Net.WebException
            || (exc is TaskCanceledException && !ct.IsCancellationRequested);

    /// <summary>
    /// A network error is reported without request details: neither a token-bearing host nor headers.
    /// </summary>
    private static SyncError Fail(string what, Exception cause) =>
        SyncError.Of(ErrorKind.Network, $"{what}: network failure ({cause.Message})", cause);

    /// <summary>
    /// Body of the v2 init response.
    /// </summary>
    private sealed record InitResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("server")] ServerInfo? Server,
        [property: System.Text.Json.Serialization.JsonPropertyName("login")] string? Login,
        [property: System.Text.Json.Serialization.JsonPropertyName("poll")] PollInfo? Poll);

    /// <summary>
    /// The "server" fields in flow responses.
    /// </summary>
    private sealed record ServerInfo(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("version")] string? Version);

    /// <summary>
    /// Token+endpoint pair for polling consent.
    /// </summary>
    private sealed record PollInfo(
        [property: System.Text.Json.Serialization.JsonPropertyName("token")] string? Token,
        [property: System.Text.Json.Serialization.JsonPropertyName("endpoint")] string? Endpoint);

    /// <summary>
    /// Successful poll body: login name + app password. Per dev-manual v2 "server" is <b>a URL string</b>
    /// (unlike init, where it is an object); legacy object-shaped responses are also valid, so the
    /// field is read as <see cref="JsonElement"/> and the shape is unwrapped in place.
    /// </summary>
    private sealed record PollResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("server")] JsonElement? Server,
        [property: System.Text.Json.Serialization.JsonPropertyName("loginName")] string? LoginName,
        [property: System.Text.Json.Serialization.JsonPropertyName("appPassword")] string? AppPassword);

    /// <summary>
    /// Server name from the "server" field: object → <c>name</c>; URL string → host.
    /// </summary>
    private static string? ServerNameOf(JsonElement? server)
    {
        if (server is not { } el)
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            var raw = el.GetString();
            return Uri.TryCreate(raw, UriKind.Absolute, out var url) ? url.Host : raw;
        }

        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;
    }

    /// <summary>
    /// The server version exists only in the object form of the "server" field (legacy).
    /// </summary>
    private static string? ServerVersionOf(JsonElement? server)
    {
        var el = server;
        return el is { ValueKind: JsonValueKind.Object } && el.Value.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
    }
}

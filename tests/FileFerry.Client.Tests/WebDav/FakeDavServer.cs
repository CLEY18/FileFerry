using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using FileFerry.Client.WebDav;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// In-process "DAV server" on <see cref="HttpListener"/>+<see cref="InMemoryRemoteStore"/>:
/// PROPFIND(Depth 0/1) · GET · PUT · MKCOL · MOVE · DELETE with Basic auth + a copy of the
/// chunked-UPLOAD v2 protocol (<c>uploads/{user}/{tid}/…</c>). Goal — exercise the contract and
/// the chunked path through the real HTTP stack without external dependencies.
/// </summary>
public sealed class FakeDavServer : IDisposable
{
    /// <summary>
    /// Login the server accepts.
    /// </summary>
    public const string User = "anna";

    /// <summary>
    /// App token the server accepts.
    /// </summary>
    public const string Token = "secret-app-token";

    private readonly HttpListener _listener = new();
    private readonly InMemoryRemoteStore _store = new();
    private readonly object _logGate = new();
    private readonly List<string> _requestLog = [];

    /// <summary>
    /// v2 transactions: transferId → (offset → chunk bytes).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, byte[]>> _transfers = new();

    private int _chunkPutsSeen;

    /// <summary>
    /// BaseAddress for the client HttpClient (the user's DAV root).
    /// </summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// v2 uploads root — the same "../../uploads/{user}/" shift the transport computes.
    /// </summary>
    public Uri UploadsBaseAddress { get; }

    /// <summary>
    /// If set — the Nth chunk PUT (global counter) gets a 500 and is not stored.
    /// </summary>
    public int? FailChunkPutAt { get; set; }

    /// <summary>
    /// Start the server on a random free port (both roots: files and uploads).
    /// </summary>
    public FakeDavServer()
    {
        var port = GetFreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/dav/files/{User}/");
        UploadsBaseAddress = new Uri(BaseAddress, $"../../uploads/{User}/");
        _listener.Prefixes.Add(BaseAddress.ToString());
        _listener.Prefixes.Add(UploadsBaseAddress.ToString());
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>
    /// Ready-made adapter pointed at this server; <paramref name="chunkedOptions"/> sets the v2 threshold.
    /// </summary>
    public WebDavRemoteStore CreateStore(ChunkUploadOptions? chunkedOptions = null) =>
        new(new HttpClient { BaseAddress = BaseAddress }, User, static _ => Task.FromResult<string?>(Token), chunkedOptions);

    /// <summary>
    /// Request log of "METHOD server-root-relative path" — for assertions on what traffic flew.
    /// </summary>
    public IReadOnlyList<string> RequestLog
    {
        get
        {
            lock (_logGate)
            {
                return [.. _requestLog];
            }
        }
    }

    /// <summary>
    /// Count of v2 chunk PUTs in the log (entries "PUT /…uploads/{tid}/000…").
    /// </summary>
    public int CountChunkPuts() => RequestLog.Count(e => IsChunkPut(e));

    /// <summary>
    /// Whether any request touched the uploads collection (the "small file — plain PUT" check).
    /// </summary>
    public bool TouchedUploads => RequestLog.Any(e => e.Contains("/uploads/", StringComparison.Ordinal));

    /// <summary>
    /// Number of unfinished transactions on the server (must be 0 after success/finalize).
    /// </summary>
    public int LiveTransferCount => _transfers.Count;

    /// <summary>
    /// Stages an abandoned transaction the way a killed client would leave one: chunks aligned
    /// on the 64-byte chunk bounds under the deterministic site name — the fixture mirrors the
    /// uploader's id rule (hash of "destination|size", 16 hex) so a same-name, same-size retry
    /// walks straight into the stranger's bytes.
    /// </summary>
    public void SeedAbandonedTransfer(string destination, long size, byte[] staleContent)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"{destination}|{size.ToString(CultureInfo.InvariantCulture)}"));
        var tid = Convert.ToHexString(hash).ToLowerInvariant()[..16];
        var chunks = new ConcurrentDictionary<long, byte[]>();
        for (long offset = 0; offset < staleContent.Length; offset += 64)
        {
            var length = (int)Math.Min(64, staleContent.Length - offset);
            var data = new byte[length];
            Array.Copy(staleContent, offset, data, 0, length);
            chunks[offset] = data;
        }

        _transfers[tid] = chunks;
    }

    /// <summary>
    /// Stop the server.
    /// </summary>
    public void Dispose() => _listener.Stop();

    private static bool IsChunkPut(string logEntry)
    {
        if (!logEntry.StartsWith("PUT ", StringComparison.Ordinal) || !logEntry.Contains("/uploads/", StringComparison.Ordinal))
        {
            return false;
        }

        var name = logEntry[1..].TrimEnd('/');
        var slash = name.LastIndexOf('/');
        return slash >= 0 && name[(slash + 1)..].Length == 16 && name[(slash + 1)..].All(char.IsDigit);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // Stop() breaks the wait — normal exit
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Token}"));
            if (request.Headers["Authorization"] != expected)
            {
                await RespondAsync(response, HttpStatusCode.Unauthorized).ConfigureAwait(false);
                return;
            }

            var uploads = request.Url!.AbsolutePath.StartsWith(UploadsBaseAddress.AbsolutePath, StringComparison.Ordinal);
            var root = uploads ? UploadsBaseAddress.AbsolutePath : BaseAddress.AbsolutePath;
            lock (_logGate)
            {
                _requestLog.Add($"{request.HttpMethod} {request.Url.AbsolutePath}");
            }

            var path = RemotePath.ParseDecoded(
                Uri.UnescapeDataString(request.Url.AbsolutePath)[root.Length..].TrimStart('/'));

            var handled = uploads
                ? await HandleUploadsAsync(request, response, path).ConfigureAwait(false)
                : await HandleFilesAsync(request, response, path).ConfigureAwait(false);

            if (!handled)
            {
                await RespondAsync(response, HttpStatusCode.MethodNotAllowed).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            try
            {
                await RespondAsync(response, HttpStatusCode.InternalServerError).ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // the client has already dropped — ignore
            }
        }
    }

    private async Task<bool> HandleFilesAsync(HttpListenerRequest request, HttpListenerResponse response, RemotePath path)
        => request.HttpMethod switch
        {
            "PROPFIND" => await PropfindAsync(request, response, path).ConfigureAwait(false),
            "GET" => await GetAsync(response, path).ConfigureAwait(false),
            "PUT" => await PutAsync(request, response, path).ConfigureAwait(false),
            "MKCOL" => await MkcolAsync(response, path).ConfigureAwait(false),
            "MOVE" => await MoveAsync(request, response, path).ConfigureAwait(false),
            "DELETE" => await DeleteAsync(response, path).ConfigureAwait(false),
            _ => false,
        };

    /// <summary>
    /// Chunked v2 protocol: MKCOL/chunk-PUT/PUT-end/PROPFIND/DELETE on the transaction directory.
    /// </summary>
    private async Task<bool> HandleUploadsAsync(HttpListenerRequest request, HttpListenerResponse response, RemotePath path)
    {
        // path inside the transaction: "{tid}" (collection) or "{tid}/{name}" (chunk/"end")
        var tid = path.IsRoot ? string.Empty : path.Segments[0];
        var leaf = path.Depth >= 2 ? path.Segments[1] : null;
        switch (request.HttpMethod)
        {
            case "MKCOL" when leaf is null && tid.Length > 0:
                var created = _transfers.TryAdd(tid, new ConcurrentDictionary<long, byte[]>());
                await RespondAsync(response, created ? HttpStatusCode.Created : HttpStatusCode.MethodNotAllowed)
                    .ConfigureAwait(false); // 405 = "already exists", like Nextcloud
                return true;

            case "PUT" when leaf is not null && leaf != "end":
                await PutChunkAsync(request, response, tid, leaf).ConfigureAwait(false);
                return true;

            case "PUT" when leaf == "end":
                await FinalizeTransferAsync(request, response, tid).ConfigureAwait(false);
                return true;

            case "PROPFIND" when leaf is null:
                await PropfindTransferAsync(response, tid).ConfigureAwait(false);
                return true;

            case "DELETE" when leaf is null:
                _transfers.TryRemove(tid, out _);
                await RespondAsync(response, HttpStatusCode.NoContent).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    private async Task PutChunkAsync(HttpListenerRequest request, HttpListenerResponse response, string tid, string name)
    {
        var seen = Interlocked.Increment(ref _chunkPutsSeen);
        using var body = new MemoryStream();
        await request.InputStream.CopyToAsync(body).ConfigureAwait(false);

        if (FailChunkPutAt == seen)
        {
            FailChunkPutAt = null; // one-off injected failure
            await RespondAsync(response, HttpStatusCode.InternalServerError).ConfigureAwait(false);
            return;
        }

        if (!_transfers.TryGetValue(tid, out var chunks)
            || !long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            await RespondAsync(response, HttpStatusCode.Conflict).ConfigureAwait(false);
            return;
        }

        chunks[offset] = body.ToArray();
        await RespondAsync(response, HttpStatusCode.Created).ConfigureAwait(false);
    }

    /// <summary>
    /// "PUT …/end": glue chunks by offset, verify OC-Total-Length, write into files.
    /// </summary>
    private async Task FinalizeTransferAsync(HttpListenerRequest request, HttpListenerResponse response, string tid)
    {
        if (!_transfers.TryGetValue(tid, out var chunks)
            || !long.TryParse(request.Headers["OC-Total-Length"], out var expected))
        {
            await RespondAsync(response, HttpStatusCode.NotFound).ConfigureAwait(false);
            return;
        }

        var ordered = chunks.OrderBy(static kv => kv.Key).ToList();
        var assembled = new MemoryStream();
        foreach (var (_, data) in ordered)
        {
            assembled.Write(data, 0, data.Length);
        }

        if (assembled.Length != expected)
        {
            await RespondAsync(response, HttpStatusCode.BadRequest).ConfigureAwait(false); // 400: aggregate mismatch
            return;
        }

        var destination = request.Headers["Destination"] ?? string.Empty;
        var rel = Uri.UnescapeDataString(new Uri(destination).AbsolutePath);
        var filesRoot = BaseAddress.AbsolutePath;
        if (!rel.StartsWith(filesRoot, StringComparison.Ordinal))
        {
            await RespondAsync(response, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        var target = RemotePath.ParseDecoded(rel[filesRoot.Length..].TrimStart('/'));
        assembled.Position = 0;
        var written = await _store.WriteAsync(target, assembled, CancellationToken.None).ConfigureAwait(false);
        _transfers.TryRemove(tid, out _); // the server cleans up the transaction after assembly
        if (written.IsFailure)
        {
            // a missing destination collection is the same 409 a plain PUT gets; the rest is server trouble
            await RespondAsync(response, written.Error.Kind == ErrorKind.Conflict
                ? HttpStatusCode.Conflict
                : HttpStatusCode.InternalServerError).ConfigureAwait(false);
            return;
        }

        response.Headers["ETag"] = written.Value.ETag!;
        await RespondAsync(response, HttpStatusCode.Created).ConfigureAwait(false);
    }

    private async Task PropfindTransferAsync(HttpListenerResponse response, string tid)
    {
        if (!_transfers.TryGetValue(tid, out var chunks))
        {
            await RespondAsync(response, HttpStatusCode.NotFound).ConfigureAwait(false);
            return;
        }

        var prefix = UploadsBaseAddress.AbsolutePath;
        var xml = new StringBuilder("""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">""");
        xml.Append($"""<d:response><d:href>{prefix}{tid}/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>""");
        foreach (var (offset, data) in chunks)
        {
            var name = offset.ToString("D16", CultureInfo.InvariantCulture);
            xml.Append(
                $"""<d:response><d:href>{prefix}{tid}/{name}</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>{data.Length}</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>""");
        }

        xml.Append("</d:multistatus>");
        await RespondAsync(response, (HttpStatusCode)207, xml.ToString()).ConfigureAwait(false);
    }

    private async Task<bool> PropfindAsync(HttpListenerRequest request, HttpListenerResponse response, RemotePath path)
    {
        var depth = request.Headers["Depth"];
        var self = await FindAsync(path).ConfigureAwait(false);
        if (self is null)
        {
            await RespondAsync(response, HttpStatusCode.NotFound).ConfigureAwait(false);
            return true;
        }

        var entries = new List<RemoteEntry> { self };
        if (depth == "1" && self.Kind == EntryKind.Directory)
        {
            var children = await _store.ListAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (children.IsFailure)
            {
                await RespondAsync(response, HttpStatusCode.NotFound).ConfigureAwait(false);
                return true;
            }

            entries.AddRange(children.Value);
        }

        var xml = new StringBuilder("""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">""");
        foreach (var entry in entries)
        {
            xml.Append(EntryXml(entry));
        }

        xml.Append("</d:multistatus>");
        await RespondAsync(response, (HttpStatusCode)207, xml.ToString()).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> GetAsync(HttpListenerResponse response, RemotePath path)
    {
        var read = await _store.ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (read.IsFailure)
        {
            await RespondAsync(response, HttpStatusCode.NotFound).ConfigureAwait(false);
            return true;
        }

        using var buffer = new MemoryStream();
        await read.Value.CopyToAsync(buffer).ConfigureAwait(false);
        response.ContentType = "application/octet-stream";
        await RespondAsync(response, HttpStatusCode.OK, buffer.ToArray()).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> PutAsync(HttpListenerRequest request, HttpListenerResponse response, RemotePath path)
    {
        using var body = new MemoryStream();
        await request.InputStream.CopyToAsync(body).ConfigureAwait(false);
        body.Position = 0;

        var written = await _store.WriteAsync(path, body, CancellationToken.None).ConfigureAwait(false);
        if (written.IsFailure)
        {
            await RespondAsync(response, HttpStatusCode.Conflict).ConfigureAwait(false);
            return true;
        }

        response.Headers["ETag"] = written.Value.ETag!;
        await RespondAsync(response, HttpStatusCode.Created).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> MkcolAsync(HttpListenerResponse response, RemotePath path)
    {
        if (await FindAsync(path).ConfigureAwait(false) is not null)
        {
            await RespondAsync(response, HttpStatusCode.MethodNotAllowed).ConfigureAwait(false); // "already exists"
            return true;
        }

        var created = await _store.CreateFolderAsync(path, CancellationToken.None).ConfigureAwait(false);
        await RespondAsync(response, created.IsSuccess ? HttpStatusCode.Created : HttpStatusCode.Conflict)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> MoveAsync(HttpListenerRequest request, HttpListenerResponse response, RemotePath from)
    {
        var destination = request.Headers["Destination"]
            ?? throw new InvalidOperationException("MOVE without Destination.");
        var to = RemotePath.ParseDecoded(
            Uri.UnescapeDataString(new Uri(destination).AbsolutePath)[BaseAddress.AbsolutePath.Length..].TrimStart('/'));

        var moved = await _store.MoveAsync(from, to, CancellationToken.None).ConfigureAwait(false);
        var status = moved.IsSuccess
            ? HttpStatusCode.Created
            : moved.Error.Kind == ErrorKind.NotFound ? HttpStatusCode.NotFound : (HttpStatusCode)412;
        await RespondAsync(response, status).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> DeleteAsync(HttpListenerResponse response, RemotePath path)
    {
        var deleted = await _store.DeleteAsync(path, CancellationToken.None).ConfigureAwait(false);
        await RespondAsync(response, deleted.IsSuccess
            ? HttpStatusCode.NoContent
            : deleted.Error.Kind == ErrorKind.NotFound ? HttpStatusCode.NotFound : HttpStatusCode.Conflict)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Find an entry: the root always exists; otherwise look among the parent's children
    /// (mirrors NC's 404 semantics).
    /// </summary>
    private async Task<RemoteEntry?> FindAsync(RemotePath path)
    {
        if (path.IsRoot)
        {
            return new RemoteEntry(RemotePath.Root, EntryKind.Directory, 0, ETag: "\"root\"");
        }

        var parent = await _store.ListAsync(path.Parent, CancellationToken.None).ConfigureAwait(false);
        return parent.IsSuccess ? parent.Value.FirstOrDefault(e => e.Path.Equals(path)) : null;
    }

    /// <summary>
    /// One &lt;d:response&gt; with the href escaped per-segment.
    /// </summary>
    private string EntryXml(RemoteEntry entry)
    {
        var encoded = string.Join('/', entry.Path.Segments.Select(Uri.EscapeDataString));
        var href = BaseAddress.AbsolutePath + (entry.Path.IsRoot ? string.Empty : encoded);
        if (entry.Kind == EntryKind.Directory)
        {
            href += "/";
        }

        var type = entry.Kind == EntryKind.Directory
            ? "<d:resourcetype><d:collection/></d:resourcetype>"
            : "<d:resourcetype/>";
        var etag = entry.ETag is { } tag ? $"<d:getetag>{WebUtility.HtmlEncode(tag)}</d:getetag>" : string.Empty;
        var length = entry.Kind == EntryKind.File ? $"<d:getcontentlength>{entry.Size}</d:getcontentlength>" : string.Empty;
        var modified = entry.ModifiedAt is { } at
            ? $"<d:getlastmodified>{at.ToUniversalTime():R}</d:getlastmodified>"
            : string.Empty;

        return $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>{type}{etag}{length}{modified}</d:prop>"
             + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
    }

    private static async Task RespondAsync(
        HttpListenerResponse response, HttpStatusCode status, string? body = null)
    {
        var payload = body is null ? [] : Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentLength64 = payload.Length;
        if (payload.Length > 0)
        {
            response.ContentType = "application/xml; charset=utf-8";
        }

        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.Close();
    }

    private static async Task RespondAsync(HttpListenerResponse response, HttpStatusCode status, byte[] payload)
    {
        response.StatusCode = (int)status;
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.Close();
    }

    /// <summary>
    /// Port from the free range: briefly bind a listener, then release it.
    /// </summary>
    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

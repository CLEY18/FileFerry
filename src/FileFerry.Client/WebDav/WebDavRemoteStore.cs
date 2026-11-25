using System.Net;
using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Client.WebDav;

/// <summary>
/// <see cref="IWritableRemoteStore"/> over WebDAV Nextcloud: PROPFIND Depth:0/1, GET, PUT
/// (with MKCOL parents; large files via chunked v2), MOVE, DELETE.
/// <para>
/// Failures are <see cref="Result{TValue, TError}"/> with a <see cref="SyncError"/>, not exceptions
/// Basic auth with a "live" token on every request — the secret is cached nowhere.
/// </para>
/// </summary>
public sealed class WebDavRemoteStore : IWritableRemoteStore
{
    private readonly DavTransport _transport;
    private readonly ChunkedV2Uploader? _chunked;

    /// <summary>
    /// Store over <paramref name="httpClient"/> (its <c>BaseAddress</c> is required — the user's DAV
    /// root, e.g. <c>…/remote.php/dav/files/anna/</c>); the token is requested
    /// <paramref name="tokenProvider"/> on every request. <paramref name="chunkedOptions"/> enables
    /// chunked-UPLOAD v2 above the threshold (<c>null</c> — always a plain PUT).
    /// </summary>
    public WebDavRemoteStore(
        HttpClient httpClient,
        string userName,
        Func<CancellationToken, Task<string?>> tokenProvider,
        ChunkUploadOptions? chunkedOptions = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _transport = new DavTransport(
            httpClient,
            httpClient.BaseAddress ?? throw new ArgumentException("HttpClient.BaseAddress is required."),
            userName,
            tokenProvider);
        _chunked = chunkedOptions is null ? null : new ChunkedV2Uploader(_transport, chunkedOptions);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct)
    {
        var sent = await PropfindAsync(folder, depth: "1", ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(sent.Error);
        }

        using var response = sent.Value;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // No folder — a real NotFound, not an empty list: the scanner distinguishes these states.
            return Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(
                SyncError.Of(ErrorKind.NotFound, $"The folder '{folder}' does not exist on the server (404)."));
        }

        if (!response.IsSuccessStatusCode)
        {
            return Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(
                DavTransport.ToSyncError("PROPFIND", folder.ToString(), response));
        }

        var body = await DavTransport.ReadBodyCappedAsync(response, $"PROPFIND '{folder}'", ct).ConfigureAwait(false);
        if (body.IsFailure)
        {
            return Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(body.Error);
        }

        var parsed = MultistatusParser.Parse(body.Value, _transport.BaseAddress.AbsolutePath);
        if (parsed.IsFailure)
        {
            return Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(parsed.Error);
        }

        // Depth:1 also returns the folder itself — keep only the direct children.
        var children = parsed.Value
            .Where(e => !e.Path.Equals(folder) && e.Path.Parent.Equals(folder))
            .ToList();
        return Result<IReadOnlyList<RemoteEntry>, SyncError>.Success((IReadOnlyList<RemoteEntry>)children);
    }

    /// <inheritdoc />
    public async Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct)
    {
        var sent = await _transport.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, _transport.UrlFor(file)),
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<Stream, SyncError>.Failure(sent.Error);
        }

        var response = sent.Value;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return Result<Stream, SyncError>.Failure(
                SyncError.Of(ErrorKind.NotFound, $"The file '{file}' does not exist on the server (404)."));
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return Result<Stream, SyncError>.Failure(DavTransport.ToSyncError("GET", file.ToString(), response));
        }

        // The body streams over the live connection: multi-gigabyte files never sit in memory.
        // The response lives exactly as long as its stream — closing the stream returns the
        // connection; abandoning the read abandons the body (the step fails, the next run replans).
        var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return Result<Stream, SyncError>.Success(new ResponseStream(content, response));
    }

    /// <inheritdoc />
    public async Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct)
    {
        // No ancestor re-ensuring: the plan creates folders in its own phase, a write into a
        // missing collection fails with the server's 409 (the plan's ordering is the contract).
        long? size = content.CanSeek ? content.Length - content.Position : null;
        if (_chunked is not null && _chunked.ShouldUseChunked(size))
        {
            return await FinishChunkedAsync(file, () => _chunked.UploadAsync(file, content, size, ct), ct)
                .ConfigureAwait(false);
        }

        if (_chunked is not null && size is null)
        {
            // Non-seekable stream: buffer up to "threshold+1" first; from there the uploader decides.
            return await WriteUnknownSizeAsync(file, content, ct).ConfigureAwait(false);
        }

        return await PutSimpleAsync(file, content, size, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct)
    {
        var sent = await _transport.SendAsync(
            () => new HttpRequestMessage(new HttpMethod("MKCOL"), new Uri(_transport.BaseAddress, folder.ToEncodedPath() + "/")),
            ct: ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<RemoteEntry, SyncError>.Failure(sent.Error);
        }

        using var response = sent.Value;
        // 405 = "already exists" — the port requires idempotency, so this counts as success too.
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed)
        {
            return Result<RemoteEntry, SyncError>.Success(new RemoteEntry(folder, EntryKind.Directory, 0));
        }

        return Result<RemoteEntry, SyncError>.Failure(
            DavTransport.ToSyncError("MKCOL", folder.ToString(), response));
    }

    /// <inheritdoc />
    public async Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct)
    {
        var sent = await _transport.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(new HttpMethod("MOVE"), _transport.UrlFor(from));
                request.Headers.TryAddWithoutValidation("Destination", _transport.UrlFor(to).ToString());
                request.Headers.TryAddWithoutValidation("Overwrite", "F"); // occupied target → 412, not a silent overwrite
                return request;
            },
            ct: ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<Ok, SyncError>.Failure(sent.Error);
        }

        using var response = sent.Value;
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return Result<Ok, SyncError>.Failure(SyncError.Of(
                ErrorKind.Conflict, $"MOVE '{from}' → '{to}': the target is occupied or changed ({(int)response.StatusCode})."));
        }

        return response.IsSuccessStatusCode
            ? Result<Ok, SyncError>.Success()
            : Result<Ok, SyncError>.Failure(DavTransport.ToSyncError("MOVE", from.ToString(), response));
    }

    /// <inheritdoc />
    public async Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct)
    {
        var sent = await _transport.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, _transport.UrlFor(path)),
            ct: ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<Ok, SyncError>.Failure(sent.Error);
        }

        using var response = sent.Value;
        return response.IsSuccessStatusCode
            ? Result<Ok, SyncError>.Success()
            : Result<Ok, SyncError>.Failure(DavTransport.ToSyncError("DELETE", path.ToString(), response));
    }

    /// <summary>
    /// Plain PUT (small files); the ETag comes from the response header, as Nextcloud sends it.
    /// </summary>
    private async Task<Result<RemoteEntry, SyncError>> PutSimpleAsync(
        RemotePath file,
        Stream content,
        long? size,
        CancellationToken ct)
    {
        var start = content.CanSeek ? content.Position : (long?)null;
        // A retry after 401 is possible only if the body can be rewound to the start.
        Func<bool>? rewind = start is null
            ? static () => false
            : () =>
            {
                content.Position = start.Value;
                return true;
            };
        var sent = await _transport.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, _transport.UrlFor(file))
                {
                    Content = new StreamContent(content),
                };
                if (size is not null)
                {
                    request.Content.Headers.ContentLength = size;
                }

                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            ct,
            rewind)
            .ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return Result<RemoteEntry, SyncError>.Failure(sent.Error);
        }

        using var response = sent.Value;
        if (!response.IsSuccessStatusCode)
        {
            return Result<RemoteEntry, SyncError>.Failure(DavTransport.ToSyncError("PUT", file.ToString(), response));
        }

        var etag = response.Headers.ETag?.Tag ?? FirstHeader(response, "ETag");
        return Result<RemoteEntry, SyncError>.Success(new RemoteEntry(file, EntryKind.File, size ?? 0, ETag: etag));
    }

    /// <summary>
    /// Unknown size: read "threshold+1" bytes of head into memory; it fits — PUT, otherwise — chunked without resume.
    /// </summary>
    private async Task<Result<RemoteEntry, SyncError>> WriteUnknownSizeAsync(
        RemotePath file,
        Stream content,
        CancellationToken ct)
    {
        var threshold = Math.Max(1, (int)Math.Min(_chunked!.ThresholdBytes, int.MaxValue));
        var head = new byte[threshold + 1];
        var filled = 0;
        while (filled < head.Length)
        {
            var read = await content.ReadAsync(head.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        if (filled <= threshold)
        {
            return await PutSimpleAsync(
                file, new MemoryStream(head, 0, filled, writable: false), filled, ct).ConfigureAwait(false);
        }

        return await FinishChunkedAsync(
            file, () => _chunked.UploadAsync(file, new PrefixStream(head, content), contentLength: null, ct), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Chunked path: after a successful finalize we do a Depth:0 PROPFIND, so the returned
    /// <see cref="RemoteEntry"/> carries the real server-side size and ETag, just like after a plain PUT.
    /// </summary>
    private async Task<Result<RemoteEntry, SyncError>> FinishChunkedAsync(
        RemotePath file,
        Func<Task<Result<long, SyncError>>> upload,
        CancellationToken ct)
    {
        var uploaded = await upload().ConfigureAwait(false);
        if (uploaded.IsFailure)
        {
            return Result<RemoteEntry, SyncError>.Failure(uploaded.Error);
        }

        var fallback = new RemoteEntry(file, EntryKind.File, uploaded.Value);
        var fresh = await PropfindAsync(file, depth: "0", ct).ConfigureAwait(false);
        if (fresh.IsFailure)
        {
            return Result<RemoteEntry, SyncError>.Success(fallback);
        }

        if (!fresh.Value.IsSuccessStatusCode)
        {
            fresh.Value.Dispose();
            return Result<RemoteEntry, SyncError>.Success(fallback);
        }

        using var response = fresh.Value;
        var body = await DavTransport.ReadBodyCappedAsync(response, $"PROPFIND '{file}'", ct).ConfigureAwait(false);
        var parsed = body.IsFailure
            ? Result<IReadOnlyList<RemoteEntry>, SyncError>.Failure(body.Error)
            : MultistatusParser.Parse(body.Value, _transport.BaseAddress.AbsolutePath);
        var entry = parsed.IsSuccess
            ? parsed.Value.FirstOrDefault(static e => e.Kind == EntryKind.File)
            : null;
        return Result<RemoteEntry, SyncError>.Success(entry ?? fallback);
    }

    /// <summary>
    /// PROPFIND with the properties body and a Depth header.
    /// </summary>
    private Task<Result<HttpResponseMessage, SyncError>> PropfindAsync(RemotePath path, string depth, CancellationToken ct)
        => _transport.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _transport.UrlFor(path));
                request.Headers.TryAddWithoutValidation("Depth", depth);
                request.Content = new StringContent(DavTransport.PropfindBody, Encoding.UTF8, "application/xml");
                return request;
            },
            ct: ct);

    /// <summary>
    /// First header value without structural parsing (servers' ETags sometimes come "raw").
    /// </summary>
    private static string? FirstHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Stream of "buffered head first, then the inner stream" — for a segment of unknown length.
    /// </summary>
    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _head;
        private readonly Stream _rest;
        private int _headPosition;

        /// <summary>
        /// Wrapper: reads from <paramref name="head"/> until it is exhausted, then from <paramref name="rest"/>.
        /// </summary>
        internal PrefixStream(byte[] head, Stream rest)
        {
            _head = head;
            _rest = rest;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_headPosition < _head.Length)
            {
                var taken = Math.Min(buffer.Length, _head.Length - _headPosition);
                _head.AsMemory(_headPosition, taken).CopyTo(buffer);
                _headPosition += taken;
                return taken;
            }

            return await _rest.ReadAsync(buffer, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Client.WebDav;

/// <summary>
/// Nextcloud chunked-UPLOAD v2 client (DavPlugin <c>chunkedhttpupload</c>): the file travels 
/// chunks to the temporary directory <c>uploads/{user}/{transferId}/</c> — a chunk named
/// "&lt;offset:D16&gt;" + the <c>OC-Chunk-Offset</c> header — and a one-shot <c>PUT …/end</c>
/// with <c>Destination</c> and <c>OC-Total-Length</c> aggregates them into the target file.
/// The deterministic <see cref="TransferIdFor"/> (hash of destination+size) makes a retry after
/// an abort cheaper: chunks already uploaded in the same transaction are not rewritten.
/// </summary>
internal sealed class ChunkedV2Uploader
{
    private readonly DavTransport _transport;
    private readonly ChunkUploadOptions _options;

    /// <summary>
    /// Uploader over the same transport as the main <see cref="WebDavRemoteStore"/>.
    /// </summary>
    public ChunkedV2Uploader(DavTransport transport, ChunkUploadOptions options)
    {
        _transport = transport;
        _options = options;
    }

    /// <summary>
    /// The chunked-path threshold in bytes — the store needs it to buffer non-seekable streams.
    /// </summary>
    public long ThresholdBytes => _options.ChunkedThresholdBytes;

    /// <summary>
    /// Whether a file of this size should take the chunked path (unknown length — no).
    /// </summary>
    public bool ShouldUseChunked(long? size) => ThresholdBytes > 0 && size > ThresholdBytes;

    /// <summary>
    /// Uploads <paramref name="content"/> to <paramref name="destination"/>; on success, the actual
    /// number of bytes uploaded. <paramref name="contentLength"/> (when known) enables resume:
    /// chunks are size-aligned, so a retry of a same-size file lines up with the bounds of the
    /// previous transaction.
    /// </summary>
    public async Task<Result<long, SyncError>> UploadAsync(
        RemotePath destination,
        Stream content,
        long? contentLength,
        CancellationToken ct)
    {
        var transferId = TransferIdFor(destination, contentLength);

        var directoryError = await EnsureTransferDirectoryAsync(transferId, ct).ConfigureAwait(false);
        if (directoryError is not null)
        {
            return Result<long, SyncError>.Failure(directoryError);
        }

        var uploadedOffsets = contentLength is > 0
            ? await ListUploadedOffsetsAsync(transferId, ct).ConfigureAwait(false)
            : new HashSet<long>();

        var buffer = new byte[_options.ChunkSizeBytes];
        long offset = 0;
        while (contentLength is null || offset < contentLength.Value)
        {
            var filled = await ReadFullAsync(content, buffer, ct).ConfigureAwait(false);
            if (filled == 0)
            {
                break; // EOF earlier than expected — the consistency check is below
            }

            if (uploadedOffsets.Contains(offset))
            {
                offset += filled; // chunk already on the server from a previous attempt — skip without traffic
                continue;
            }

            var putError = await PutChunkAsync(transferId, offset, buffer.AsMemory(0, filled), ct)
                .ConfigureAwait(false);
            if (putError is not null)
            {
                return Result<long, SyncError>.Failure(putError);
            }

            offset += filled;
        }

        if (contentLength is long declared && offset != declared)
        {
            // The file changed/was cut short during reading — the server-side state is untrustworthy, leave the target alone.
            return Result<long, SyncError>.Failure(SyncError.Of(
                ErrorKind.Conflict,
                $"Stream '{destination}' yielded {offset} bytes instead of the declared {declared}."));
        }

        var finalizeError = await FinalizeAsync(transferId, destination, offset, ct).ConfigureAwait(false);
        if (finalizeError is not null)
        {
            await AbortQuietlyAsync(transferId).ConfigureAwait(false);
            return Result<long, SyncError>.Failure(finalizeError);
        }

        return Result<long, SyncError>.Success(offset);
    }

    /// <summary>
    /// Transaction id: the same (destination, size) yields the same site with its already-uploaded
    /// chunks; unknown length — random (nothing to resume).
    /// </summary>
    public static string TransferIdFor(RemotePath destination, long? contentLength)
    {
        if (contentLength is null)
        {
            return Guid.NewGuid().ToString("N");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{destination}|{contentLength.Value.ToString(CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    /// <summary>
    /// Creates the transaction directory; 405 = already exists (retry after an abort) — also fine.
    /// </summary>
    private async Task<SyncError?> EnsureTransferDirectoryAsync(string transferId, CancellationToken ct)
    {
        var url = _transport.UploadsUrlFor(transferId + "/");
        var sent = await _transport.SendAsync(
            () => new HttpRequestMessage(new HttpMethod("MKCOL"), url), ct: ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        return response.StatusCode is HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed
            ? null
            : DavTransport.ToSyncError("MKCOL uploads", transferId, response);
    }

    /// <summary>
    /// Offsets of already-uploaded chunks, from a PROPFIND Depth:1 over the transaction directory.
    /// </summary>
    private async Task<HashSet<long>> ListUploadedOffsetsAsync(string transferId, CancellationToken ct)
    {
        var url = _transport.UploadsUrlFor(transferId + "/");
        var sent = await _transport.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
                request.Headers.TryAddWithoutValidation("Depth", "1");
                request.Content = new StringContent(DavTransport.PropfindBody, Encoding.UTF8, "application/xml");
                return request;
            },
            ct: ct).ConfigureAwait(false);

        if (sent.IsFailure)
        {
            return new HashSet<long>(); // the network is down — no resume, everything uploads from scratch
        }

        if (sent.Value.StatusCode != (HttpStatusCode)207)
        {
            sent.Value.Dispose();
            return new HashSet<long>(); // no transaction yet (404) — everything uploads from scratch
        }

        using var response = sent.Value;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = MultistatusParser.Parse(body, _transport.UploadsBaseAddress.AbsolutePath);
        if (parsed.IsFailure)
        {
            return new HashSet<long>();
        }

        var offsets = new HashSet<long>();
        foreach (var entry in parsed.Value)
        {
            // A chunk is a second-level file "{transferId}/{offset:D16}"; the transaction's own entry — skip.
            if (entry.Path.Depth == 2 && entry.Path.Parent.Name == transferId
                && long.TryParse(entry.Path.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            {
                offsets.Add(offset);
            }
        }

        return offsets;
    }

    /// <summary>
    /// Uploads one chunk at <paramref name="offset"/> (name = the offset in D16 format).
    /// </summary>
    private async Task<SyncError?> PutChunkAsync(string transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var url = _transport.UploadsUrlFor(
            $"{transferId}/{offset.ToString("D16", CultureInfo.InvariantCulture)}");
        var payload = data.ToArray();
        var sent = await _transport.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(payload) };
                request.Content.Headers.TryAddWithoutValidation(
                    "OC-Chunk-Offset",
                    offset.ToString(CultureInfo.InvariantCulture));
                return request;
            },
            ct: ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        return response.IsSuccessStatusCode
            ? null
            : DavTransport.ToSyncError("PUT chunk", url.ToString(), response);
    }

    /// <summary>
    /// Finalize: <c>PUT …/end</c> tells the server to aggregate the chunks into the target file.
    /// </summary>
    private async Task<SyncError?> FinalizeAsync(string transferId, RemotePath destination, long totalLength, CancellationToken ct)
    {
        var url = _transport.UploadsUrlFor(transferId + "/end");
        var destinationUrl = _transport.UrlFor(destination).ToString();
        var sent = await _transport.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.TryAddWithoutValidation("Destination", destinationUrl);
                request.Content = new StringContent(string.Empty);
                request.Content.Headers.TryAddWithoutValidation(
                    "OC-Total-Length",
                    totalLength.ToString(CultureInfo.InvariantCulture));
                return request;
            },
            ct: ct).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        return response.IsSuccessStatusCode
            ? null
            : DavTransport.ToSyncError("PUT end", destination.ToString(), response);
    }

    /// <summary>
    /// Removes the transaction directory after a failed finalize (error swallowed — best-effort).
    /// </summary>
    private async Task AbortQuietlyAsync(string transferId)
    {
        try
        {
            var sent = await _transport.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, _transport.UploadsUrlFor(transferId + "/")),
                ct: CancellationToken.None).ConfigureAwait(false);
            if (sent.IsSuccess)
            {
                sent.Value.Dispose();
            }
        }
        catch (Exception)
        {
            // The next attempt with the same transferId will recreate/re-stage the site.
        }
    }

    /// <summary>
    /// Reads the stream into the buffer until full or EOF; returns the number of bytes actually read.
    /// </summary>
    private static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

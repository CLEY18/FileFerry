namespace FileFerry.Client.WebDav;

/// <summary>
/// chunked-UPLOAD v2 parameters. The thresholds in <see cref="Defaults"/> match
/// typical Nextcloud limits; the server may change the chunk maximum via the <c>OC-Chunk-Size</c>
/// header — we honour it.
/// </summary>
public sealed record ChunkUploadOptions
{
    /// <summary>
    /// The Nextcloud-recommended minimum chunk (10 MiB).
    /// </summary>
    public static readonly ChunkUploadOptions Defaults = new();

    /// <summary>
    /// A file larger than this goes through the chunked path; a smaller one uses a plain PUT.
    /// &lt;= 0 disables the chunked path.
    /// </summary>
    public long ChunkedThresholdBytes { get; init; } = 10L * 1024 * 1024;

    /// <summary>
    /// Size of one chunk when slicing the stream (chunk-size-aligned offsets — a protocol condition).
    /// </summary>
    public int ChunkSizeBytes { get; init; } = 10 * 1024 * 1024;
}

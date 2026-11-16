using FileFerry.Client.WebDav;
using FileFerry.ContractTests;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// Chunked-UPLOAD v2 tests against <see cref="FakeDavServer"/> over real HTTP: small files
/// never touch uploads; large files — chunks + assembly with round-trip; a retry after an
/// interruption does not rewrite already-uploaded chunks; length consistency; non-seekable streams.
/// Thresholds are shrunk (160/64 bytes) to test protocol correctness, not 10 MiB.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChunkedUploadTests : IDisposable
{
    private static readonly ChunkUploadOptions TinyChunks = new()
    {
        ChunkedThresholdBytes = 160,
        ChunkSizeBytes = 64,
    };

    private readonly FakeDavServer _server = new();

    /// <summary>
    /// Tear down the HTTP server.
    /// </summary>
    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Small file — plain PUT: nothing ever flew to the uploads collection.
    /// </summary>
    [Fact]
    public async Task Small_file_uses_plain_put_and_never_touches_uploads()
    {
        var store = _server.CreateStore(TinyChunks);
        var payload = RandomBytes(50);

        var written = await store.WriteAsync(RemotePath.Parse("small.bin"), new MemoryStream(payload), CT);

        written.AssertOk();
        Assert.False(_server.TouchedUploads);
        await AssertFileBytesAsync(store, "small.bin", payload);
    }

    /// <summary>
    /// File past the threshold — chunks + "end": content round-trips, no transactions left behind.
    /// </summary>
    [Fact]
    public async Task Large_file_chunks_then_assembles_exactly()
    {
        var store = _server.CreateStore(TinyChunks);
        var payload = RandomBytes(500); // 8 chunks of 64 (the last one — 52)

        var written = await store.WriteAsync(RemotePath.Parse("big/big.bin"), new MemoryStream(payload), CT);

        written.AssertOk();
        Assert.Equal(payload.Length, written.Value.Size);
        Assert.True(_server.TouchedUploads);
        await AssertFileBytesAsync(store, "big/big.bin", payload);
        Assert.Equal(0, _server.LiveTransferCount); // the server removed the transaction directory after finalize
    }

    /// <summary>
    /// Interrupted on chunk 3 → retrying the same file does not rewrite the first two
    /// (deterministic transfer id).
    /// </summary>
    [Fact]
    public async Task Retry_after_interrupt_reuses_uploaded_chunks()
    {
        var store = _server.CreateStore(TinyChunks);
        var payload = RandomBytes(500);
        _server.FailChunkPutAt = 3; // 500 on the third chunk PUT — the first run fails

        var first = await store.WriteAsync(RemotePath.Parse("resume.bin"), new MemoryStream(payload), CT);
        Assert.True(first.IsFailure);

        _server.FailChunkPutAt = null;
        var second = await store.WriteAsync(RemotePath.Parse("resume.bin"), new MemoryStream(payload), CT);
        second.AssertOk();

        // The file is 8 chunks; the first run did 3 PUTs (2 held). The resumed retry writes
        // the remaining 6, not all 8: 9 PUTs total vs 11 without reuse.
        Assert.Equal(9, _server.CountChunkPuts());
        await AssertFileBytesAsync(store, "resume.bin", payload);
        Assert.Equal(0, _server.LiveTransferCount);
    }

    /// <summary>
    /// The stream "lied" about its length (ended early) — failure without writing the target and without garbage.
    /// </summary>
    [Fact]
    public async Task Truncated_stream_fails_before_finalize_and_cleans_nothing_at_target()
    {
        var store = _server.CreateStore(TinyChunks);
        var payload = RandomBytes(500);

        var result = await store.WriteAsync(
            RemotePath.Parse("liar.bin"),
            new LengthLyingStream(payload, declaredExtra: 100),
            CT);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind); // dishonest stream state — conflict, not network
        var read = await store.ReadAsync(RemotePath.Parse("liar.bin"), CT);
        Assert.True(read.IsFailure); // the target was not created
    }

    /// <summary>
    /// Non-seekable large stream (length unknowable) — chunked without resume, round-trip ok.
    /// </summary>
    [Fact]
    public async Task Non_seekable_large_stream_round_trips_via_chunked()
    {
        var store = _server.CreateStore(TinyChunks);
        var payload = RandomBytes(400);

        var written = await store.WriteAsync(
            RemotePath.Parse("streamed.bin"), new ForwardOnlyStream(payload), CT);

        written.AssertOk();
        Assert.True(_server.TouchedUploads);
        await AssertFileBytesAsync(store, "streamed.bin", payload);
        Assert.Equal(0, _server.LiveTransferCount);
    }

    /// <summary>
    /// Small non-seekable stream — buffered and plain PUT, uploads not involved.
    /// </summary>
    [Fact]
    public async Task Non_seekable_small_stream_spools_to_plain_put()
    {
        var store = _server.CreateStore(TinyChunks);
        var payload = RandomBytes(80);

        var written = await store.WriteAsync(
            RemotePath.Parse("tiny-stream.bin"), new ForwardOnlyStream(payload), CT);

        written.AssertOk();
        Assert.False(_server.TouchedUploads);
        await AssertFileBytesAsync(store, "tiny-stream.bin", payload);
    }

    private static readonly CancellationToken CT = CancellationToken.None;

    /// <summary>
    /// Deterministic "randomness" — so failures repeat.
    /// </summary>
    private static byte[] RandomBytes(int count)
    {
        var buffer = new byte[count];
        new Random(42).NextBytes(buffer);
        return buffer;
    }

    /// <summary>
    /// Read the file back from the server and compare bytes.
    /// </summary>
    private static async Task AssertFileBytesAsync(
        FileFerry.Core.Ports.IWritableRemoteStore store, string path, byte[] expected)
    {
        var read = await store.ReadAsync(RemotePath.Parse(path), CT);
        read.AssertOk();
        using var stream = read.Value;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, CT);
        Assert.Equal(expected, buffer.ToArray());
    }

    /// <summary>
    /// Stream without seeking: reads forward only, like a network/piped source.
    /// </summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        /// <summary>
        /// Wrapper over <paramref name="payload"/> with CanSeek == false.
        /// </summary>
        internal ForwardOnlyStream(byte[] payload) => _inner = new MemoryStream(payload);

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
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

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
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// "Liar" stream: <see cref="Length"/> is exaggerated — models a read interrupted mid-write.
    /// </summary>
    private sealed class LengthLyingStream : Stream
    {
        private readonly MemoryStream _inner;

        /// <summary>
        /// Wrapper declaring <paramref name="declaredExtra"/> bytes more than the real content.
        /// </summary>
        internal LengthLyingStream(byte[] payload, int declaredExtra)
            : this(new MemoryStream(payload), declaredExtra)
        {
        }

        private LengthLyingStream(MemoryStream inner, int declaredExtra)
        {
            _inner = inner;
            Length = inner.Length + declaredExtra;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length { get; }

        /// <inheritdoc />
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override void Flush() => _inner.Flush();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

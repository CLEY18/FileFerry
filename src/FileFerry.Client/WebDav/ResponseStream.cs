namespace FileFerry.Client.WebDav;

/// <summary>
/// A response body handed to the caller as the response's owner: reads stream straight off the
/// connection, and disposing the stream releases the response (and the connection) exactly once.
/// Deliberately not seekable — the port contract is forward streaming, never full buffering.
/// </summary>
internal sealed class ResponseStream(Stream body, HttpResponseMessage response) : Stream
{
    /// <inheritdoc />
    public override bool CanRead => body.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => body.Length;

    /// <inheritdoc />
    public override long Position { get => body.Position; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Flush() => body.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> destination) => body.Read(destination);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        body.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override int ReadByte() => body.ReadByte();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            body.Dispose();
            response.Dispose(); // the connection returns to the pool once the reader is done
        }

        base.Dispose(disposing);
    }
}

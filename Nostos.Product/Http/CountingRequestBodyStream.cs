namespace Nostos.Product.Http;

/// <summary>
/// Counts bytes actually read from a request body (#1266). A truncated upload
/// is exactly the case where the declared Content-Length is known and the
/// bytes that arrived are not, so the warning log needs this counter rather
/// than the header.
/// </summary>
public sealed class CountingRequestBodyStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        BytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

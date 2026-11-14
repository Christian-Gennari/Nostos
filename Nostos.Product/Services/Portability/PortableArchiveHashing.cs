using System.Security.Cryptography;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// The one SHA-256 implementation for every portable byte stream: archive
/// payloads, manifest entries, pinned media, staged files and recovery
/// payloads. All digests in the interchange format are lowercase hexadecimal
/// of the SHA-256 of the exact bytes, so every producer shares this code.
/// </summary>
internal static class PortableArchiveHashing
{
    /// <summary>Bounded copy/hash buffer; no whole-file buffering is ever used.</summary>
    internal const int StreamBufferBytes = 128 * 1024;

    /// <summary>
    /// Hashes an already-opened readable stream with a bounded buffer, optionally
    /// refusing to read past <paramref name="maxBytes"/>. Exceeding the limit
    /// fails with <c>entry_too_large</c> before the extra bytes are hashed.
    /// </summary>
    internal static async Task<(long Length, string Sha256)> HashStreamAsync(
        Stream source,
        CancellationToken cancellationToken,
        long maxBytes = long.MaxValue,
        string limitMessage = "The payload exceeds its declared length.")
    {
        ArgumentNullException.ThrowIfNull(source);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferBytes];
        long total = 0;

        while (true)
        {
            var read = await source
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total = checked(total + read);
            if (total > maxBytes)
                throw new PortableArchiveException("entry_too_large", limitMessage);

            hash.AppendData(buffer, 0, read);
        }

        return (total, Finalize(hash));
    }

    internal static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Finalize(IncrementalHash hash) =>
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

/// <summary>
/// Serializes relational JSON straight into a ZIP entry while counting and
/// hashing the exact bytes that will be stored. Only asynchronous writes are
/// forwarded; the archived entry never needs a temporary file. Exceeding
/// <c>maxBytes</c> fails before more than the limit is written. A recovery
/// payload uses the same wrapper to digest the bytes it writes to its caller's
/// stream.
/// </summary>
internal sealed class HashingWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly long _maxBytes;
    private readonly string _limitCode;
    private readonly string _limitMessage;
    private readonly IncrementalHash _hash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;
    private bool _completed;
    private bool _disposed;

    internal HashingWriteStream(
        Stream destination,
        long maxBytes = long.MaxValue,
        string limitCode = "entry_too_large",
        string limitMessage = "The payload exceeds its declared length.")
    {
        ArgumentNullException.ThrowIfNull(destination);
        _destination = destination;
        _maxBytes = maxBytes;
        _limitCode = limitCode;
        _limitMessage = limitMessage;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_completed;
    public override long Length => _length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public (long Length, string Sha256) Complete()
    {
        if (_completed)
            throw new InvalidOperationException("The hashing stream is already complete.");
        if (_disposed)
            throw new ObjectDisposedException(nameof(HashingWriteStream));

        _completed = true;
        return (
            _length,
            Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant());
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Observe(buffer.Span);
        await _destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override void WriteByte(byte value) =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _destination.FlushAsync(cancellationToken);

    public override void Flush() =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override ValueTask DisposeAsync()
    {
        Dispose(disposing: true);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }

    private void Observe(ReadOnlySpan<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HashingWriteStream));
        if (_completed)
            throw new InvalidOperationException("The hashing stream is already complete.");

        var next = checked(_length + buffer.Length);
        if (next > _maxBytes)
            throw new PortableArchiveException(_limitCode, _limitMessage);

        _hash.AppendData(buffer);
        _length = next;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();
}

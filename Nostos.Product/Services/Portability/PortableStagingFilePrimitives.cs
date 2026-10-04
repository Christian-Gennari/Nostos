using System.Security.Cryptography;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Lifecycle status of one item held by a file-backed staging provider. A discarded
/// item is invisible to readers and may be replaced by a later write.
/// </summary>
internal enum PortableStagingItemStatus
{
    Writing,
    Completed,
    Discarded,
}

/// <summary>
/// The identity of one staged media entry within a staging area. Two descriptors
/// with the same book, kind, and archive path are the same item.
/// </summary>
internal readonly record struct PortableStagingMediaIdentity(Guid BookId, string Kind, string Path);

/// <summary>
/// Write state for one staged item shared by the file-backed staging providers:
/// the scratch file currently being written, the final generated path it is
/// published to, the byte limit derived from the descriptor bound when the write was
/// opened, the running length, and the incremental SHA-256 of everything written.
/// </summary>
/// <remarks>
/// The incremental hash means completion never re-reads the staged bytes: it
/// compares the running length and hash against the bound descriptor after the
/// writer is closed. The same state object is never shared between two writers; the
/// owning provider serialises every transition under its staging-area gate.
/// </remarks>
internal sealed class PortableStagingWriteState
{
    internal required string TempPath { get; init; }

    internal required string FinalPath { get; init; }

    internal required long Limit { get; init; }

    internal required long Written { get; set; }

    internal PortableStagingItemStatus Status { get; set; } = PortableStagingItemStatus.Writing;

    internal FileStream? Stream { get; set; }

    internal IncrementalHash? Hash { get; set; }

    internal static PortableStagingWriteState Open(
        FileStream stream,
        string tempPath,
        string finalPath,
        long limit)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return new PortableStagingWriteState
        {
            TempPath = tempPath,
            FinalPath = finalPath,
            Limit = limit,
            Written = 0,
            Stream = stream,
            Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
        };
    }

    internal static PortableStagingWriteState Completed(string finalPath, long length) =>
        new()
        {
            TempPath = finalPath,
            FinalPath = finalPath,
            Limit = long.MaxValue,
            Written = length,
            Status = PortableStagingItemStatus.Completed,
        };

    /// <summary>
    /// Closes the writer and its hash without changing the item status. Best-effort:
    /// a failure while disposing must never replace the caller's original error.
    /// </summary>
    internal void DisposeWriter()
    {
        var stream = Stream;
        Stream = null;
        stream?.Dispose();
        Hash?.Dispose();
        Hash = null;
    }
}

/// <summary>
/// Completion verification for one staged item. Operates on the running state
/// produced while the item was written, so no second pass over the staged bytes is
/// needed and no whole-file buffer is allocated.
/// </summary>
internal static class PortableStagingSealing
{
    /// <summary>
    /// Closes the writer, compares the running length and SHA-256 against the
    /// descriptor bound when the write was opened, and returns whether they match.
    /// When <paramref name="flushToDisk"/> is true the writer is flushed with
    /// <c>Flush(true)</c> before the handle closes, so the bytes are on disk before
    /// the caller publishes the item by rename.
    /// </summary>
    internal static async Task<bool> SealAsync(
        PortableStagingWriteState state,
        long expectedLength,
        string expectedSha256,
        bool flushToDisk)
    {
        var stream = state.Stream;
        state.Stream = null;
        if (stream is not null)
        {
            try
            {
                await stream.FlushAsync().ConfigureAwait(false);
                if (flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        var actualSha256 = Convert.ToHexString(
            state.Hash!.GetHashAndReset()).ToLowerInvariant();

        return state.Written == expectedLength
            && string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Shared hashing and reference helpers for the file-backed staging providers.
/// </summary>
internal static class PortableStagingFilePrimitives
{
    /// <summary>Bounded copy/hash buffer; no whole-file buffering is ever used.</summary>
    internal const int StreamBufferBytes = 128 * 1024;

    internal const int StateBufferBytes = 64 * 1024;

    internal static async Task<(long Length, string Sha256)> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferBytes];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total = checked(total + read);
            hash.AppendData(buffer, 0, read);
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>
    /// Validates a staged media reference against the strict opaque-token grammar
    /// before it is ever combined with a filesystem path. The single value shape
    /// that both providers generate is lowercase hexadecimal, so any separator,
    /// parent segment, rooted path, uppercase value, or overlong token is rejected.
    /// </summary>
    internal static string ValidateReference(string? value)
    {
        if (value is null || !Transfers.TransferPathResolver.IsValidOpaqueToken(value))
        {
            throw new PortableStagingException(
                PortableStagingException.InvalidReferenceCode,
                "The staged reference is not a valid opaque token.");
        }

        return value;
    }

    /// <summary>
    /// Generates a fresh opaque media reference: 32 random bytes encoded as
    /// lowercase hexadecimal. It is never derived from an archive entry name, path,
    /// filename, header, or any other client input.
    /// </summary>
    internal static string NewReference(Func<string, bool> isTaken)
    {
        string reference;
        do
        {
            reference = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }
        while (isTaken(reference));

        return reference;
    }

    /// <summary>
    /// Removes a scratch or published file. Cleanup only: a failed delete must never
    /// replace the caller's original failure.
    /// </summary>
    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup only.
        }
    }

    /// <summary>
    /// Removes a staging area tree. Cleanup only: a failed delete must never replace
    /// the caller's original failure.
    /// </summary>
    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup only.
        }
    }

    internal static async ValueTask DiscardIfWritingAsync(
        SemaphoreSlim gate,
        PortableStagingWriteState state,
        Action discard)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state.Status == PortableStagingItemStatus.Writing)
                discard();
        }
        finally
        {
            gate.Release();
        }
    }

    internal static PortableStagingException NotFound(string message) =>
        new(PortableStagingException.NotFoundCode, message);

    internal static PortableStagingException IntegrityMismatch(string message) =>
        new(PortableStagingException.IntegrityMismatchCode, message);

    internal static PortableStagingException Conflict(string message) =>
        new(PortableStagingException.ConflictCode, message);
}

/// <summary>
/// Sequential writer for one staged item shared by the file-backed staging
/// providers. All transitions are guarded by the owning staging-area gate; the
/// owning item is discarded when the stream is disposed before completion. The
/// provider may enforce the item's maximum length while it is written, and a write
/// that overflows the limit discards the item immediately.
/// </summary>
internal sealed class PortableStagingWriteStream : Stream
{
    private readonly SemaphoreSlim _gate;
    private readonly PortableStagingWriteState _state;
    private readonly Func<bool> _isAreaDeleted;
    private readonly Action _discard;
    private readonly Action? _beforeWrite;
    private bool _disposed;

    internal PortableStagingWriteStream(
        SemaphoreSlim gate,
        PortableStagingWriteState state,
        Func<bool> isAreaDeleted,
        Action discard,
        Action? beforeWrite = null)
    {
        _gate = gate;
        _state = state;
        _isAreaDeleted = isAreaDeleted;
        _discard = discard;
        _beforeWrite = beforeWrite;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _beforeWrite?.Invoke();

        _gate.Wait();
        try
        {
            EnsureWritable(buffer.Length);
            _state.Stream!.Write(buffer);
            _state.Hash!.AppendData(buffer);
            _state.Written += buffer.Length;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _beforeWrite?.Invoke();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWritable(buffer.Length);
            await _state.Stream!
                .WriteAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            _state.Hash!.AppendData(buffer.Span);
            _state.Written += buffer.Length;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await PortableStagingFilePrimitives
            .DiscardIfWritingAsync(_gate, _state, _discard)
            .ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;
        if (disposing)
        {
            _gate.Wait();
            try
            {
                if (_state.Status == PortableStagingItemStatus.Writing)
                    _discard();
            }
            finally
            {
                _gate.Release();
            }
        }

        base.Dispose(disposing);
    }

    private void EnsureWritable(int length)
    {
        if (_isAreaDeleted())
            throw PortableStagingFilePrimitives.NotFound("The staging area has been deleted.");

        if (_state.Status != PortableStagingItemStatus.Writing)
        {
            throw PortableStagingFilePrimitives.Conflict(
                "The write handle has been completed, discarded, or closed.");
        }

        if (_state.Written + length > _state.Limit)
        {
            _discard();
            throw new PortableStagingException(
                PortableStagingException.LimitExceededCode,
                $"The staged item exceeds its {_state.Limit}-byte limit.");
        }
    }
}

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Single-writer stream: captures native ZIP finalization in bounded memory. Dispose the
/// archive with leaveOpen, then CompleteAsync. No synchronous method touches the destination.
/// </summary>
internal sealed class BoundedSynchronousCaptureSink : Stream
{
    private readonly Stream _destination;
    private readonly PortableBufferLease _pending;
    private readonly bool _leaveOpen;
    private bool _disposed;
    private bool _completed;
    private bool _destinationDisposeStarted;

    public BoundedSynchronousCaptureSink(
        Stream destination, PortableArchiveBufferBudget budget, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(budget);
        if (!destination.CanWrite)
            throw new ArgumentException("The ZIP destination must be writable.", nameof(destination));
        _destination = destination;
        _leaveOpen = leaveOpen;
        _pending = budget.Rent(PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes);
    }

    public long BytesWritten { get; private set; }
    public int PendingSynchronousBytes { get; private set; }
    public int SynchronousHighWaterBytes { get; private set; }
    public int MaxSingleSynchronousWriteBytes { get; private set; }
    public bool IsPoisoned { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_completed && !IsPoisoned;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        if (buffer.Length > _pending.Length - PendingSynchronousBytes)
        {
            IsPoisoned = true;
            throw new PortableArchiveException("zip_sync_buffer_exceeded", "ZIP exceeded the bounded synchronous-write buffer.");
        }
        buffer.CopyTo(_pending.Memory.Span[PendingSynchronousBytes..]);
        PendingSynchronousBytes += buffer.Length;
        SynchronousHighWaterBytes = Math.Max(SynchronousHighWaterBytes, PendingSynchronousBytes);
        MaxSingleSynchronousWriteBytes = Math.Max(MaxSingleSynchronousWriteBytes, buffer.Length);
    }

    // Native ZIP may flush during finalization; this is only a memory boundary.
    public override void Flush() => EnsureUsable();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
            await _destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten = checked(BytesWritten + buffer.Length);
        }
        catch { IsPoisoned = true; throw; }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureUsable();
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
            await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { IsPoisoned = true; throw; }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (_completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PendingSynchronousBytes == 0)
            return;
        await _destination.WriteAsync(_pending.Memory[..PendingSynchronousBytes], cancellationToken).ConfigureAwait(false);
        BytesWritten = checked(BytesWritten + PendingSynchronousBytes);
        PendingSynchronousBytes = 0;
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_disposed && !IsPoisoned)
                await CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            _pending.Dispose();
            if (!_leaveOpen && !_destinationDisposeStarted)
            {
                _destinationDisposeStarted = true;
                try { await _destination.DisposeAsync().ConfigureAwait(false); }
                catch { IsPoisoned = true; throw; }
            }
            GC.SuppressFinalize(this);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
            return;
        _disposed = true;
        _pending.Dispose();
        // Even owned destinations are disposed only through DisposeAsync: Dispose must do no IO.
        if (PendingSynchronousBytes != 0)
        {
            IsPoisoned = true;
            throw new PortableArchiveException("zip_sync_dispose_pending", "Synchronous ZIP sink disposal left undrained output.");
        }
        base.Dispose(disposing);
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPoisoned)
            throw new PortableArchiveException("zip_sink_poisoned", "The ZIP sink failed and cannot be resumed.");
    }
    private void EnsureWritable()
    {
        EnsureUsable();
        if (_completed)
            throw new InvalidOperationException("The ZIP sink is already complete.");
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

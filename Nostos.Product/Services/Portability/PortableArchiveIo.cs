using Microsoft.Win32.SafeHandles;

namespace Nostos.Backend.Services.Portability;

public interface IPortableArchiveSource : IAsyncDisposable
{
    long Length { get; }

    ValueTask<int> ReadAtAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);
}

public interface IPortableArchiveSink : IAsyncDisposable
{
    ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default);
}

public sealed class FilePortableArchiveSource : IPortableArchiveSource
{
    private readonly SafeFileHandle _handle;
    private int _disposed;

    public FilePortableArchiveSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            Length = RandomAccess.GetLength(_handle);
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public long Length { get; }

    public async ValueTask<int> ReadAtAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ValidateOffset(offset, Length);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var requestedBytes = GetReadableBytes(offset, buffer.Length, Length);
        if (requestedBytes == 0)
            return 0;

        var bytesRead = await RandomAccess.ReadAsync(
            _handle,
            buffer[..requestedBytes],
            offset,
            cancellationToken).ConfigureAwait(false);

        if (bytesRead == 0)
            throw CreateUnexpectedEndException(offset, Length);

        return bytesRead;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _handle.Dispose();

        return ValueTask.CompletedTask;
    }

    internal static void ValidateOffset(long offset, long length)
    {
        if (offset < 0 || offset > length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    internal static int GetReadableBytes(long offset, int requestedBytes, long length) =>
        (int)Math.Min(requestedBytes, length - offset);

    internal static PortableArchiveException CreateUnexpectedEndException(long offset, long length) =>
        new(
            "archive_source_truncated",
            $"The archive source ended at byte {offset} before its declared length of {length} bytes.");
}

public sealed class RangePortableArchiveSource : IPortableArchiveSource
{
    private readonly Func<long, Memory<byte>, CancellationToken, ValueTask<int>> _readAtAsync;
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    /// <summary>The read delegate must not re-enter a range cache that is calling it.</summary>
    public RangePortableArchiveSource(
        long length,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> readAtAsync,
        Func<ValueTask>? disposeAsync = null)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        ArgumentNullException.ThrowIfNull(readAtAsync);
        Length = length;
        _readAtAsync = readAtAsync;
        _disposeAsync = disposeAsync;
    }

    public long Length { get; }

    public async ValueTask<int> ReadAtAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        FilePortableArchiveSource.ValidateOffset(offset, Length);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var requestedBytes = FilePortableArchiveSource.GetReadableBytes(offset, buffer.Length, Length);
        if (requestedBytes == 0)
            return 0;

        var bytesRead = await _readAtAsync(
            offset,
            buffer[..requestedBytes],
            cancellationToken).ConfigureAwait(false);

        if (bytesRead < 0 || bytesRead > requestedBytes)
            throw new InvalidDataException("The archive range reader returned an invalid byte count.");

        if (bytesRead == 0)
            throw FilePortableArchiveSource.CreateUnexpectedEndException(offset, Length);

        return bytesRead;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _disposeAsync is not null)
            await _disposeAsync().ConfigureAwait(false);
    }
}

public sealed class StreamPortableArchiveSink : IPortableArchiveSink
{
    private readonly object _gate = new();
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _writerOpened;
    private bool _disposed;
    private Task? _streamDisposeTask;
    private Task? _disposeTask;

    public StreamPortableArchiveSink(Stream stream, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("The archive sink stream must be writable.", nameof(stream));

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Opens an async-only writer. Use DisposeAsync on the writer or sink to complete
    /// owned-stream disposal; synchronous writer disposal only closes the writer.
    /// </summary>
    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writerOpened)
                throw new InvalidOperationException("An archive sink can have only one writer.");

            _writerOpened = true;
            return ValueTask.FromResult<Stream>(new WriterStream(this, _stream));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposed = true;
            _disposeTask = _leaveOpen ? Task.CompletedTask : GetStreamDisposeTask();
            return new ValueTask(_disposeTask);
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _disposed;
        }
    }

    private ValueTask CompleteWriterAsync()
    {
        if (_leaveOpen)
            return ValueTask.CompletedTask;

        lock (_gate)
        {
            _disposeTask ??= GetStreamDisposeTask();
            _disposed = true;
            return new ValueTask(_disposeTask);
        }
    }

    private Task GetStreamDisposeTask()
    {
        _streamDisposeTask ??= _stream.DisposeAsync().AsTask();
        return _streamDisposeTask;
    }

    private sealed class WriterStream(StreamPortableArchiveSink owner, Stream stream) : Stream
    {
        private int _writerDisposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => Volatile.Read(ref _writerDisposed) == 0 && !owner.IsDisposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException("Use FlushAsync for archive output.");

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            EnsureWritable();
            return stream.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(Span<byte> buffer) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new NotSupportedException());

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException<int>(new NotSupportedException());

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Use WriteAsync for archive output.");

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new NotSupportedException("Use WriteAsync for archive output.");

        public override void WriteByte(byte value) =>
            throw new NotSupportedException("Use WriteAsync for archive output.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWritable();
            return stream.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
                throw new ArgumentException("The offset and count exceed the buffer length.");

            EnsureWritable();
            return stream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _writerDisposed, 1);
            return owner.CompleteWriterAsync();
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _writerDisposed, 1);
            base.Dispose(disposing);
        }

        private void EnsureWritable()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _writerDisposed) != 0 || owner.IsDisposed, this);
        }
    }
}

using System.IO.Compression;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Single-reader seekable facade. Synchronous reads only copy the owned tail or already
/// resident cache pages. It borrows the source, tail and cache; their owner controls disposal.
/// </summary>
internal sealed class PrefetchedZipTailSourceStream : Stream
{
    private readonly long _tailOffset;
    private readonly ReadOnlyMemory<byte> _tail;
    private readonly PortableArchiveRangeCache _cache;
    private long _position;
    private bool _disposed;

    public PrefetchedZipTailSourceStream(IPortableArchiveSource source, long prefetchedOffset,
        ReadOnlyMemory<byte> prefetchedTail, PortableArchiveRangeCache rangeCache)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rangeCache);
        PortableZipMetadata.RequireRange(prefetchedOffset, prefetchedTail.Length, source.Length);
        if (source.Length - prefetchedOffset != prefetchedTail.Length)
            throw new ArgumentException("Prefetched data must extend to the archive end.", nameof(prefetchedTail));
        Length = source.Length;
        _tailOffset = prefetchedOffset;
        _tail = prefetchedTail;
        _cache = rangeCache;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position { get { EnsureOpen(); return _position; } set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        EnsureOpen();
        var count = ReadableCount(buffer.Length);
        if (count == 0)
            return 0;
        var destination = buffer[..count];
        if (_position >= _tailOffset)
            _tail.Span.Slice((int)(_position - _tailOffset), count).CopyTo(destination);
        else
        {
            var prefixCount = (int)Math.Min(count, _tailOffset - _position);
            if (!_cache.TryRead(_position, destination[..prefixCount], out var read) || read != prefixCount)
                throw new PortableArchiveException("zip_sync_cache_miss", "ZIP attempted a synchronous read outside prefetched memory.");
            if (prefixCount < count)
                _tail.Span[..(count - prefixCount)].CopyTo(destination[prefixCount..]);
        }
        _position += count;
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        cancellationToken.ThrowIfCancellationRequested();
        var count = ReadableCount(buffer.Length);
        if (count == 0)
            return 0;
        if (_position >= _tailOffset)
            _tail.Slice((int)(_position - _tailOffset), count).CopyTo(buffer);
        else
        {
            // Stop at the tail boundary so the tail is never fetched through the cache.
            count = (int)Math.Min(count, _tailOffset - _position);
            count = await _cache.ReadAsync(_position, buffer[..count], cancellationToken).ConfigureAwait(false);
        }
        _position += count;
        return count;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureOpen();
        long next;
        try
        {
            next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
        }
        catch (OverflowException exception) { throw new IOException("ZIP seek overflow.", exception); }
        if (next < 0)
            throw new IOException("Cannot seek before the archive start.");
        return _position = next;
    }
    private int ReadableCount(int requested) => _position >= Length ? 0 : (int)Math.Min(requested, Length - _position);
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
    public override void Flush() => EnsureOpen();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
}

/// <summary>
/// Owns the prefetched tail, cache, facade and native archive, but borrows the source.
/// Structural validation and native metadata cross-check finish before returning entries.
/// The measured 32 MiB tail + 16 MiB cache footprint belongs to the supplied budget instance.
/// Concurrent operations need separate budgets or host-level admission/concurrency control;
/// this is not a process-wide memory limit.
/// </summary>
internal sealed class PortableArchiveZipReader : IAsyncDisposable
{
    private readonly PortableBufferLease _tail;
    private readonly PortableArchiveRangeCache _cache;
    private bool _disposed;
    private PortableArchiveZipReader(ZipArchive archive, PrefetchedZipTailSourceStream stream,
        PortableBufferLease tail, PortableArchiveRangeCache cache, PortableZipTailLayout layout,
        IReadOnlyList<PortableZipDirectoryEntry> directory, IReadOnlyList<PortableZipEntryLayout> entryLayouts)
    {
        Archive = archive; Stream = stream; _tail = tail; _cache = cache;
        TailLayout = layout; Directory = directory; EntryLayouts = entryLayouts;
    }
    public ZipArchive Archive { get; }
    public PrefetchedZipTailSourceStream Stream { get; }
    public PortableZipTailLayout TailLayout { get; }
    public IReadOnlyList<PortableZipDirectoryEntry> Directory { get; }
    public IReadOnlyList<PortableZipEntryLayout> EntryLayouts { get; }
    public int PrefetchedTailBytes => _tail.Length;
    public long PrefetchedTailAllocatedBytes => _tail.AccountedBytes;
    public long RangeCacheHighWaterBytes => _cache.HighWaterBytes;

    public static async Task<PortableArchiveZipReader> OpenAsync(IPortableArchiveSource source,
        PortableArchiveBufferBudget budget, CancellationToken cancellationToken = default)
    {
        var layout = await PortableZipTailLocator.LocateAsync(source, budget, cancellationToken).ConfigureAwait(false);
        var tail = await budget.RentAsync(layout.PrefetchLength, cancellationToken).ConfigureAwait(false);
        PortableArchiveRangeCache? cache = null;
        PrefetchedZipTailSourceStream? stream = null;
        ZipArchive? archive = null;
        var transferred = false;
        try
        {
            await PortableZipMetadata.ReadExactlyAsync(source, layout.CentralDirectoryOffset, tail.Memory, cancellationToken).ConfigureAwait(false);
            PortableZipTailLocator.ValidatePrefetchedTail(tail.Memory.Span, layout);
            var directory = PortableZipDirectoryParser.Parse(tail.Memory[..(int)layout.CentralDirectorySize], layout.EntryCount, layout.CentralDirectoryOffset);
            cache = new PortableArchiveRangeCache(source, budget);
            var entries = await PortableZipEntryLayoutValidator.ValidateAsync(cache, budget, directory,
                layout.CentralDirectoryOffset, cancellationToken).ConfigureAwait(false);
            stream = new(source, layout.CentralDirectoryOffset, tail.Memory, cache);
            archive = await ZipArchive.CreateAsync(stream, ZipArchiveMode.Read, leaveOpen: true,
                entryNameEncoding: null, cancellationToken).ConfigureAwait(false);
            CrossCheck(archive, directory);
            var reader = new PortableArchiveZipReader(archive, stream, tail, cache, layout, directory, entries);
            transferred = true;
            return reader;
        }
        catch (InvalidDataException exception)
        {
            throw new PortableArchiveException("invalid_zip", "Native ZIP reader rejected the archive.", exception);
        }
        finally
        {
            // Ownership transfers only on successful construction/cross-check.
            if (!transferred)
            {
                try
                {
                    if (archive is not null) await archive.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    stream?.Dispose();
                    try { if (cache is not null) await cache.DisposeAsync().ConfigureAwait(false); }
                    finally { tail.Dispose(); }
                }
            }
        }
    }
    // No public native local-header offset API exists. The last eligible EOCD, signature-free
    // comment, contiguous metadata, snapshot revalidation and exact ZIP64 replacements
    // force the same directory/offsets.
    // Tests additionally compare native private offsets; product code uses no reflection.
    private static void CrossCheck(ZipArchive archive, IReadOnlyList<PortableZipDirectoryEntry> directory)
    {
        var native = archive.Entries;
        if (native.Count != directory.Count)
            throw PortableZipMetadata.Invalid("Native and defensive ZIP entry counts disagree.");
        for (var i = 0; i < native.Count; i++)
            if (native[i].FullName != directory[i].Path || native[i].Length != directory[i].Length
                || native[i].CompressedLength != directory[i].CompressedLength || native[i].Crc32 != directory[i].Crc32)
                throw PortableZipMetadata.Invalid("Native and defensive ZIP directory interpretations disagree.");
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await Archive.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            Stream.Dispose();
            try { await _cache.DisposeAsync().ConfigureAwait(false); }
            finally { _tail.Dispose(); }
        }
    }
}

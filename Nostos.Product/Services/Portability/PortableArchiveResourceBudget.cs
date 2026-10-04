namespace Nostos.Backend.Services.Portability;

internal sealed class PortableArchiveBufferBudget
{
    private readonly object _gate = new();
    private long _currentBytes;
    private long _highWaterBytes;

    public PortableArchiveBufferBudget(long maxBytes)
    {
        if (maxBytes <= 0 || maxBytes > PortableArchiveLimits.MaxExplicitBufferBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                $"The archive buffer budget must be between 1 and {PortableArchiveLimits.MaxExplicitBufferBytes} bytes.");
        }

        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }

    public long CurrentBytes
    {
        get
        {
            lock (_gate)
                return _currentBytes;
        }
    }

    public long HighWaterBytes
    {
        get
        {
            lock (_gate)
                return _highWaterBytes;
        }
    }

    public ValueTask<PortableBufferLease> RentAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0 || bytes > PortableArchiveLimits.MaxExplicitBufferBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        cancellationToken.ThrowIfCancellationRequested();

        var reservedBytes = GetPoolBucketLength(bytes);
        Reserve(reservedBytes);

        try
        {
            // ArrayPool only guarantees a minimum length; allocate the exact reserved bucket.
            var buffer = new byte[reservedBytes];
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PortableBufferLease(this, buffer, bytes, reservedBytes));
        }
        catch
        {
            Release(reservedBytes);
            throw;
        }
    }

    private void Reserve(long bytes)
    {
        lock (_gate)
        {
            if (bytes > MaxBytes - _currentBytes)
            {
                throw new InvalidOperationException(
                    $"The archive buffer budget would exceed its {MaxBytes} byte limit.");
            }

            _currentBytes += bytes;
            _highWaterBytes = Math.Max(_highWaterBytes, _currentBytes);
        }
    }

    private void Release(long bytes)
    {
        lock (_gate)
        {
            _currentBytes -= bytes;
            if (_currentBytes < 0)
                throw new InvalidOperationException("Archive buffer budget accounting became negative.");
        }
    }

    internal static int GetPoolBucketLength(int bytes)
    {
        if (bytes <= 16)
            return 16;

        var value = unchecked((uint)(bytes - 1));
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return checked((int)(value + 1));
    }

    internal void Return(byte[] buffer, long accountedBytes)
    {
        try
        {
            Array.Clear(buffer);
        }
        finally
        {
            Release(accountedBytes);
        }
    }
}

internal sealed class PortableBufferLease : IDisposable, IAsyncDisposable
{
    private readonly PortableArchiveBufferBudget _budget;
    private readonly int _length;
    private readonly long _accountedBytes;
    private byte[]? _buffer;

    internal PortableBufferLease(
        PortableArchiveBufferBudget budget,
        byte[] buffer,
        int length,
        long accountedBytes)
    {
        _budget = budget;
        _buffer = buffer;
        _length = length;
        _accountedBytes = accountedBytes;
    }

    public int Length => _length;

    internal long AccountedBytes => _accountedBytes;

    public Memory<byte> Memory =>
        (_buffer ?? throw new ObjectDisposedException(nameof(PortableBufferLease))).AsMemory(0, _length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            _budget.Return(buffer, _accountedBytes);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class PortableArchiveRangeCache : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly IPortableArchiveSource _source;
    private readonly PortableArchiveBufferBudget _budget;
    private readonly int _pageBytes;
    private readonly int _maxBytes;
    private readonly Dictionary<long, CachePage> _pages = new();
    private readonly LinkedList<long> _lru = new();
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private long _residentBytes;
    private long _reservedBytes;
    private long _highWaterBytes;
    private int _disposeStarted;

    public PortableArchiveRangeCache(
        IPortableArchiveSource source,
        PortableArchiveBufferBudget budget,
        int pageBytes = PortableArchiveLimits.CopyBufferBytes,
        int maxBytes = PortableArchiveLimits.RangeCacheBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        if (pageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageBytes));
        if (maxBytes <= 0 || maxBytes > PortableArchiveLimits.RangeCacheBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (PortableArchiveBufferBudget.GetPoolBucketLength(pageBytes) > maxBytes)
        {
            throw new ArgumentException(
                "The range-cache cap must be at least one pooled page buffer.",
                nameof(maxBytes));
        }

        _source = source;
        _budget = budget;
        _pageBytes = pageBytes;
        _maxBytes = maxBytes;
    }

    public long CurrentBytes
    {
        get
        {
            lock (_gate)
                return _residentBytes + _reservedBytes;
        }
    }

    public long HighWaterBytes
    {
        get
        {
            lock (_gate)
                return _highWaterBytes;
        }
    }

    public int PageCount
    {
        get
        {
            lock (_gate)
                return _pages.Count;
        }
    }

    public bool TryRead(long offset, Span<byte> destination, out int bytesRead)
    {
        FilePortableArchiveSource.ValidateOffset(offset, _source.Length);
        ThrowIfDisposing();

        bytesRead = FilePortableArchiveSource.GetReadableBytes(
            offset,
            destination.Length,
            _source.Length);
        if (bytesRead == 0)
            return true;

        lock (_gate)
        {
            ThrowIfDisposing();
            if (!ArePagesResident(offset, bytesRead))
            {
                bytesRead = 0;
                return false;
            }

            CopyResidentPages(offset, destination[..bytesRead]);
            return true;
        }
    }

    public async ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        FilePortableArchiveSource.ValidateOffset(offset, _source.Length);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposing();

        var requestedBytes = FilePortableArchiveSource.GetReadableBytes(
            offset,
            destination.Length,
            _source.Length);
        if (requestedBytes == 0)
            return 0;

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            var totalRead = 0;
            while (totalRead < requestedBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentOffset = checked(offset + totalRead);
                var pageIndex = currentOffset / _pageBytes;
                var pageOffset = (int)(currentOffset % _pageBytes);
                var page = await GetPageAsync(pageIndex, cancellationToken).ConfigureAwait(false);
                var bytesFromPage = Math.Min(
                    requestedBytes - totalRead,
                    page.Length - pageOffset);

                if (bytesFromPage <= 0)
                    throw new InvalidOperationException("The archive range cache produced an empty page read.");

                CopyPage(page, pageOffset, destination, totalRead, bytesFromPage);
                totalRead += bytesFromPage;
            }

            return totalRead;
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await _readGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    foreach (var page in _pages.Values)
                        page.Lease.Dispose();

                    _pages.Clear();
                    _lru.Clear();
                    _residentBytes = 0;
                }
            }
            finally
            {
                _readGate.Release();
            }

            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task<CachePage> GetPageAsync(long pageIndex, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfDisposing();
            if (TryGetPageLocked(pageIndex, out var cachedPage))
                return cachedPage;
        }

        var pageStart = checked(pageIndex * _pageBytes);
        var pageLength = checked((int)Math.Min(_pageBytes, _source.Length - pageStart));
        if (pageLength <= 0)
            throw new InvalidOperationException("The archive range cache requested a page outside the source length.");

        var reservedBytes = PortableArchiveBufferBudget.GetPoolBucketLength(pageLength);
        lock (_gate)
        {
            EvictUntilFits(reservedBytes);
            _reservedBytes += reservedBytes;
            _highWaterBytes = Math.Max(_highWaterBytes, _residentBytes + _reservedBytes);
        }

        PortableBufferLease? lease = null;
        var reservationPending = true;
        try
        {
            lease = await _budget.RentAsync(pageLength, cancellationToken).ConfigureAwait(false);
            var bytesRead = 0;
            while (bytesRead < pageLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposing();
                var read = await _source.ReadAtAsync(
                    checked(pageStart + bytesRead),
                    lease.Memory[bytesRead..],
                    cancellationToken).ConfigureAwait(false);

                if (read < 0 || read > pageLength - bytesRead)
                    throw new InvalidDataException("The archive source returned an invalid byte count.");
                if (read == 0)
                {
                    throw FilePortableArchiveSource.CreateUnexpectedEndException(
                        pageStart + bytesRead,
                        _source.Length);
                }

                bytesRead += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposing();
                var lruNode = _lru.AddLast(pageIndex);
                var page = new CachePage(pageIndex, lease, pageLength, lruNode);
                _pages.Add(pageIndex, page);
                _reservedBytes -= reservedBytes;
                _residentBytes += lease.AccountedBytes;
                reservationPending = false;
                lease = null;
                return page;
            }
        }
        finally
        {
            lease?.Dispose();
            if (reservationPending)
            {
                lock (_gate)
                    _reservedBytes -= reservedBytes;
            }
        }
    }

    private bool ArePagesResident(long offset, int bytesRead)
    {
        var remaining = bytesRead;
        var currentOffset = offset;
        while (remaining > 0)
        {
            var pageIndex = currentOffset / _pageBytes;
            if (!_pages.TryGetValue(pageIndex, out var page))
                return false;

            var pageOffset = (int)(currentOffset % _pageBytes);
            var count = Math.Min(remaining, page.Length - pageOffset);
            if (count <= 0)
                return false;

            currentOffset += count;
            remaining -= count;
        }

        return true;
    }

    private void CopyResidentPages(long offset, Span<byte> destination)
    {
        var remaining = destination.Length;
        var currentOffset = offset;
        var destinationOffset = 0;
        while (remaining > 0)
        {
            var pageIndex = currentOffset / _pageBytes;
            var page = _pages[pageIndex];
            var pageOffset = (int)(currentOffset % _pageBytes);
            var count = Math.Min(remaining, page.Length - pageOffset);
            page.Lease.Memory.Span.Slice(pageOffset, count).CopyTo(destination[destinationOffset..]);
            Touch(page);
            currentOffset += count;
            destinationOffset += count;
            remaining -= count;
        }
    }

    private bool TryGetPageLocked(long pageIndex, out CachePage page)
    {
        if (_pages.TryGetValue(pageIndex, out page!))
        {
            Touch(page);
            return true;
        }

        return false;
    }

    private void Touch(CachePage page)
    {
        _lru.Remove(page.Node);
        _lru.AddLast(page.Node);
    }

    private void EvictUntilFits(int pageLength)
    {
        while (_residentBytes + _reservedBytes + pageLength > _maxBytes)
        {
            var oldest = _lru.First
                ?? throw new InvalidOperationException("The archive range cache cannot evict enough space.");
            var page = _pages[oldest.Value];
            _pages.Remove(page.Index);
            _lru.RemoveFirst();
            _residentBytes -= page.Lease.AccountedBytes;
            page.Lease.Dispose();
        }
    }

    private static void CopyPage(
        CachePage page,
        int pageOffset,
        Memory<byte> destination,
        int destinationOffset,
        int count) =>
        page.Lease.Memory.Slice(pageOffset, count).CopyTo(destination.Slice(destinationOffset, count));

    private void ThrowIfDisposing() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

    private sealed record CachePage(long Index, PortableBufferLease Lease, int Length, LinkedListNode<long> Node);
}

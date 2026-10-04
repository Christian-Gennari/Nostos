using System.Collections.Concurrent;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableArchiveIoTests
{
    [Fact]
    public async Task File_source_reads_ranges_concurrently_and_returns_zero_at_length()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nostos-archive-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "archive.nostos");
        var expected = Enumerable.Range(0, 64).Select(x => (byte)x).ToArray();
        await File.WriteAllBytesAsync(path, expected);

        try
        {
            await using var source = new FilePortableArchiveSource(path);
            source.Length.Should().Be(expected.Length);

            var ranges = Enumerable.Range(0, 8).Select(index => ReadRangeAsync(source, index * 8, 8));
            var results = await Task.WhenAll(ranges);
            for (var index = 0; index < results.Length; index++)
                results[index].Should().Equal(expected.AsSpan(index * 8, 8).ToArray());

            (await source.ReadAtAsync(source.Length, new byte[4])).Should().Be(0);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => source.ReadAtAsync(source.Length + 1, new byte[1]).AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Range_source_clips_reads_at_length_and_preserves_legitimate_short_reads()
    {
        var payload = new byte[] { 10, 11, 12, 13, 14, 15 };
        var calls = new ConcurrentQueue<(long Offset, int Length)>();
        await using var source = new RangePortableArchiveSource(
            payload.Length,
            (offset, buffer, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls.Enqueue((offset, buffer.Length));
                var count = Math.Min(2, Math.Min(buffer.Length, payload.Length - checked((int)offset)));
                payload.AsMemory(checked((int)offset), count).CopyTo(buffer);
                return ValueTask.FromResult(count);
            });

        var buffer = new byte[10];
        var bytesRead = await source.ReadAtAsync(1, buffer);

        bytesRead.Should().Be(2);
        buffer.AsSpan(0, bytesRead).ToArray().Should().Equal(11, 12);
        (await source.ReadAtAsync(source.Length, buffer)).Should().Be(0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => source.ReadAtAsync(source.Length + 1, buffer).AsTask());
        calls.Should().ContainSingle().Which.Should().Be((1L, 5));
    }

    [Fact]
    public async Task Range_cache_loops_over_short_reads_and_fails_on_premature_end()
    {
        var payload = Enumerable.Range(0, 12).Select(x => (byte)(20 + x)).ToArray();
        var calls = new ConcurrentQueue<long>();
        await using var source = new RangePortableArchiveSource(
            payload.Length,
            (offset, buffer, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls.Enqueue(offset);
                var count = Math.Min(2, Math.Min(buffer.Length, payload.Length - checked((int)offset)));
                payload.AsMemory(checked((int)offset), count).CopyTo(buffer);
                return ValueTask.FromResult(count);
            });
        var budget = new PortableArchiveBufferBudget(1024);
        await using var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 8, maxBytes: 32);

        var destination = new byte[9];
        var bytesRead = await cache.ReadAsync(1, destination, CancellationToken.None);

        bytesRead.Should().Be(destination.Length);
        destination.Should().Equal(payload.AsSpan(1, 9).ToArray());
        calls.ToArray().Should().Equal(0, 2, 4, 6, 8, 10);
        cache.CurrentBytes.Should().Be(32);
        budget.CurrentBytes.Should().Be(cache.CurrentBytes);

        await using var prematureSource = new RangePortableArchiveSource(
            8,
            (offset, buffer, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offset >= 2)
                    return ValueTask.FromResult(0);

                buffer.Span[0] = 1;
                buffer.Span[1] = 2;
                return ValueTask.FromResult(Math.Min(2, buffer.Length));
            });
        await using var prematureCache = new PortableArchiveRangeCache(
            prematureSource,
            budget,
            pageBytes: 8,
            maxBytes: 32);

        var exception = await Assert.ThrowsAsync<PortableArchiveException>(
            () => prematureCache.ReadAsync(0, new byte[4], CancellationToken.None).AsTask());
        exception.Code.Should().Be("archive_source_truncated");
    }

    [Fact]
    public async Task Range_cache_uses_bounded_lru_and_sync_lookup_never_fetches()
    {
        var payload = Enumerable.Range(0, 48).Select(x => (byte)x).ToArray();
        var calls = 0;
        await using var source = new RangePortableArchiveSource(
            payload.Length,
            (offset, buffer, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref calls);
                var count = Math.Min(buffer.Length, payload.Length - checked((int)offset));
                payload.AsMemory(checked((int)offset), count).CopyTo(buffer);
                return ValueTask.FromResult(count);
            });
        var budget = new PortableArchiveBufferBudget(1024);
        await using var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 16, maxBytes: 32);

        await cache.ReadAsync(0, new byte[16], CancellationToken.None);
        await cache.ReadAsync(16, new byte[16], CancellationToken.None);
        var callsBeforeLookup = calls;
        var firstPage = new byte[16];
        cache.TryRead(0, firstPage, out var firstPageBytes).Should().BeTrue();
        firstPageBytes.Should().Be(16);
        firstPage.Should().Equal(payload.AsSpan(0, 16).ToArray());
        calls.Should().Be(callsBeforeLookup);

        await cache.ReadAsync(32, new byte[16], CancellationToken.None);

        cache.TryRead(16, new byte[16].AsSpan(), out var missingPageBytes).Should().BeFalse();
        missingPageBytes.Should().Be(0);
        cache.TryRead(0, firstPage.AsSpan(), out _).Should().BeTrue();
        cache.CurrentBytes.Should().Be(32);
        cache.HighWaterBytes.Should().BeLessThanOrEqualTo(32);
        cache.PageCount.Should().Be(2);
        budget.HighWaterBytes.Should().BeLessThanOrEqualTo(1024);
    }

    [Fact]
    public async Task Range_source_and_cache_honor_cancellation()
    {
        var callbackCalls = 0;
        await using var source = new RangePortableArchiveSource(
            4,
            (offset, buffer, cancellationToken) =>
            {
                Interlocked.Increment(ref callbackCalls);
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(0);
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.ReadAtAsync(0, new byte[1], cancellation.Token).AsTask());
        callbackCalls.Should().Be(0);

        var budget = new PortableArchiveBufferBudget(1024);
        await using var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 4, maxBytes: 16);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.ReadAsync(0, new byte[1], cancellation.Token).AsTask());
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Stream_sink_is_async_non_seekable_and_allows_one_writer()
    {
        var stream = new SyncForbiddenWriteStream();
        await using var sink = new StreamPortableArchiveSink(stream);
        var writer = await sink.OpenWriteAsync();

        writer.CanWrite.Should().BeTrue();
        writer.CanSeek.Should().BeFalse();
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3, CancellationToken.None);
        await writer.WriteAsync(new byte[] { 4, 5 }, CancellationToken.None);
        await writer.FlushAsync(CancellationToken.None);
        await writer.DisposeAsync();

        stream.ToArray().Should().Equal(1, 2, 3, 4, 5);
        stream.SynchronousCalls.Should().Be(0);
        stream.IsDisposed.Should().BeFalse();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await sink.OpenWriteAsync();
        });
    }

    [Fact]
    public async Task Stream_sink_disposes_owned_stream_asynchronously()
    {
        var stream = new SyncForbiddenWriteStream();
        var sink = new StreamPortableArchiveSink(stream, leaveOpen: false);
        var writer = await sink.OpenWriteAsync();
        await writer.DisposeAsync();

        stream.IsDisposed.Should().BeTrue();
        stream.SynchronousCalls.Should().Be(0);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task Range_cache_concurrent_misses_account_for_in_flight_bytes_within_cap()
    {
        const int pageBytes = 16;
        const int pageCount = 12;
        const int cap = 2 * pageBytes;
        var entered = Enumerable.Range(0, pageCount).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, pageCount).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var budget = new PortableArchiveBufferBudget(1024);
        await using var source = new RangePortableArchiveSource(pageCount * pageBytes,
            async (offset, buffer, cancellationToken) =>
            {
                var index = checked((int)(offset / pageBytes));
                entered[index].TrySetResult();
                await release[index].Task.WaitAsync(cancellationToken);
                buffer.Span.Fill((byte)index);
                return buffer.Length;
            });
        var cache = new PortableArchiveRangeCache(source, budget, pageBytes, cap);
        var destinations = Enumerable.Range(0, pageCount).Select(_ => new byte[pageBytes]).ToArray();
        var readers = Enumerable.Range(0, pageCount).Select(index =>
            cache.ReadAsync(index * pageBytes, destinations[index], CancellationToken.None).AsTask()).ToArray();
        long maxObservedBytes = 0;
        try
        {
            for (var index = 0; index < pageCount; index++)
            {
                await entered[index].Task;
                // This budget belongs only to the cache: it includes resident and in-flight arrays.
                var ownedBytes = budget.CurrentBytes;
                maxObservedBytes = Math.Max(maxObservedBytes, ownedBytes);
                ownedBytes.Should().BeLessThanOrEqualTo(cap);
                cache.CurrentBytes.Should().Be(ownedBytes, "resident and in-flight bytes must both be reported");
                cache.HighWaterBytes.Should().Be(maxObservedBytes);
                cache.HighWaterBytes.Should().BeLessThanOrEqualTo(cap);
                release[index].TrySetResult();
            }

            (await Task.WhenAll(readers)).Should().OnlyContain(count => count == pageBytes);
            for (var index = 0; index < pageCount; index++)
                destinations[index].Should().OnlyContain(value => value == index);
            maxObservedBytes.Should().Be(cap);
            budget.HighWaterBytes.Should().Be(cap);
        }
        finally
        {
            foreach (var gate in release)
                gate.TrySetResult();
            await Task.WhenAll(readers);
            await cache.DisposeAsync();
        }

        cache.CurrentBytes.Should().Be(0);
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Range_cache_concurrent_same_page_misses_share_one_source_read()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var source = new RangePortableArchiveSource(16,
            async (offset, buffer, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                buffer.Span.Fill(42);
                return buffer.Length;
            });
        var budget = new PortableArchiveBufferBudget(1024);
        await using var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 16, maxBytes: 16);
        var destinations = Enumerable.Range(0, 12).Select(_ => new byte[16]).ToArray();
        var readers = destinations.Select(destination =>
            cache.ReadAsync(0, destination, CancellationToken.None).AsTask()).ToArray();
        try
        {
            await entered.Task;
            calls.Should().Be(1);
            budget.CurrentBytes.Should().Be(16);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(readers);
        }

        calls.Should().Be(1);
        cache.PageCount.Should().Be(1);
        cache.CurrentBytes.Should().Be(16);
        foreach (var destination in destinations)
            destination.Should().OnlyContain(value => value == 42);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Range_cache_failed_or_cancelled_fetch_releases_reservation(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var source = new RangePortableArchiveSource(16,
            async (offset, buffer, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    throw new IOException("Gated source failed.");
                }

                buffer.Span.Fill(42);
                return buffer.Length;
            });
        var budget = new PortableArchiveBufferBudget(1024);
        var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 16, maxBytes: 16);
        using var cancellation = new CancellationTokenSource();
        var reader = cache.ReadAsync(0, new byte[16], cancellation.Token).AsTask();
        try
        {
            await entered.Task;
            cache.CurrentBytes.Should().Be(16);
            budget.CurrentBytes.Should().Be(16);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
            }
            else
            {
                release.SetResult();
                await Assert.ThrowsAsync<IOException>(() => reader);
            }

            cache.CurrentBytes.Should().Be(0);
            budget.CurrentBytes.Should().Be(0);
            cache.PageCount.Should().Be(0);
            (await cache.ReadAsync(0, new byte[16], CancellationToken.None)).Should().Be(16);
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            try { await reader; }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            await cache.DisposeAsync();
        }

        cache.CurrentBytes.Should().Be(0);
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Range_cache_failed_rent_releases_reservation_without_fetching()
    {
        var calls = 0;
        await using var source = new RangePortableArchiveSource(16, (offset, buffer, cancellationToken) =>
        {
            calls++;
            return ValueTask.FromResult(buffer.Length);
        });
        var budget = new PortableArchiveBufferBudget(16);
        using var lease = await budget.RentAsync(16, CancellationToken.None);
        await using var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 16, maxBytes: 16);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.ReadAsync(0, new byte[16], CancellationToken.None).AsTask());
        cache.CurrentBytes.Should().Be(0);
        cache.PageCount.Should().Be(0);
        calls.Should().Be(0);
        lease.Dispose();
        (await cache.ReadAsync(0, new byte[16], CancellationToken.None)).Should().Be(16);
        await cache.DisposeAsync();
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Range_cache_multi_page_sync_miss_leaves_destination_untouched()
    {
        var calls = 0;
        await using var source = new RangePortableArchiveSource(32, (offset, buffer, cancellationToken) =>
        {
            calls++;
            buffer.Span.Fill(42);
            return ValueTask.FromResult(buffer.Length);
        });
        var budget = new PortableArchiveBufferBudget(1024);
        await using var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 16, maxBytes: 32);
        await cache.ReadAsync(0, new byte[16], CancellationToken.None);
        var destination = Enumerable.Repeat((byte)99, 16).ToArray();

        cache.TryRead(8, destination, out var bytesRead).Should().BeFalse();

        bytesRead.Should().Be(0);
        destination.Should().OnlyContain(value => value == 99);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Range_cache_final_short_page_clips_async_and_sync_reads()
    {
        var payload = Enumerable.Range(0, 18).Select(value => (byte)value).ToArray();
        var calls = new List<(long Offset, int Length)>();
        await using var source = new RangePortableArchiveSource(payload.Length, (offset, buffer, cancellationToken) =>
        {
            calls.Add((offset, buffer.Length));
            payload.AsMemory(checked((int)offset), buffer.Length).CopyTo(buffer);
            return ValueTask.FromResult(buffer.Length);
        });
        var budget = new PortableArchiveBufferBudget(1024);
        await using var cache = new PortableArchiveRangeCache(source, budget, pageBytes: 16, maxBytes: 32);
        var asyncDestination = Enumerable.Repeat((byte)99, 8).ToArray();
        (await cache.ReadAsync(16, asyncDestination, CancellationToken.None)).Should().Be(2);
        asyncDestination.Should().Equal(16, 17, 99, 99, 99, 99, 99, 99);
        var syncDestination = Enumerable.Repeat((byte)99, 8).ToArray();

        cache.TryRead(16, syncDestination, out var bytesRead).Should().BeTrue();
        bytesRead.Should().Be(2);
        syncDestination.Should().Equal(asyncDestination);
        calls.Should().ContainSingle().Which.Should().Be((16L, 2));
        (await cache.ReadAsync(18, new byte[8], CancellationToken.None)).Should().Be(0);
        cache.TryRead(18, syncDestination, out bytesRead).Should().BeTrue();
        bytesRead.Should().Be(0);
        syncDestination.Should().Equal(asyncDestination);
    }

    [Fact]
    public async Task Stream_sink_sync_writer_disposal_only_closes_until_async_completion()
    {
        var stream = new SyncForbiddenWriteStream();
        await using var sink = new StreamPortableArchiveSink(stream, leaveOpen: false);
        var writer = await sink.OpenWriteAsync();
        await writer.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);

        writer.Dispose();

        writer.CanWrite.Should().BeFalse();
        stream.IsDisposed.Should().BeFalse();
        stream.SynchronousCalls.Should().Be(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => writer.WriteAsync(new byte[] { 3 }, CancellationToken.None).AsTask());
        await writer.DisposeAsync();
        await writer.DisposeAsync();
        stream.IsDisposed.Should().BeTrue();
        stream.AsyncDisposeCalls.Should().Be(1);
        stream.SynchronousCalls.Should().Be(0);
    }

    private static async Task<byte[]> ReadRangeAsync(IPortableArchiveSource source, long offset, int length)
    {
        var buffer = new byte[length];
        var read = await source.ReadAtAsync(offset, buffer);
        return buffer.AsSpan(0, read).ToArray();
    }

    private sealed class SyncForbiddenWriteStream : Stream
    {
        private readonly MemoryStream _buffer = new();

        public int SynchronousCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public int AsyncDisposeCalls { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !IsDisposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _buffer.ToArray();

        public override void Flush() => RecordSynchronousCall();

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => RecordSynchronousCall<int>();

        public override int Read(Span<byte> buffer) => RecordSynchronousCall<int>();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public override long Seek(long offset, SeekOrigin origin) => RecordSynchronousCall<long>();

        public override void SetLength(long value) => RecordSynchronousCall();

        public override void Write(byte[] buffer, int offset, int count) => RecordSynchronousCall();

        public override void Write(ReadOnlySpan<byte> buffer) => RecordSynchronousCall();

        public override void WriteByte(byte value) => RecordSynchronousCall();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _buffer.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _buffer.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            AsyncDisposeCalls++;
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing) => RecordSynchronousCall();

        private void RecordSynchronousCall()
        {
            SynchronousCalls++;
            throw new InvalidOperationException("Synchronous stream operation forbidden.");
        }

        private T RecordSynchronousCall<T>()
        {
            RecordSynchronousCall();
            throw new InvalidOperationException("Unreachable.");
        }
    }
}

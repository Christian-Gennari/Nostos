using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Reflection;
using Nostos.Backend.Services.Portability;
using Xunit;
using Xunit.Abstractions;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableZipAdapterTests(ITestOutputHelper output)
{
    private static PortableArchiveBufferBudget Budget() => new(PortableArchiveLimits.MaxExplicitBufferBytes);
    private static readonly DateTimeOffset Timestamp = new(2020, 1, 2, 3, 4, 6, TimeSpan.Zero);

    [Theory]
    [InlineData(31, CompressionLevel.NoCompression, 1, false)]
    [InlineData(4 * 1024 * 1024, CompressionLevel.NoCompression, 1, false)]
    [InlineData(31, CompressionLevel.Optimal, 1, false)]
    [InlineData(2 * 1024 * 1024, CompressionLevel.Optimal, 1, false)]
    [InlineData(4 * 1024 * 1024, CompressionLevel.Optimal, 1, false)]
    [InlineData(2 * 1024 * 1024, CompressionLevel.Optimal, 1, true)]
    [InlineData(1, CompressionLevel.NoCompression, 2000, false)]
    [InlineData(1, CompressionLevel.NoCompression, 20000, false)]
    public async Task Writer_is_byte_identical_to_native_with_no_physical_sync_io(
        int size, CompressionLevel compression, int count, bool compressible)
    {
        var clock = Stopwatch.StartNew();
        var payload = Payload(size, compressible);
        var budget = Budget();
        using var real = new CountingSink();
        var adapter = new BoundedSynchronousCaptureSink(real, budget);
        await WriteZipAsync(adapter, payload, compression, count);
        // Native DisposeAsync may itself flush asynchronously, draining captured metadata.
        await adapter.CompleteAsync();
        await adapter.CompleteAsync();
        var bytes = real.ToArray();
        using var reference = new CountingSink(allowSync: true);
        await WriteZipAsync(reference, payload, compression, count);
        Assert.Equal(reference.ToArray(), bytes);
        Assert.True(reference.SyncCalls > 0);
        Assert.Equal(0, real.SyncCalls);
        Assert.Equal(bytes.LongLength, adapter.BytesWritten);
        Assert.InRange(adapter.SynchronousHighWaterBytes, 1, PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes - 1);
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            Assert.Equal(count, archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                using var input = entry.Open();
                using var contents = new MemoryStream();
                input.CopyTo(contents);
                Assert.Equal(payload, contents.ToArray());
            }
        }
        output.WriteLine($"writer entries={count}, payload={size}, compression={compression}, pending high-water={adapter.SynchronousHighWaterBytes}, max sync write={adapter.MaxSingleSynchronousWriteBytes}, allocated high-water={budget.HighWaterBytes}, elapsed={clock.Elapsed.TotalSeconds:F3}s");
        await adapter.DisposeAsync();
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_max_data_deflate_purge_stays_bounded(bool compressible)
    {
        // Generated in 1 MiB chunks; no archive-sized capture or media-sized test input.
        var budget = Budget();
        using var destination = new CountingSink(discard: true);
        await using var sink = new BoundedSynchronousCaptureSink(destination, budget);
        var payload = Payload(1024 * 1024, compressible);
        await using (var archive = await ZipArchive.CreateAsync(sink, ZipArchiveMode.Create, true, null))
        {
            await using var entry = await archive.CreateEntry("data/library.json", CompressionLevel.Optimal).OpenAsync();
            for (long written = 0; written < PortableArchiveLimits.MaxDataBytes; written += payload.Length)
                await entry.WriteAsync(payload);
        }
        await sink.CompleteAsync();
        Assert.Equal(0, destination.SyncCalls);
        Assert.InRange(sink.SynchronousHighWaterBytes, 1, PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes - 1);
        output.WriteLine($"64 MiB deflate compressible={compressible}: pending={sink.SynchronousHighWaterBytes}, single={sink.MaxSingleSynchronousWriteBytes}, budget={budget.HighWaterBytes}");
        await sink.DisposeAsync();
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Writer_orders_all_sync_and_async_overloads_and_flushes()
    {
        var budget = Budget();
        using var real = new CountingSink();
        var sink = new BoundedSynchronousCaptureSink(real, budget);
        sink.Write(new byte[] { 1 }, 0, 1);
        sink.WriteByte(2);
        sink.Flush();
        Assert.Equal(0, real.AsyncCalls);
        await sink.WriteAsync(new byte[] { 3 }, 0, 1, CancellationToken.None);
        sink.Write(new byte[] { 4 }.AsSpan());
        await sink.FlushAsync(CancellationToken.None);
        sink.WriteByte(5);
        await sink.WriteAsync(new byte[] { 6 }.AsMemory());
        sink.WriteByte(7);
        await sink.DisposeAsync();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, real.ToArray());
        Assert.Equal(0, real.SyncCalls);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Writer_cap_overflow_poisoning_prevents_completion_or_retry()
    {
        var budget = Budget();
        using var real = new CountingSink();
        var sink = new BoundedSynchronousCaptureSink(real, budget);
        sink.Write(new byte[PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes]);
        AssertCode("zip_sync_buffer_exceeded", () => sink.WriteByte(1));
        Assert.True(sink.IsPoisoned);
        await AssertCodeAsync("zip_sink_poisoned", () => sink.CompleteAsync());
        AssertCode("zip_sink_poisoned", () => sink.WriteByte(1));
        Assert.Equal(0, real.AsyncCalls);
        Assert.Equal(0, real.SyncCalls);
        await sink.DisposeAsync();
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public void Writer_sync_dispose_with_pending_bytes_errors_without_io()
    {
        var budget = Budget();
        using var real = new CountingSink();
        var sink = new BoundedSynchronousCaptureSink(real, budget, leaveOpen: false);
        sink.WriteByte(1);
        AssertCode("zip_sync_dispose_pending", sink.Dispose);
        Assert.True(sink.IsPoisoned);
        Assert.Equal(0, real.AsyncCalls);
        Assert.Equal(0, real.SyncCalls);
        Assert.Equal(0, real.AsyncDisposals);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("complete")]
    public async Task Writer_cancellation_poisoning_never_reports_success(string operation)
    {
        var budget = Budget();
        using var real = new CountingSink();
        var sink = new BoundedSynchronousCaptureSink(real, budget);
        sink.WriteByte(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "write" => sink.WriteAsync(new byte[] { 2 }.AsMemory(), cts.Token).AsTask(),
            "flush" => sink.FlushAsync(cts.Token),
            _ => sink.CompleteAsync(cts.Token),
        });
        Assert.True(sink.IsPoisoned);
        await AssertCodeAsync("zip_sink_poisoned", () => sink.CompleteAsync());
        Assert.Equal(0, real.AsyncCalls);
        Assert.Equal(0, real.SyncCalls);
        await sink.DisposeAsync();
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_failed_drain_or_flush_poisoning_never_retries(bool failFlush)
    {
        var budget = Budget();
        using var real = new CountingSink { FailWrite = !failFlush, FailFlush = failFlush };
        var sink = new BoundedSynchronousCaptureSink(real, budget, leaveOpen: false);
        sink.WriteByte(1);
        await Assert.ThrowsAsync<IOException>(() => sink.CompleteAsync());
        Assert.True(sink.IsPoisoned);
        var calls = real.AsyncCalls;
        await AssertCodeAsync("zip_sink_poisoned", () => sink.CompleteAsync());
        Assert.Equal(calls, real.AsyncCalls);
        Assert.Equal(0, real.SyncCalls);
        await sink.DisposeAsync();
        Assert.Equal(1, real.AsyncDisposals);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData(false, CompressionLevel.NoCompression)]
    [InlineData(true, CompressionLevel.NoCompression)]
    [InlineData(false, CompressionLevel.Optimal)]
    [InlineData(true, CompressionLevel.Optimal)]
    public async Task Reader_first_native_index_is_memory_only_and_entries_are_async(bool descriptor, CompressionLevel compression)
    {
        var payload = Payload(2 * 1024 * 1024, false);
        var bytes = await NativeZipAsync(payload, compression, descriptor);
        using var physical = new CountingReadStream(bytes);
        await using var source = Source(physical, shortReads: 137);
        var budget = Budget();
        var layout = await PortableZipTailLocator.LocateAsync(source, budget);
        using var tail = await budget.RentAsync(layout.PrefetchLength, CancellationToken.None);
        await PortableZipMetadata.ReadExactlyAsync(source, layout.CentralDirectoryOffset, tail.Memory, CancellationToken.None);
        await using var cache = new PortableArchiveRangeCache(source, budget);
        await using var stream = new PrefetchedZipTailSourceStream(source, layout.CentralDirectoryOffset, tail.Memory, cache);
        await using var archive = await ZipArchive.CreateAsync(stream, ZipArchiveMode.Read, true, null);
        var readsBeforeIndex = physical.AsyncCalls;
        Assert.Single(archive.Entries);
        Assert.Equal(readsBeforeIndex, physical.AsyncCalls);
        Assert.False(cache.TryRead(0, new byte[30], out _)); // Initial EOCD probing may warm tail-adjacent pages, never the local header.
        await using var entry = await archive.Entries[0].OpenAsync();
        using var contents = new MemoryStream();
        await entry.CopyToAsync(contents);
        Assert.Equal(payload, contents.ToArray());
        Assert.True(physical.AsyncCalls > readsBeforeIndex);
        Assert.Equal(0, physical.SyncCalls);
        output.WriteLine($"reader tail={tail.Length}, tail allocation={tail.AccountedBytes}, range cache high-water={cache.HighWaterBytes}, total budget high-water={budget.HighWaterBytes}");
        await cache.DisposeAsync();
        tail.Dispose();
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Writer_to_validated_reader_roundtrip_matches_every_entry()
    {
        var payload = Payload(2 * 1024 * 1024, false);
        using var real = new CountingSink();
        var writerBudget = Budget();
        await using (var sink = new BoundedSynchronousCaptureSink(real, writerBudget))
        {
            await WriteZipAsync(sink, payload, CompressionLevel.Optimal, 3);
            await sink.CompleteAsync();
        }
        Assert.Equal(0, writerBudget.CurrentBytes);
        using var physical = new CountingReadStream(real.ToArray());
        await using var source = Source(physical);
        var budget = Budget();
        await using (var reader = await PortableArchiveZipReader.OpenAsync(source, budget))
        {
            var calls = physical.AsyncCalls;
            Assert.Equal(3, reader.Archive.Entries.Count);
            Assert.Equal(calls, physical.AsyncCalls);
            foreach (var item in reader.Archive.Entries)
            {
                await using var entry = await item.OpenAsync();
                using var contents = new MemoryStream();
                await entry.CopyToAsync(contents);
                Assert.Equal(payload, contents.ToArray());
            }
            output.WriteLine($"factory reader: tail={reader.PrefetchedTailBytes}, tail allocation={reader.PrefetchedTailAllocatedBytes}, range cache high-water={reader.RangeCacheHighWaterBytes}, budget={budget.HighWaterBytes}");
        }
        Assert.Equal(0, physical.SyncCalls);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_accepts_native_stored_and_deflated_without_descriptors(bool deflated)
    {
        var bytes = await NativeZipAsync(Payload(400, false), deflated ? CompressionLevel.Optimal : CompressionLevel.NoCompression, false);
        await ReadValidatedAsync(bytes, Payload(400, false));
    }

    [Fact]
    public async Task Reader_sync_cache_miss_does_no_io_and_preserves_position()
    {
        var bytes = await NativeZipAsync(Payload(1024, false), CompressionLevel.NoCompression, false);
        using var physical = new CountingReadStream(bytes);
        await using var source = Source(physical);
        var budget = Budget();
        var layout = await PortableZipTailLocator.LocateAsync(source, budget);
        using var tail = await budget.RentAsync(layout.PrefetchLength, CancellationToken.None);
        await PortableZipMetadata.ReadExactlyAsync(source, layout.CentralDirectoryOffset, tail.Memory, CancellationToken.None);
        await using var cache = new PortableArchiveRangeCache(source, budget);
        using var stream = new PrefetchedZipTailSourceStream(source, layout.CentralDirectoryOffset, tail.Memory, cache);
        var calls = physical.AsyncCalls;
        AssertCode("zip_sync_cache_miss", () => stream.ReadByte());
        AssertCode("zip_sync_cache_miss", () => { _ = stream.Read(new byte[1].AsSpan()); });
        Assert.Equal(0, stream.Position);
        Assert.Equal(calls, physical.AsyncCalls);
        Assert.Equal(0, physical.SyncCalls);
        var first = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(first, 0, 1));
        stream.Position = 0;
        Assert.Equal(bytes[0], stream.ReadByte()); // Resident pages are allowed, never populated synchronously.
        stream.Seek(-1, SeekOrigin.End);
        Assert.Equal(bytes[^1], stream.ReadByte());
        Assert.Equal(-1, stream.ReadByte());
        stream.Seek(10, SeekOrigin.End);
        Assert.Equal(0, stream.Read(new byte[1]));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        stream.Position = long.MaxValue;
        Assert.Throws<IOException>(() => stream.Seek(1, SeekOrigin.Current));
        await cache.DisposeAsync(); tail.Dispose();
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData("no_eocd", "invalid_zip")]
    [InlineData("comment", "invalid_zip")]
    [InlineData("directory_cap", "invalid_zip")]
    [InlineData("entry_cap", "too_many_entries")]
    [InlineData("directory_offset", "invalid_zip")]
    [InlineData("directory_overlap", "invalid_zip")]
    [InlineData("truncated_entry", "invalid_zip")]
    [InlineData("name_overrun", "invalid_zip")]
    [InlineData("local_offset", "invalid_zip")]
    [InlineData("overlap", "invalid_zip")]
    [InlineData("encrypted", "invalid_zip")]
    [InlineData("multi_disk", "invalid_zip")]
    [InlineData("entry_multi_disk", "invalid_zip")]
    [InlineData("method", "invalid_zip")]
    [InlineData("duplicate_path", "duplicate_path")]
    [InlineData("duplicate_offset", "invalid_zip")]
    [InlineData("unsafe_path", "unsafe_archive_path")]
    [InlineData("missing_zip64_extra", "invalid_zip")]
    [InlineData("malformed_extra", "invalid_zip")]
    [InlineData("local_method", "invalid_zip")]
    [InlineData("local_name", "invalid_zip")]
    [InlineData("local_sizes", "invalid_zip")]
    [InlineData("descriptor", "invalid_zip")]
    public async Task Hostile_archive_is_rejected_before_large_allocation(string attack, string code)
    {
        var bytes = await NativeZipAsync(Payload(40, false), CompressionLevel.NoCompression, true, 2);
        var eocd = bytes.Length - 22;
        var cd = (int)U32(bytes, eocd + 16);
        var second = cd + 46 + U16(bytes, cd + 28);
        switch (attack)
        {
            case "no_eocd": W32(bytes, eocd, 0); break;
            case "comment": W16(bytes, eocd + 20, ushort.MaxValue); break;
            case "directory_cap": W32(bytes, eocd + 12, PortableArchiveLimits.MaxCentralDirectoryBytes + 1); break;
            case "entry_cap": W16(bytes, eocd + 8, 20001); W16(bytes, eocd + 10, 20001); break;
            case "directory_offset": W32(bytes, eocd + 16, (uint)bytes.Length + 1); break;
            case "directory_overlap": W32(bytes, eocd + 16, (uint)eocd); break;
            case "truncated_entry": W32(bytes, eocd + 12, 45); break;
            case "name_overrun": W16(bytes, cd + 28, ushort.MaxValue); break;
            case "local_offset": W32(bytes, cd + 42, (uint)bytes.Length + 1); break;
            case "overlap": W32(bytes, cd + 20, 80); W32(bytes, cd + 24, 80); break;
            case "encrypted": W16(bytes, cd + 8, 1); break;
            case "multi_disk": W16(bytes, eocd + 4, 1); break;
            case "entry_multi_disk": W16(bytes, cd + 34, 1); break;
            case "method": W16(bytes, cd + 10, 99); break;
            case "duplicate_path": bytes.AsSpan(cd + 46, U16(bytes, cd + 28)).CopyTo(bytes.AsSpan(second + 46)); break;
            case "duplicate_offset": W32(bytes, second + 42, 0); break;
            case "unsafe_path": bytes[cd + 46] = (byte)'/'; break;
            case "missing_zip64_extra": W32(bytes, cd + 24, uint.MaxValue); break;
            case "malformed_extra": W16(bytes, cd + 30, 1); break;
            case "local_method": W16(bytes, 8, 8); break;
            case "local_name": bytes[30] ^= 1; break;
            case "local_sizes": W16(bytes, 6, 0); W16(bytes, cd + 8, 0); break;
            case "descriptor": bytes[30 + U16(bytes, 26) + 40 + 4] ^= 1; break;
        }
        using var physical = new CountingReadStream(bytes);
        await using var source = Source(physical);
        var budget = Budget();
        await AssertCodeAsync(code, async () => { await using var reader = await PortableArchiveZipReader.OpenAsync(source, budget); });
        Assert.Equal(0, physical.SyncCalls);
        Assert.Equal(0, budget.CurrentBytes);
        Assert.True(budget.HighWaterBytes <= 2 * 1024 * 1024);
    }

    [Fact]
    public async Task Source_shorter_than_declared_is_archive_source_truncated()
    {
        var budget = Budget();
        await using var source = new RangePortableArchiveSource(1000, (_, _, _) => ValueTask.FromResult(0));
        await AssertCodeAsync("archive_source_truncated", async () => { await using var reader = await PortableArchiveZipReader.OpenAsync(source, budget); });
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Over_limit_source_is_rejected_before_read_or_allocation()
    {
        var budget = Budget();
        var calls = 0;
        await using var source = new RangePortableArchiveSource(PortableArchiveLimits.MaxArchiveBytes + 1,
            (_, _, _) => { calls++; throw new InvalidOperationException(); });
        await AssertCodeAsync("archive_too_large", () => PortableZipTailLocator.LocateAsync(source, budget));
        Assert.Equal(0, calls);
        Assert.Equal(0, budget.HighWaterBytes);
    }

    [Fact]
    public void Range_cache_rejects_oversized_page_before_allocation_rounding()
    {
        var source = new RangePortableArchiveSource(10, (_, _, _) => ValueTask.FromResult(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortableArchiveRangeCache(source, Budget(), int.MaxValue));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Reader_supports_all_four_standard_descriptor_forms(bool zip64, bool signature)
    {
        var payload = Payload(40, false);
        var original = await NativeZipAsync(payload, CompressionLevel.NoCompression, true);
        var cd = (int)U32(original, original.Length - 6);
        var descriptorOffset = cd - 16;
        using var bytes = new MemoryStream();
        bytes.Write(original.AsSpan(0, descriptorOffset));
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            if (signature) writer.Write(0x08074b50u);
            writer.Write(U32(original, descriptorOffset + 4));
            if (zip64) { writer.Write((ulong)payload.Length); writer.Write((ulong)payload.Length); }
            else { writer.Write((uint)payload.Length); writer.Write((uint)payload.Length); }
        }
        var newCd = (uint)bytes.Length;
        bytes.Write(original.AsSpan(cd));
        var modified = bytes.ToArray();
        W32(modified, modified.Length - 6, newCd);
        await ReadValidatedAsync(modified, payload);
    }

    [Fact]
    public async Task Reader_rejects_actual_nested_physical_entry_overlap()
    {
        var bytes = await NativeZipAsync(Payload(256, false), CompressionLevel.NoCompression, false, 2);
        var cd = (int)U32(bytes, bytes.Length - 6);
        var secondCd = cd + 46 + U16(bytes, cd + 28);
        var secondLocal = (int)U32(bytes, secondCd + 42);
        var headerLength = 30 + U16(bytes, secondLocal + 26);
        var nestedOffset = 30 + U16(bytes, 26) + 10;
        var nestedHeader = bytes.AsSpan(secondLocal, headerLength).ToArray();
        W32(nestedHeader, 14, 0); W32(nestedHeader, 18, 0); W32(nestedHeader, 22, 0);
        nestedHeader.CopyTo(bytes.AsSpan(nestedOffset));
        W32(bytes, secondCd + 16, 0); W32(bytes, secondCd + 20, 0); W32(bytes, secondCd + 24, 0);
        W32(bytes, secondCd + 42, (uint)nestedOffset);
        var budget = Budget();
        using var physical = new CountingReadStream(bytes);
        await using var source = Source(physical);
        var exception = await Assert.ThrowsAsync<PortableArchiveException>(() => PortableArchiveZipReader.OpenAsync(source, budget));
        Assert.Equal("invalid_zip", exception.Code);
        Assert.Contains("overlap", exception.Message);
        Assert.Equal(0, physical.SyncCalls);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("locator_outside")]
    [InlineData("locator_missing")]
    [InlineData("locator_disk")]
    [InlineData("record_short")]
    [InlineData("record_large")]
    [InlineData("record_overlap")]
    [InlineData("record_disk")]
    [InlineData("count_disagreement")]
    [InlineData("classic_disagreement")]
    [InlineData("unsigned_overflow")]
    [InlineData("directory_cap")]
    [InlineData("entry_cap")]
    [InlineData("directory_outside")]
    [InlineData("tail_gap")]
    public async Task Sparse_zip64_tail_is_bounded_and_validated(string scenario)
    {
        var payload = Payload(40, false);
        var native = await NativeZipAsync(payload, CompressionLevel.NoCompression, false);
        var nativeCd = (int)U32(native, native.Length - 6);
        const long cdOffset = (long)uint.MaxValue + 4096;
        var local = native[..nativeCd];
        var directory = native[nativeCd..^22];
        var tail = Zip64Tail(directory, cdOffset);
        var record = directory.Length;
        var locator = record + 56;
        switch (scenario)
        {
            case "locator_outside": W64(tail, locator + 8, (ulong)(cdOffset + tail.Length)); break;
            case "locator_missing": W32(tail, locator, 0); break;
            case "locator_disk": W32(tail, locator + 4, 1); break;
            case "record_short": W64(tail, record + 4, 43); break;
            case "record_large": W64(tail, record + 4, PortableArchiveLimits.MaxZip64EndRecordBytes); break;
            case "record_overlap": W64(tail, record + 4, 45); break;
            case "record_disk": W32(tail, record + 16, 1); break;
            case "count_disagreement": W64(tail, record + 24, 2); break;
            case "classic_disagreement": W16(tail, tail.Length - 12, 2); break;
            case "unsigned_overflow": W64(tail, record + 48, ulong.MaxValue); break;
            case "directory_cap": W64(tail, record + 40, PortableArchiveLimits.MaxCentralDirectoryBytes + 1); break;
            case "entry_cap": W64(tail, record + 24, 20001); W64(tail, record + 32, 20001); break;
            case "directory_outside": W64(tail, record + 48, (ulong)(cdOffset + tail.Length)); break;
            case "tail_gap": W64(tail, record + 48, (ulong)(cdOffset - PortableArchiveLimits.MaxPrefetchedZipTailBytes)); break;
        }
        var sourceCalls = 0;
        await using var source = SparseSource(local, cdOffset, tail, () => sourceCalls++);
        var budget = Budget();
        if (scenario == "valid")
        {
            await using (var reader = await PortableArchiveZipReader.OpenAsync(source, budget))
            {
                Assert.True(reader.TailLayout.IsZip64);
                Assert.True(reader.TailLayout.CentralDirectoryOffset > uint.MaxValue);
                var calls = sourceCalls;
                Assert.Single(reader.Archive.Entries);
                Assert.Equal(calls, sourceCalls);
                await using var entry = await reader.Archive.Entries[0].OpenAsync();
                using var contents = new MemoryStream();
                await entry.CopyToAsync(contents);
                Assert.Equal(payload, contents.ToArray());
                Assert.True(budget.HighWaterBytes < 3 * 1024 * 1024);
                output.WriteLine($"sparse ZIP64 length={source.Length}, tail={reader.PrefetchedTailBytes}, budget={budget.HighWaterBytes}, source calls={sourceCalls}");
            }
        }
        else
        {
            await AssertCodeAsync(scenario == "entry_cap" ? "too_many_entries" : "invalid_zip",
                () => PortableArchiveZipReader.OpenAsync(source, budget));
            Assert.True(budget.HighWaterBytes < 256 * 1024);
        }
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Reader_accepts_unchanged_existing_generated_historical_fixtures(int version)
    {
        // Reuse the existing private generator verbatim without widening another slice's test API.
        var method = typeof(PortableMigrationFixtureAndCompatTests).GetMethod("CreateFixtureAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, new object[] { $"data{version}.nostos", version })!;
        await task;
        using var fixture = (IDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        var bytes = (byte[])fixture.GetType().GetProperty("Bytes")!.GetValue(fixture)!;
        using var native = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var physical = new CountingReadStream(bytes);
        await using var source = Source(physical);
        var budget = Budget();
        await using (var reader = await PortableArchiveZipReader.OpenAsync(source, budget))
        {
            Assert.Equal(native.Entries.Count, reader.Archive.Entries.Count);
            foreach (var item in reader.Archive.Entries)
            {
                using var expected = native.GetEntry(item.FullName)!.Open();
                using var expectedBytes = new MemoryStream();
                await expected.CopyToAsync(expectedBytes);
                await using var actual = await item.OpenAsync();
                using var actualBytes = new MemoryStream();
                await actual.CopyToAsync(actualBytes);
                Assert.Equal(expectedBytes.ToArray(), actualBytes.ToArray());
            }
        }
        Assert.Equal(0, physical.SyncCalls);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Maximum_directory_and_full_range_cache_fit_the_64_mib_budget()
    {
        var native = await NativeZipAsync(Array.Empty<byte>(), CompressionLevel.NoCompression, false, 256);
        var cd = (int)U32(native, native.Length - 6);
        using var directory = new MemoryStream();
        var position = cd;
        for (var i = 0; i < 256; i++)
        {
            var recordLength = 46 + U16(native, position + 28);
            var entry = native.AsSpan(position, recordLength).ToArray();
            var extraLength = 65536 - recordLength;
            W16(entry, 30, (ushort)extraLength);
            directory.Write(entry);
            var extra = new byte[extraLength];
            W16(extra, 0, 0xffff); W16(extra, 2, (ushort)(extraLength - 4));
            directory.Write(extra);
            position += recordLength;
        }
        Assert.Equal(PortableArchiveLimits.MaxCentralDirectoryBytes, directory.Length);
        const long sparseCd = (long)uint.MaxValue + 4096;
        // ZIP64 EOCD is needed only for the sparse offset, not the individual entries.
        var tail = Zip64Tail(directory.ToArray(), sparseCd, 256);
        await using var source = SparseSource(native[..cd], sparseCd, tail);
        var budget = Budget();
        await using (var reader = await PortableArchiveZipReader.OpenAsync(source, budget))
        {
            Assert.Equal(32 * 1024 * 1024, reader.PrefetchedTailAllocatedBytes);
            for (var page = 1; page <= 16; page++)
            {
                reader.Stream.Position = page * 1024L * 1024;
                Assert.Equal(1, await reader.Stream.ReadAsync(new byte[1]));
            }
            Assert.Equal(16 * 1024 * 1024, reader.RangeCacheHighWaterBytes);
            Assert.Equal(48 * 1024 * 1024, budget.HighWaterBytes);
            output.WriteLine($"max reader footprint: logical tail={reader.PrefetchedTailBytes}, tail allocation={reader.PrefetchedTailAllocatedBytes}, range cache={reader.RangeCacheHighWaterBytes}, total={budget.HighWaterBytes}");
        }
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Reader_cancellation_during_prefetch_and_layout_returns_every_lease()
    {
        var bytes = await NativeZipAsync(Payload(1024, false), CompressionLevel.NoCompression, false);
        foreach (var cancelOnCall in new[] { 1, 3, 4 })
        {
            using var cts = new CancellationTokenSource();
            var calls = 0;
            await using var source = new RangePortableArchiveSource(bytes.Length, (offset, buffer, ct) =>
            {
                if (++calls == cancelOnCall) cts.Cancel();
                ct.ThrowIfCancellationRequested();
                bytes.AsMemory((int)offset, buffer.Length).CopyTo(buffer);
                return ValueTask.FromResult(buffer.Length);
            });
            var budget = Budget();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PortableArchiveZipReader.OpenAsync(source, budget, cts.Token));
            Assert.Equal(0, budget.CurrentBytes);
        }
    }

    private static void W64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static byte[] Zip64Tail(byte[] directory, long cdOffset, int count = 1)
    {
        using var tail = new MemoryStream();
        using (var writer = new BinaryWriter(tail, Encoding.UTF8, true))
        {
            writer.Write(directory);
            writer.Write(0x06064b50u); writer.Write(44UL); writer.Write((ushort)45); writer.Write((ushort)45);
            writer.Write(0u); writer.Write(0u); writer.Write((ulong)count); writer.Write((ulong)count);
            writer.Write((ulong)directory.Length); writer.Write((ulong)cdOffset);
            writer.Write(0x07064b50u); writer.Write(0u); writer.Write((ulong)(cdOffset + directory.Length)); writer.Write(1u);
            writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write(ushort.MaxValue); writer.Write(ushort.MaxValue); writer.Write(uint.MaxValue); writer.Write(uint.MaxValue); writer.Write((ushort)0);
        }
        return tail.ToArray();
    }
    private static RangePortableArchiveSource SparseSource(byte[] local, long tailOffset, byte[] tail, Action? onRead = null) =>
        new(tailOffset + tail.Length, (offset, buffer, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            onRead?.Invoke();
            buffer.Span.Clear();
            CopyOverlap(local, 0, offset, buffer.Span);
            CopyOverlap(tail, tailOffset, offset, buffer.Span);
            return ValueTask.FromResult(buffer.Length);
        });
    private static void CopyOverlap(byte[] bytes, long start, long offset, Span<byte> buffer)
    {
        var from = Math.Max(start, offset);
        var to = Math.Min(start + bytes.Length, offset + buffer.Length);
        if (from < to) bytes.AsSpan((int)(from - start), (int)(to - from)).CopyTo(buffer[(int)(from - offset)..]);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("truncated")]
    [InlineData("missing_value")]
    [InlineData("overflow")]
    [InlineData("duplicate")]
    [InlineData("local_extra")]
    public async Task Zip64_entry_replacement_fields_are_validated(string scenario)
    {
        var payload = Payload(40, false);
        var bytes = await NativeZipAsync(payload, CompressionLevel.NoCompression, false);
        var cd = (int)U32(bytes, bytes.Length - 6);
        var nameLength = U16(bytes, cd + 28);
        var header = bytes.AsSpan(cd, 46 + nameLength).ToArray();
        W32(header, 20, uint.MaxValue); W32(header, 24, uint.MaxValue); W32(header, 42, uint.MaxValue);
        var extra = new byte[28];
        W16(extra, 0, 1); W16(extra, 2, 24);
        W64(extra, 4, (ulong)payload.Length); W64(extra, 12, (ulong)payload.Length); W64(extra, 20, 0);
        switch (scenario)
        {
            case "truncated": W16(extra, 2, 25); break;
            case "missing_value": extra = extra[..20]; W16(extra, 2, 16); break;
            case "overflow": W64(extra, 20, ulong.MaxValue); break;
            case "duplicate": extra = extra.Concat(extra).ToArray(); break;
            case "local_extra": W16(bytes, 28, 1); break;
        }
        W16(header, 30, (ushort)extra.Length);
        using var buffer = new MemoryStream();
        buffer.Write(bytes.AsSpan(0, cd)); buffer.Write(header); buffer.Write(extra); buffer.Write(bytes.AsSpan(bytes.Length - 22));
        var modified = buffer.ToArray();
        W32(modified, modified.Length - 10, (uint)(header.Length + extra.Length));
        if (scenario == "valid") await ReadValidatedAsync(modified, payload);
        else
        {
            using var physical = new CountingReadStream(modified);
            await using var source = Source(physical);
            var budget = Budget();
            await AssertCodeAsync("invalid_zip", () => PortableArchiveZipReader.OpenAsync(source, budget));
            Assert.Equal(0, budget.CurrentBytes);
            Assert.Equal(0, physical.SyncCalls);
        }
    }

    [Fact]
    public async Task Maximum_legal_comment_is_accepted_with_bounded_tail()
    {
        var bytes = await NativeZipAsync(Payload(40, false), CompressionLevel.NoCompression, false);
        W16(bytes, bytes.Length - 2, ushort.MaxValue);
        await ReadValidatedAsync(bytes.Concat(new byte[ushort.MaxValue]).ToArray(), Payload(40, false));
    }

    [Fact]
    public async Task Writer_sync_dispose_never_disposes_owned_sink_but_async_cleanup_does()
    {
        var budget = Budget();
        using var real = new CountingSink();
        var sink = new BoundedSynchronousCaptureSink(real, budget, leaveOpen: false);
        sink.Dispose();
        Assert.Equal(0, real.AsyncDisposals);
        Assert.Equal(0, real.SyncCalls);
        await sink.DisposeAsync();
        await sink.DisposeAsync();
        Assert.Equal(1, real.AsyncDisposals);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Writer_partial_drain_failure_is_not_replayed()
    {
        var budget = Budget();
        using var real = new CountingSink { PartialWriteFailure = true };
        var sink = new BoundedSynchronousCaptureSink(real, budget);
        sink.Write(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync(new byte[] { 4 }.AsMemory()).AsTask());
        Assert.Equal(new byte[] { 1 }, real.ToArray());
        Assert.True(sink.IsPoisoned);
        Assert.Equal(0, sink.BytesWritten);
        await AssertCodeAsync("zip_sink_poisoned", () => sink.CompleteAsync());
        await sink.DisposeAsync();
        Assert.Equal(new byte[] { 1 }, real.ToArray());
        Assert.Equal(0, real.SyncCalls);
        Assert.Equal(0, budget.CurrentBytes);
    }

    [Fact]
    public async Task Reader_premature_eof_after_tail_prefetch_releases_tail_and_cache()
    {
        var bytes = await NativeZipAsync(Payload(1024, false), CompressionLevel.NoCompression, false);
        var cd = U32(bytes, bytes.Length - 6);
        var calls = 0;
        await using var source = new RangePortableArchiveSource(bytes.Length, (offset, buffer, _) =>
        {
            calls++;
            // Discovery and prefetch succeed; local-header page retrieval ends prematurely.
            if (calls >= 4 && offset < cd) return ValueTask.FromResult(0);
            bytes.AsMemory((int)offset, buffer.Length).CopyTo(buffer);
            return ValueTask.FromResult(buffer.Length);
        });
        var budget = Budget();
        await AssertCodeAsync("archive_source_truncated", () => PortableArchiveZipReader.OpenAsync(source, budget));
        Assert.Equal(0, budget.CurrentBytes);
    }

    private async Task ReadValidatedAsync(byte[] bytes, byte[] payload)
    {
        using var physical = new CountingReadStream(bytes);
        await using var source = Source(physical, shortReads: 11);
        var budget = Budget();
        await using (var reader = await PortableArchiveZipReader.OpenAsync(source, budget))
        {
            foreach (var item in reader.Archive.Entries)
            {
                await using var entry = await item.OpenAsync();
                using var contents = new MemoryStream();
                await entry.CopyToAsync(contents);
                Assert.Equal(payload, contents.ToArray());
            }
        }
        Assert.Equal(0, physical.SyncCalls);
        Assert.Equal(0, budget.CurrentBytes);
    }
    private static RangePortableArchiveSource Source(CountingReadStream physical, int shortReads = int.MaxValue) =>
        new(physical.Length, async (offset, buffer, ct) =>
        {
            physical.Position = offset;
            return await physical.ReadAsync(buffer[..Math.Min(buffer.Length, shortReads)], ct);
        });
    private static byte[] Payload(int size, bool compressible)
    {
        var bytes = new byte[size];
        if (!compressible) new Random(12345).NextBytes(bytes);
        else Array.Fill(bytes, (byte)'a');
        return bytes;
    }
    private static async Task WriteZipAsync(Stream destination, byte[] payload, CompressionLevel compression, int count)
    {
        await using var archive = await ZipArchive.CreateAsync(destination, ZipArchiveMode.Create, true, null);
        for (var i = 0; i < count; i++)
        {
            var entry = archive.CreateEntry($"media/books/{i:x32}/book.epub", compression);
            entry.LastWriteTime = Timestamp;
            await using var stream = await entry.OpenAsync();
            await stream.WriteAsync(payload.AsMemory());
        }
    }
    private static async Task<byte[]> NativeZipAsync(byte[] payload, CompressionLevel compression, bool descriptor, int count = 1)
    {
        if (descriptor)
        {
            using var sink = new CountingSink(allowSync: true);
            await WriteZipAsync(sink, payload, compression, count);
            return sink.ToArray();
        }
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            for (var i = 0; i < count; i++)
            {
                var entry = archive.CreateEntry($"media/books/{i:x32}/book.epub", compression);
                entry.LastWriteTime = Timestamp;
                using var stream = entry.Open();
                stream.Write(payload);
            }
        return buffer.ToArray();
    }
    private static void AssertCode(string code, Action action) => Assert.Equal(code, Assert.Throws<PortableArchiveException>(action).Code);
    private static async Task AssertCodeAsync(string code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<PortableArchiveException>(action)).Code);
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void W16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private sealed class CountingSink(bool allowSync = false, bool discard = false) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }
        public int AsyncDisposals { get; private set; }
        public bool FailWrite { get; init; }
        public bool FailFlush { get; init; }
        public bool PartialWriteFailure { get; init; }
        public byte[] ToArray() => _buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private void Sync() { SyncCalls++; if (!allowSync) throw new InvalidOperationException("Physical synchronous IO forbidden."); }
        public override void Write(byte[] buffer, int offset, int count) { Sync(); if (!discard) _buffer.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Sync(); if (!discard) _buffer.Write(buffer); }
        public override void WriteByte(byte value) { Sync(); if (!discard) _buffer.WriteByte(value); }
        public override void Flush() { Sync(); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncCalls++;
            if (FailWrite) throw new IOException("Injected write failure.");
            if (PartialWriteFailure)
            {
                _buffer.Write(buffer.Span[..1]);
                throw new IOException("Injected failure after partial write.");
            }
            if (!discard) _buffer.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncCalls++;
            if (FailFlush) throw new IOException("Injected flush failure.");
            return Task.CompletedTask;
        }
        public override ValueTask DisposeAsync() { AsyncDisposals++; return ValueTask.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class CountingReadStream : Stream
    {
        private readonly MemoryStream _buffer;
        public CountingReadStream(byte[] bytes) => _buffer = new(bytes, false);
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => _buffer.Position = value; }
        private int Sync() { SyncCalls++; throw new InvalidOperationException("Physical synchronous IO forbidden."); }
        public override int Read(byte[] buffer, int offset, int count) => Sync();
        public override int Read(Span<byte> buffer) => Sync();
        public override int ReadByte() => Sync();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncCalls++;
            return ValueTask.FromResult(_buffer.Read(buffer.Span));
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _buffer.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

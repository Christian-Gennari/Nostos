using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Xunit;
using Xunit.Abstractions;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// End-to-end working-set and scratch measurement for the streaming archive
/// engine on a library whose media total exceeds 4 GiB, plus a scaled version
/// that always runs in ordinary CI. Media is generated deterministically on
/// demand; no large fixture is committed and no archive-sized buffer is
/// allocated.
///
/// <para>The opt-in run is:</para>
/// <code>
/// NOSTOS_RUN_LARGE_PORTABILITY_TESTS=1 \
///   dotnet test Nostos.Backend.Tests/Nostos.Backend.Tests.csproj \
///   --filter "FullyQualifiedName~PortableArchiveLargeMeasurementTests"
/// </code>
/// </summary>
public sealed class PortableArchiveLargeMeasurementTests(ITestOutputHelper output)
{
    private const long FourGiB = 4L * 1024 * 1024 * 1024;
    private const int CopyChunkBytes = 1024 * 1024;

    private static readonly Scenario ScaledScenario = new(
        Name: "scaled (ordinary CI)",
        BigMediaBytes: 40L * 1024 * 1024,
        SmallMediaCount: 56,
        SmallMediaBytes: 1024 * 1024,
        Seed: 20261004);

    // One entry alone is larger than 4 GiB so ZIP64 entry sizes are exercised;
    // 96 further 1 MiB entries exercise the per-entry paths.
    private static readonly Scenario LargeScenario = new(
        Name: "greater than 4 GiB",
        BigMediaBytes: FourGiB + (256L * 1024 * 1024),
        SmallMediaCount: 96,
        SmallMediaBytes: 1024 * 1024,
        Seed: 20261004);

    [LargePortabilityFact]
    [Trait("Category", "LargeArchive")]
    public async Task Export_and_prepare_of_greater_than_4gib_archive_keep_bounded_memory_and_zero_scratch()
    {
        await MeasureAsync(LargeScenario);
    }

    [Fact]
    public async Task Export_and_prepare_of_scaled_archive_keep_bounded_memory_and_zero_scratch()
    {
        await MeasureAsync(ScaledScenario);
    }

    private async Task MeasureAsync(Scenario scenario)
    {
        var totalMediaBytes = checked(
            scenario.BigMediaBytes + (scenario.SmallMediaCount * (long)scenario.SmallMediaBytes));

        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var populated = await PopulateAsync(library, scenario);
        var service = new PortableArchiveService(
            library.Db,
            populated.Storage,
            NullLogger<PortableArchiveService>.Instance);

        // ---------------------------------------------------------------
        // Phase 1: export to a counting, non-seekable, sync-forbidding
        // discard sink. This is the measured export: the engine must never
        // call a synchronous method on the physical destination.
        // ---------------------------------------------------------------
        using var exportScratch = new ScratchWatch("nostos-portable-export-");
        var exportBudget = new PortableArchiveBufferBudget(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        var exportSink = new CountingDiscardStream();

        var managedBeforeExport = SnapshotManagedHeap();
        using var exportSampler = new WorkingSetSampler();
        var exportTimer = Stopwatch.StartNew();
        var exported = await service.ExportAsync(
            exportSink,
            exportBudget,
            CancellationToken.None);
        exportTimer.Stop();
        var peakExportWorkingSet = exportSampler.PeakWorkingSet;
        var managedAfterExport = SnapshotManagedHeap();

        exportSink.SyncCalls.Should().Be(0, "Kestrel-style destinations forbid synchronous IO");
        exportSink.BytesWritten.Should().BeGreaterThanOrEqualTo(
            totalMediaBytes,
            "stored media is written without compression, so the archive cannot be smaller than the media");
        exportBudget.HighWaterBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        exportBudget.HighWaterBytes.Should().BeGreaterThanOrEqualTo(
            PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes,
            "the capture lease must be accounted by the operation budget");
        exportBudget.CurrentBytes.Should().Be(0);
        exported.MediaFiles.Should().Be(populated.MediaEntries);
        exported.MediaBytes.Should().Be(totalMediaBytes);
        exportScratch.ObservedNewNames.Should().BeEmpty(
            "the streamed export must never create an export scratch directory");
        exportScratch.NewNamesAfter.Should().BeEmpty();
        (managedAfterExport - managedBeforeExport).Should().BeLessThan(
            512L * 1024 * 1024,
            "the managed heap must not retain anything close to the archive size");
        (peakExportWorkingSet - exportSampler.BaselineWorkingSet).Should().BeLessThan(
            1024L * 1024 * 1024,
            "the process working set must stay far below the archive size");

        if (scenario.BigMediaBytes > FourGiB)
        {
            exportSink.BytesWritten.Should().BeGreaterThan(
                FourGiB,
                "the measured archive must exceed 4 GiB");
        }

        // ---------------------------------------------------------------
        // Phase 2: a test-owned export to a temp file. The import measurement
        // needs a range-addressable archive; regenerating a full ZIP64
        // container through a synthetic range source would require a
        // parallel test-only ZIP writer, so one throw-away file (deleted in
        // finally) is the smallest-scratch option.
        // ---------------------------------------------------------------
        var archivePath = Path.Combine(
            Path.GetTempPath(),
            $"nostos-s9-measure-{Guid.NewGuid():N}.nostos");
        try
        {
            EnsureFreeDiskSpace(archivePath, totalMediaBytes);

            await using (var archiveFile = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await service.ExportAsync(archiveFile, CancellationToken.None);
                await archiveFile.FlushAsync(CancellationToken.None);
            }

            var archiveLength = new FileInfo(archivePath).Length;
            archiveLength.Should().BeGreaterThanOrEqualTo(totalMediaBytes);

            if (scenario.BigMediaBytes > FourGiB)
            {
                (await ContainsZip64EndRecordAsync(archivePath)).Should().BeTrue(
                    "a stored entry and central-directory offset above 4 GiB must use ZIP64 framing");
            }

            // -----------------------------------------------------------
            // Phase 3: direct native-writer stress with the same entry
            // inventory through a capture sink we own, so the bounded
            // synchronous-write high-water marks are measurable.
            // -----------------------------------------------------------
            var capture = await MeasureCaptureSinkAsync(archivePath);

            // -----------------------------------------------------------
            // Phase 4: prepared-import measurement. The archive is read by
            // range through FilePortableArchiveSource; the staging double
            // hashes and discards every media byte while still enforcing the
            // declared length and SHA-256.
            // -----------------------------------------------------------
            using var importScratch = new ScratchWatch("nostos-portable-import-");
            var prepareBudget = new PortableArchiveBufferBudget(
                PortableArchiveLimits.MaxExplicitBufferBytes);
            var physicalSource = new FilePortableArchiveSource(archivePath);
            var recordingSource = new TrackingArchiveSource(physicalSource);
            var staging = new DiscardingPortableImportStaging();
            var reader = new PortableArchiveReader();

            var managedBeforeImport = SnapshotManagedHeap();
            using var importSampler = new WorkingSetSampler();
            var importTimer = Stopwatch.StartNew();
            var prepared = await reader.PrepareImportAsync(
                recordingSource,
                staging,
                progress: null,
                CancellationToken.None,
                resourceBudget: prepareBudget);
            importTimer.Stop();
            var peakImportWorkingSet = importSampler.PeakWorkingSet;
            var managedAfterImport = SnapshotManagedHeap();

            await recordingSource.DisposeAsync();

            prepareBudget.HighWaterBytes.Should().BeLessThanOrEqualTo(
                PortableArchiveLimits.MaxExplicitBufferBytes);
            prepareBudget.CurrentBytes.Should().Be(0);
            prepared.IntegrityVerified.Should().BeTrue();
            prepared.Metadata.ArchiveBytes.Should().Be(archiveLength);
            prepared.Metadata.MediaFiles.Should().Be(populated.MediaEntries);
            prepared.Metadata.MediaBytes.Should().Be(totalMediaBytes);
            prepared.Metadata.Counts.MediaEntries.Should().Be(populated.MediaEntries);
            prepared.Metadata.DataBytes.Should().BeGreaterThan(0);
            prepared.Metadata.DataSha256.Should().HaveLength(64);
            prepared.Media.Should().HaveCount(populated.MediaEntries);

            recordingSource.ReadCalls.Should().BeGreaterThan(0);
            recordingSource.MaxRequestedBytes.Should().BeLessThanOrEqualTo(
                PortableArchiveLimits.MaxPrefetchedZipTailBytes,
                "every source access is a bounded range read, never a whole-archive fetch");
            recordingSource.TotalBytesRead.Should().BeLessThanOrEqualTo(
                (archiveLength * 2) + (64L * 1024 * 1024),
                "range traffic stays within a small multiple of the archive size");
            staging.ScratchBytesWritten.Should().Be(0);
            importScratch.NewNamesAfter.Should().BeEmpty(
                "preparation writes no engine scratch of its own");

            var expectedBigSha = populated.BookFileSha256[populated.BigBookId];
            prepared.Media
                .Single(item => item.Descriptor.BookId == populated.BigBookId)
                .Descriptor.Sha256.Should().Be(
                    expectedBigSha,
                    "the staged descriptor must carry the independently generated hash");
            staging.MediaBytesCompleted.Should().Be(totalMediaBytes);

            (managedAfterImport - managedBeforeImport).Should().BeLessThan(
                512L * 1024 * 1024,
                "preparation must not retain media in the managed heap");
            (peakImportWorkingSet - importSampler.BaselineWorkingSet).Should().BeLessThan(
                1024L * 1024 * 1024,
                "preparation must not approach the archive size in working set");

            WriteResultsTable(
                scenario,
                archiveLength,
                totalMediaBytes,
                populated.MediaEntries,
                exported,
                exportBudget,
                exportTimer.Elapsed,
                capture,
                prepareBudget,
                importTimer.Elapsed,
                managedAfterExport - managedBeforeExport,
                peakExportWorkingSet - exportSampler.BaselineWorkingSet,
                managedAfterImport - managedBeforeImport,
                peakImportWorkingSet - importSampler.BaselineWorkingSet,
                recordingSource,
                staging);
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    private void WriteResultsTable(
        Scenario scenario,
        long archiveLength,
        long mediaBytes,
        int mediaEntries,
        PortableExportResult exported,
        PortableArchiveBufferBudget exportBudget,
        TimeSpan exportDuration,
        CaptureMeasurement capture,
        PortableArchiveBufferBudget prepareBudget,
        TimeSpan importDuration,
        long managedExportGrowth,
        long workingSetExportGrowth,
        long managedImportGrowth,
        long workingSetImportGrowth,
        TrackingArchiveSource source,
        DiscardingPortableImportStaging staging)
    {
        output.WriteLine($"=== portable archive resource measurement: {scenario.Name} ===");
        output.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), {Environment.ProcessorCount} cores");
        output.WriteLine("| metric | value |");
        output.WriteLine("|---|---:|");
        output.WriteLine($"| media bytes | {mediaBytes} |");
        output.WriteLine($"| media entries | {mediaEntries} |");
        output.WriteLine($"| archive bytes (streamed export) | {exported.MediaBytes} |");
        output.WriteLine($"| physical archive bytes | {archiveLength} |");
        output.WriteLine($"| export duration | {exportDuration.TotalSeconds:F2} s |");
        output.WriteLine($"| export throughput | {Throughput(exported.MediaBytes, exportDuration):F0} MiB/s (media) |");
        output.WriteLine($"| export budget high-water | {exportBudget.HighWaterBytes} bytes |");
        output.WriteLine($"| export budget cap | {exportBudget.MaxBytes} bytes |");
        output.WriteLine($"| capture pending high-water (direct) | {capture.SynchronousHighWaterBytes} bytes |");
        output.WriteLine($"| capture max single sync write (direct) | {capture.MaxSingleSynchronousWriteBytes} bytes |");
        output.WriteLine($"| capture physical sync calls | {capture.SyncCalls} |");
        output.WriteLine($"| export managed heap growth | {managedExportGrowth} bytes |");
        output.WriteLine($"| export working-set growth | {workingSetExportGrowth} bytes |");
        output.WriteLine($"| export scratch names observed | {0} |");
        output.WriteLine($"| import prepare duration | {importDuration.TotalSeconds:F2} s |");
        output.WriteLine($"| import prepare throughput | {Throughput(archiveLength, importDuration):F0} MiB/s (archive) |");
        output.WriteLine($"| import budget high-water | {prepareBudget.HighWaterBytes} bytes |");
        output.WriteLine($"| import budget cap | {prepareBudget.MaxBytes} bytes |");
        output.WriteLine($"| source async range read calls | {source.ReadCalls} |");
        output.WriteLine($"| source max single read request | {source.MaxRequestedBytes} bytes |");
        output.WriteLine($"| source total bytes read | {source.TotalBytesRead} bytes |");
        output.WriteLine($"| import managed heap growth | {managedImportGrowth} bytes |");
        output.WriteLine($"| import working-set growth | {workingSetImportGrowth} bytes |");
        output.WriteLine($"| staged media bytes (discarded) | {staging.MediaBytesCompleted} |");
        output.WriteLine($"| engine scratch bytes written | {staging.ScratchBytesWritten} |");
    }

    private static double Throughput(long bytes, TimeSpan duration) =>
        duration.TotalSeconds <= 0
            ? 0
            : bytes / (1024d * 1024d) / duration.TotalSeconds;

    private static async Task<CaptureMeasurement> MeasureCaptureSinkAsync(string archivePath)
    {
        var budget = new PortableArchiveBufferBudget(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        var physical = new CountingDiscardStream();
        var capture = new BoundedSynchronousCaptureSink(physical, budget);
        try
        {
            await using var source = File.OpenRead(archivePath);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            await using (var target = await ZipArchive.CreateAsync(
                capture,
                ZipArchiveMode.Create,
                leaveOpen: true,
                entryNameEncoding: null,
                CancellationToken.None))
            {
                var buffer = new byte[CopyChunkBytes];
                foreach (var entry in archive.Entries)
                {
                    var level = entry.FullName.EndsWith(".json", StringComparison.Ordinal)
                        ? CompressionLevel.Optimal
                        : CompressionLevel.NoCompression;
                    var outputEntry = target.CreateEntry(entry.FullName, level);
                    await using var output = await outputEntry.OpenAsync(CancellationToken.None);
                    await using var input = entry.Open();
                    long copied = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, CancellationToken.None);
                        if (read == 0)
                            break;
                        copied = checked(copied + read);
                        await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    }

                    copied.Should().Be(entry.Length);
                }
            }

            await capture.CompleteAsync(CancellationToken.None);
        }
        finally
        {
            await capture.DisposeAsync();
        }

        physical.SyncCalls.Should().Be(0);
        physical.AsyncWriteCalls.Should().BeGreaterThan(0);
        capture.SynchronousHighWaterBytes.Should().BeGreaterThan(0);
        capture.SynchronousHighWaterBytes.Should().BeLessThan(
            PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes);
        capture.MaxSingleSynchronousWriteBytes.Should().BeLessThan(
            PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes);
        budget.HighWaterBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        budget.CurrentBytes.Should().Be(0);

        return new CaptureMeasurement(
            capture.SynchronousHighWaterBytes,
            capture.MaxSingleSynchronousWriteBytes,
            physical.SyncCalls,
            capture.BytesWritten);
    }

    private static async Task<PopulatedLibrary> PopulateAsync(
        LocalPortableTestLibrary library,
        Scenario scenario)
    {
        var storage = new GeneratedMediaStorage();
        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Measurement Work",
            NormalizedTitle = "MEASUREMENT WORK",
            NormalizedAuthor = string.Empty,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        };
        library.Db.Works.Add(work);

        var bigBookId = Guid.NewGuid();
        AddBook(library.Db, work, bigBookId, "big.epub");
        storage.RegisterBook(
            bigBookId,
            fileName: "book.epub",
            contentType: "application/epub+zip",
            length: scenario.BigMediaBytes,
            seed: scenario.Seed);

        long totalMediaBytes = scenario.BigMediaBytes;
        for (var index = 0; index < scenario.SmallMediaCount; index++)
        {
            var bookId = Guid.NewGuid();
            AddBook(library.Db, work, bookId, $"small-{index:000}.epub");
            storage.RegisterBook(
                bookId,
                fileName: "book.epub",
                contentType: "application/epub+zip",
                length: scenario.SmallMediaBytes,
                seed: scenario.Seed + 1 + index);
            totalMediaBytes = checked(totalMediaBytes + scenario.SmallMediaBytes);
        }

        await library.Db.SaveChangesAsync();

        var mediaEntries = 1 + scenario.SmallMediaCount;
        var expectedShas = storage.RegisteredSha256;
        expectedShas.Should().HaveCount(mediaEntries);
        expectedShas[bigBookId].Should().NotBeNullOrWhiteSpace();

        return new PopulatedLibrary(
            storage,
            bigBookId,
            mediaEntries,
            totalMediaBytes,
            expectedShas);
    }

    private static void AddBook(
        Nostos.Backend.Data.NostosDbContext db,
        WorkModel work,
        Guid bookId,
        string sourceFileName)
    {
        db.Books.Add(new EBookModel
        {
            Id = bookId,
            WorkId = work.Id,
            Work = work,
            Title = $"Measurement Book {sourceFileName}",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = sourceFileName,
            },
        });
    }

    private static async Task<bool> ContainsZip64EndRecordAsync(string archivePath)
    {
        const int tailBytes = 256 * 1024;
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyChunkBytes,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var length = stream.Length;
        var start = Math.Max(0, length - tailBytes);
        var tail = new byte[length - start];
        stream.Seek(start, SeekOrigin.Begin);
        var read = 0;
        while (read < tail.Length)
        {
            var count = await stream.ReadAsync(tail.AsMemory(read), CancellationToken.None);
            if (count == 0)
                break;
            read += count;
        }

        // ZIP64 end-of-central-directory signature 0x06064b50, little endian.
        for (var index = 0; index + 4 <= read; index++)
        {
            if (tail[index] == 0x50
                && tail[index + 1] == 0x4B
                && tail[index + 2] == 0x06
                && tail[index + 3] == 0x06)
            {
                return true;
            }
        }

        return false;
    }

    private static long SnapshotManagedHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static void EnsureFreeDiskSpace(string path, long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
            return;
        var free = new DriveInfo(root).AvailableFreeSpace;
        const long headroom = 2L * 1024 * 1024 * 1024;
        if (free < requiredBytes + headroom)
        {
            throw new InvalidOperationException(
                $"The >4 GiB measurement needs about {requiredBytes + headroom} bytes free on {root} "
                + $"but only {free} bytes are available.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private sealed record Scenario(
        string Name,
        long BigMediaBytes,
        int SmallMediaCount,
        int SmallMediaBytes,
        int Seed);

    private sealed record PopulatedLibrary(
        GeneratedMediaStorage Storage,
        Guid BigBookId,
        int MediaEntries,
        long TotalMediaBytes,
        IReadOnlyDictionary<Guid, string> BookFileSha256);

    private sealed record CaptureMeasurement(
        int SynchronousHighWaterBytes,
        int MaxSingleSynchronousWriteBytes,
        int SyncCalls,
        long BytesWritten);

    // ------------------------------------------------------------------
    // Deterministic generated media
    // ------------------------------------------------------------------

    /// <summary>
    /// Stream of deterministic patterned bytes: a 1 MiB pseudo-random block is
    /// repeated for the whole declared length. Every open produces identical
    /// bytes; the hash is computed while the bytes are generated and never
    /// stored. Synchronous reads throw so the engine can only consume it
    /// asynchronously.
    /// </summary>
    internal sealed class GeneratedAssetStream(long length, int seed) : Stream
    {
        internal const int BlockBytes = 1024 * 1024;
        private readonly byte[] _block = CreateBlock(seed);
        private long _position;

        internal static byte[] CreateBlock(int seed)
        {
            var block = new byte[BlockBytes];
            new Random(seed).NextBytes(block);
            return block;
        }

        internal static void Fill(byte[] block, Memory<byte> destination, long absoluteOffset)
        {
            var written = 0;
            while (written < destination.Length)
            {
                var blockOffset = (int)((absoluteOffset + written) % block.Length);
                var chunk = Math.Min(destination.Length - written, block.Length - blockOffset);
                block.AsSpan(blockOffset, chunk).CopyTo(destination.Span[written..]);
                written += chunk;
            }
        }

        /// <summary>SHA-256 of the exact bytes this stream will produce.</summary>
        public static string Hash(long length, int seed)
        {
            var block = CreateBlock(seed);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BlockBytes];
            long absoluteOffset = 0;
            while (absoluteOffset < length)
            {
                var count = (int)Math.Min(buffer.Length, length - absoluteOffset);
                Fill(block, buffer.AsMemory(0, count), absoluteOffset);
                hash.AppendData(buffer, 0, count);
                absoluteOffset += count;
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Generated media is async-only.");

        public override int Read(Span<byte> buffer) =>
            throw new NotSupportedException("Generated media is async-only.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - _position);
            if (count <= 0)
                return ValueTask.FromResult(0);

            Fill(_block, buffer[..count], _position);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Synthetic <see cref="IBookAssetStorage"/> that serves generated bytes
    /// for a known set of books and precomputes each SHA-256 while generating.
    /// Saving/deleting is not exercised by the export/import measurement.
    /// </summary>
    internal sealed class GeneratedMediaStorage : IBookAssetStorage
    {
        private static readonly DateTimeOffset FixedModified =
            new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private readonly Dictionary<Guid, Entry> _registered = new();
        private readonly Dictionary<Guid, string> _sha256 = new();

        public IReadOnlyDictionary<Guid, string> RegisteredSha256 => _sha256;

        public void RegisterBook(
            Guid bookId,
            string fileName,
            string contentType,
            long length,
            int seed)
        {
            _registered[bookId] = new Entry(fileName, contentType, length, seed);
            _sha256[bookId] = GeneratedAssetStream.Hash(length, seed);
        }

        public Task<StoredAssetInfo?> GetBookFileInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult(_registered.TryGetValue(bookId, out var entry)
                ? Info(bookId, entry)
                : null);

        public Task<StoredAssetRead?> OpenBookFileAsync(
            Guid bookId,
            StorageByteRange? range = null,
            CancellationToken ct = default)
        {
            if (!_registered.TryGetValue(bookId, out var entry))
                return Task.FromResult<StoredAssetRead?>(null);
            if (range is not null)
                throw new NotSupportedException("The measurement path opens whole media.");

            return Task.FromResult<StoredAssetRead?>(
                new StoredAssetRead(
                    Info(bookId, entry),
                    new GeneratedAssetStream(entry.Length, entry.Seed)));
        }

        public Task<string> SaveBookFileAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> AdoptBookFileAsync(
            Guid bookId,
            string sourcePath,
            string fileName,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteBookFileAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult(_registered.Remove(bookId));

        public Task DeleteBookFilesAsync(
            Guid bookId,
            CancellationToken ct = default)
        {
            _registered.Remove(bookId);
            return Task.CompletedTask;
        }

        public Task<string> SaveBookCoverAsync(
            Guid bookId,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetInfo?> GetBookCoverInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetInfo?>(null);

        public Task<StoredAssetRead?> OpenBookCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(null);

        public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetInfo?>(null);

        public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(
            Guid bookId,
            int width,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(null);

        public Task<bool> DeleteCoverAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        private static StoredAssetInfo Info(Guid bookId, Entry entry) =>
            new(
                entry.FileName,
                entry.ContentType,
                entry.Length,
                $"\"generated-{bookId:N}-{entry.Seed}\"",
                FixedModified);

        private sealed record Entry(
            string FileName,
            string ContentType,
            long Length,
            int Seed);
    }

    // ------------------------------------------------------------------
    // Instrumented destinations and sources
    // ------------------------------------------------------------------

    /// <summary>
    /// Counts and hashes every byte, stores nothing, rejects synchronous
    /// writes/flush exactly like Kestrel's response body.
    /// </summary>
    internal sealed class CountingDiscardStream : Stream
    {
        private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long BytesWritten { get; private set; }
        public int SyncCalls { get; private set; }
        public int AsyncWriteCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int AsyncDisposals { get; private set; }
        public long MaxSingleWriteBytes { get; private set; }

        public string GetHashHex() =>
            Convert.ToHexString((_hash ?? throw new ObjectDisposedException(nameof(CountingDiscardStream)))
                .GetHashAndReset()).ToLowerInvariant();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => RejectSync();

        public override void Write(ReadOnlySpan<byte> buffer) => RejectSync();

        public override void WriteByte(byte value) => RejectSync();

        public override void Flush() => RejectSync();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncWriteCalls++;
            BytesWritten = checked(BytesWritten + buffer.Length);
            MaxSingleWriteBytes = Math.Max(MaxSingleWriteBytes, buffer.Length);
            _hash!.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCalls++;
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            AsyncDisposals++;
            _hash?.Dispose();
            _hash = null;
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private void RejectSync()
        {
            SyncCalls++;
            throw new InvalidOperationException(
                "Synchronous operations are disallowed. "
                + "Call WriteAsync or set AllowSynchronousIO to true instead.");
        }
    }

    /// <summary>
    /// Wraps a position-independent source and records every asynchronous
    /// range read. The interface exposes no synchronous read at all, so a
    /// successful preparation proves all physical access was asynchronous.
    /// </summary>
    internal sealed class TrackingArchiveSource(IPortableArchiveSource inner) : IPortableArchiveSource
    {
        public long Length => inner.Length;
        public int ReadCalls { get; private set; }
        public int MaxRequestedBytes { get; private set; }
        public long TotalBytesRead { get; private set; }
        public long MinOffset { get; private set; } = long.MaxValue;
        public long MaxEnd { get; private set; }

        public async ValueTask<int> ReadAtAsync(
            long offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            MaxRequestedBytes = Math.Max(MaxRequestedBytes, buffer.Length);
            var read = await inner.ReadAtAsync(offset, buffer, cancellationToken);
            TotalBytesRead = checked(TotalBytesRead + read);
            MinOffset = Math.Min(MinOffset, offset);
            MaxEnd = Math.Max(MaxEnd, offset + read);
            return read;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// Test-owned staging provider that verifies length and SHA-256 for every
    /// media item but discards the bytes instead of storing them. It never
    /// writes a file, so its scratch accounting is exactly zero by
    /// construction; the descriptors still have to match the archive exactly.
    /// </summary>
    internal sealed class DiscardingPortableImportStaging : IPortableImportStaging
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Area> _areas = new();

        public long ScratchBytesWritten => 0;

        public long MediaBytesCompleted { get; private set; }

        public int MediaFilesCompleted { get; private set; }

        public Task<PortableStagingId> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid();
            lock (_gate)
                _areas.Add(id, new Area());
            return Task.FromResult(new PortableStagingId(id));
        }

        public Task<PortableStagingWrite> OpenMediaWriteAsync(
            PortableStagingId stagingId,
            PortableArchiveMediaEntry descriptor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor.Length < 0 || descriptor.Length > PortableArchiveLimits.MaxSingleEntryBytes)
            {
                throw new PortableStagingException(
                    PortableStagingException.LimitExceededCode,
                    "The declared media length is outside the staging limits.");
            }

            lock (_gate)
            {
                var area = Resolve(stagingId);
                var key = (descriptor.BookId, descriptor.Kind, descriptor.Path);
                if (!area.Media.TryAdd(key, new MediaItem(descriptor, Guid.NewGuid().ToString("N"))))
                {
                    throw new PortableStagingException(
                        PortableStagingException.ConflictCode,
                        "A write for this media item is already open or completed.");
                }

                var item = area.Media[key];
                var write = new PortableStagingWrite(
                    new PortableStagedMediaReference(item.Reference),
                    new HashingDiscardWriteStream(descriptor.Length, item));
                area.MediaByHandle[write] = key;
                return Task.FromResult(write);
            }
        }

        public Task CompleteMediaAsync(
            PortableStagingId stagingId,
            PortableStagingWrite write,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(write);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                var area = Resolve(stagingId);
                if (!area.MediaByHandle.TryGetValue(write, out var key))
                {
                    throw new PortableStagingException(
                        PortableStagingException.NotFoundCode,
                        "No media item matches the write handle.");
                }

                var item = area.Media[key];
                if (item.Completed)
                    return Task.CompletedTask;

                var stream = (HashingDiscardWriteStream)write.Stream;
                stream.SealIfNeeded();
                if (stream.BytesWritten != item.Descriptor.Length
                    || !string.Equals(
                        stream.Sha256,
                        item.Descriptor.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    area.Media.Remove(key);
                    area.MediaByHandle.Remove(write);
                    throw new PortableStagingException(
                        PortableStagingException.IntegrityMismatchCode,
                        "Discarded media bytes do not match the descriptor bound when the write was opened.");
                }

                item.Completed = true;
                MediaFilesCompleted++;
                MediaBytesCompleted = checked(MediaBytesCompleted + stream.BytesWritten);
                area.MediaByHandle.Remove(write);
                return Task.CompletedTask;
            }
        }

        public Task<Stream> OpenMediaReadAsync(
            PortableStagingId stagingId,
            PortableStagedMediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The discarding staging provider stores no media bytes.");

        public Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var area = Resolve(stagingId);
                return Task.FromResult<IReadOnlyList<PortablePreparedMedia>>(
                    Inventory(area));
            }
        }

        public Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
            PortableStagingId stagingId,
            PortableArchivePayload descriptor,
            CancellationToken cancellationToken = default) =>
            OpenPayloadWrite(stagingId, descriptor, manifest: false, cancellationToken);

        public Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
            PortableStagingId stagingId,
            PortableArchivePayload descriptor,
            CancellationToken cancellationToken = default) =>
            OpenPayloadWrite(stagingId, descriptor, manifest: true, cancellationToken);

        public Task CompleteDataAsync(
            PortableStagingId stagingId,
            PortableStagingPayloadWrite write,
            CancellationToken cancellationToken = default) =>
            CompletePayloadAsync(stagingId, write, manifest: false, cancellationToken);

        public Task CompleteManifestAsync(
            PortableStagingId stagingId,
            PortableStagingPayloadWrite write,
            CancellationToken cancellationToken = default) =>
            CompletePayloadAsync(stagingId, write, manifest: true, cancellationToken);

        public Task<Stream> OpenDataReadAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default) =>
            OpenPayloadReadAsync(stagingId, manifest: false, cancellationToken);

        public Task<Stream> OpenManifestReadAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default) =>
            OpenPayloadReadAsync(stagingId, manifest: true, cancellationToken);

        public Task CommitPreparedImportAsync(
            PortableStagingId stagingId,
            PreparedPortableImportMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var area = Resolve(stagingId);
                var inventory = Inventory(area);
                if (inventory.Count != metadata.MediaFiles
                    || inventory.Sum(item => item.Descriptor.Length) != metadata.MediaBytes)
                {
                    throw new PortableStagingException(
                        PortableStagingException.IntegrityMismatchCode,
                        "The staged media inventory does not match the prepared descriptor.");
                }

                if (area.Data is null
                    || !area.Data.Completed
                    || area.Data.Length != metadata.DataBytes
                    || !string.Equals(
                        area.Data.Sha256,
                        metadata.DataSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new PortableStagingException(
                        PortableStagingException.IntegrityMismatchCode,
                        "The staged relational payload does not match the prepared descriptor.");
                }

                area.Prepared = metadata;
                return Task.CompletedTask;
            }
        }

        public Task<IPreparedPortableImport> RebuildPreparedImportAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var area = Resolve(stagingId);
                var prepared = area.Prepared
                    ?? throw new PortableStagingException(
                        PortableStagingException.NotFoundCode,
                        "No prepared descriptor has been committed.");
                return Task.FromResult<IPreparedPortableImport>(
                    new PortablePreparedImport(prepared, Inventory(area)));
            }
        }

        public Task DeleteAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _areas.Remove(stagingId.Value);
                return Task.CompletedTask;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task<PortableStagingPayloadWrite> OpenPayloadWrite(
            PortableStagingId stagingId,
            PortableArchivePayload descriptor,
            bool manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = manifest
                ? PortableArchiveLimits.MaxManifestBytes
                : PortableArchiveLimits.MaxDataBytes;
            if (descriptor.Length < 0 || descriptor.Length > limit)
            {
                throw new PortableStagingException(
                    PortableStagingException.LimitExceededCode,
                    "The declared payload length is outside the staging limits.");
            }

            lock (_gate)
            {
                var area = Resolve(stagingId);
                var item = new PayloadItem(descriptor);
                if (manifest)
                    area.Manifest = item;
                else
                    area.Data = item;
                var write = new PortableStagingPayloadWrite(item.Buffer);
                return Task.FromResult(write);
            }
        }

        private Task CompletePayloadAsync(
            PortableStagingId stagingId,
            PortableStagingPayloadWrite write,
            bool manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var area = Resolve(stagingId);
                var item = manifest ? area.Manifest : area.Data;
                if (item is null || !ReferenceEquals(item.Buffer, write.Stream))
                {
                    throw new PortableStagingException(
                        PortableStagingException.NotFoundCode,
                        "No payload matches the write handle.");
                }

                item.Completed = true;
                item.Length = item.Buffer.Length;
                item.Sha256 = Convert.ToHexString(
                    SHA256.HashData(item.Buffer.ToArray())).ToLowerInvariant();
                if (item.Length != item.Descriptor.Length
                    || !string.Equals(
                        item.Sha256,
                        item.Descriptor.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new PortableStagingException(
                        PortableStagingException.IntegrityMismatchCode,
                        "Staged payload bytes do not match the descriptor bound when the write was opened.");
                }

                return Task.CompletedTask;
            }
        }

        private Task<Stream> OpenPayloadReadAsync(
            PortableStagingId stagingId,
            bool manifest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var area = Resolve(stagingId);
                var item = manifest ? area.Manifest : area.Data;
                if (item is null || !item.Completed)
                {
                    throw new PortableStagingException(
                        PortableStagingException.NotFoundCode,
                        "No completed payload is available.");
                }

                return Task.FromResult<Stream>(
                    new MemoryStream(item.Buffer.ToArray(), writable: false));
            }
        }

        private Area Resolve(PortableStagingId stagingId) =>
            stagingId.Value != Guid.Empty && _areas.TryGetValue(stagingId.Value, out var area)
                ? area
                : throw new PortableStagingException(
                    PortableStagingException.NotFoundCode,
                    "Unknown staging area.");

        private static IReadOnlyList<PortablePreparedMedia> Inventory(Area area) =>
            area.Media.Values
                .Where(item => item.Completed)
                .OrderBy(item => item.Descriptor.BookId)
                .ThenBy(item => item.Descriptor.Path, StringComparer.Ordinal)
                .Select(item => new PortablePreparedMedia(
                    item.Descriptor,
                    new PortableStagedMediaReference(item.Reference)))
                .ToArray();

        private sealed class Area
        {
            public Dictionary<(Guid, string, string), MediaItem> Media { get; } = new();

            public Dictionary<PortableStagingWrite, (Guid, string, string)> MediaByHandle { get; } = new();

            public PayloadItem? Data { get; set; }

            public PayloadItem? Manifest { get; set; }

            public PreparedPortableImportMetadata? Prepared { get; set; }
        }

        private sealed class MediaItem(PortableArchiveMediaEntry descriptor, string reference)
        {
            public PortableArchiveMediaEntry Descriptor { get; } = descriptor;

            public string Reference { get; } = reference;

            public bool Completed { get; set; }
        }

        private sealed class PayloadItem(PortableArchivePayload descriptor)
        {
            public PortableArchivePayload Descriptor { get; } = descriptor;

            public MemoryStream Buffer { get; } = new();

            public bool Completed { get; set; }

            public long Length { get; set; }

            public string? Sha256 { get; set; }
        }

        /// <summary>
        /// Counts and hashes bytes while discarding them. Synchronous writes
        /// throw: the reader must stage asynchronously.
        /// </summary>
        private sealed class HashingDiscardWriteStream(long limit, MediaItem item) : Stream
        {
            private IncrementalHash? _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            public long BytesWritten { get; private set; }

            public string? Sha256 { get; private set; }

            public void SealIfNeeded()
            {
                if (Sha256 is null)
                {
                    Sha256 = Convert.ToHexString(
                        (_hash ?? throw new ObjectDisposedException(nameof(HashingDiscardWriteStream)))
                            .GetHashAndReset()).ToLowerInvariant();
                }
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => Sha256 is null && !item.Completed;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException("Media staging is async-only.");

            public override void Write(ReadOnlySpan<byte> buffer) =>
                throw new NotSupportedException("Media staging is async-only.");

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BytesWritten + buffer.Length > limit)
                {
                    throw new PortableStagingException(
                        PortableStagingException.LimitExceededCode,
                        $"The staged item exceeds its {limit}-byte limit.");
                }

                BytesWritten += buffer.Length;
                _hash!.AppendData(buffer.Span);
                return ValueTask.CompletedTask;
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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

            public override ValueTask DisposeAsync()
            {
                SealIfNeeded();
                return ValueTask.CompletedTask;
            }
        }
    }

    // ------------------------------------------------------------------
    // Scratch and memory observers
    // ------------------------------------------------------------------

    /// <summary>
    /// Samples the process temp root every 50 ms while an operation runs and
    /// reports engine-named scratch entries that appeared after it started.
    /// The prefix is code-owned: no other test can create it, so an observation
    /// is genuinely the measured operation's scratch.
    /// </summary>
    internal sealed class ScratchWatch : IDisposable
    {
        private readonly string _root = Path.GetTempPath();
        private readonly string _prefix;
        private readonly HashSet<string> _before;
        private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
        private readonly Timer _timer;

        public ScratchWatch(string prefix)
        {
            _prefix = prefix;
            _before = Snapshot();
            _timer = new Timer(
                _ =>
                {
                    try
                    {
                        foreach (var name in Snapshot())
                        {
                            if (!_before.Contains(name))
                                _observed.Add(name);
                        }
                    }
                    catch
                    {
                        // A racing directory removal can invalidate an enumeration;
                        // the next sample retries.
                    }
                },
                state: null,
                dueTime: 50,
                period: 50);
        }

        public IReadOnlyCollection<string> ObservedNewNames
        {
            get
            {
                lock (_observed)
                    return _observed.ToArray();
            }
        }

        public IReadOnlyCollection<string> NewNamesAfter
        {
            get
            {
                var current = Snapshot();
                return current.Where(name => !_before.Contains(name)).ToArray();
            }
        }

        public void Dispose() => _timer.Dispose();

        private HashSet<string> Snapshot() =>
            Directory
                .EnumerateFileSystemEntries(_root)
                .Select(Path.GetFileName)
                .Where(name =>
                    name is not null
                    && name.StartsWith(_prefix, StringComparison.Ordinal))
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Records process working-set growth, sampled while an operation runs.</summary>
    internal sealed class WorkingSetSampler : IDisposable
    {
        private readonly CancellationTokenSource _stopped = new();
        private readonly Task _sampling;
        private long _peakWorkingSet;

        public WorkingSetSampler()
        {
            BaselineWorkingSet = Environment.WorkingSet;
            _peakWorkingSet = BaselineWorkingSet;
            _sampling = Task.Run(async () =>
            {
                while (!_stopped.IsCancellationRequested)
                {
                    var current = Environment.WorkingSet;
                    var observed = Interlocked.Read(ref _peakWorkingSet);
                    if (current > observed)
                        Interlocked.Exchange(ref _peakWorkingSet, current);
                    try
                    {
                        await Task.Delay(50, _stopped.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        public long BaselineWorkingSet { get; }

        public long PeakWorkingSet
        {
            get
            {
                var current = Environment.WorkingSet;
                var observed = Interlocked.Read(ref _peakWorkingSet);
                return Math.Max(observed, current);
            }
        }

        public void Dispose()
        {
            _stopped.Cancel();
            try
            {
                _sampling.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Sampling only.
            }

            _stopped.Dispose();
        }
    }
}

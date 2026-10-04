using System.Data.Common;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Xunit;
using Xunit.Abstractions;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableExportWriterTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task Export_streams_to_a_sync_forbidding_non_seekable_destination_and_round_trips()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            library.Db,
            library.Storage);

        var destination = new SyncForbiddingSink();
        var exported = await library.Portability().ExportAsync(destination);

        destination.SyncCalls.Should().Be(
            0,
            "native ZIP finalization must be absorbed by the bounded capture sink");
        exported.FormatVersion.Should().Be(1);
        exported.Counts.Books.Should().Be(4);
        exported.MediaFiles.Should().Be(5);

        var entries = ReadEntries(destination.ToArray());
        entries[0].Name.Should().Be(PortableArchiveFormat.DataPath);
        entries[^1].Name.Should().Be(PortableArchiveFormat.ManifestPath);
        entries
            .Skip(1)
            .Take(entries.Count - 2)
            .Should()
            .OnlyContain(entry => entry.Name.StartsWith("media/books/", StringComparison.Ordinal));

        var entryByPath = entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var manifest = JsonSerializer.Deserialize<PortableArchiveManifest>(
            entryByPath[PortableArchiveFormat.ManifestPath].Bytes,
            ManifestJsonOptions)!;
        Sha256Hex(entryByPath[PortableArchiveFormat.DataPath].Bytes)
            .Should().Be(manifest.Data.Sha256);
        entryByPath[PortableArchiveFormat.DataPath].Bytes.LongLength
            .Should().Be(manifest.Data.Length);
        manifest.Media.Should().HaveCount(5);
        foreach (var media in manifest.Media)
        {
            entryByPath[media.Path].Bytes.LongLength.Should().Be(media.Length);
            Sha256Hex(entryByPath[media.Path].Bytes).Should().Be(media.Sha256);
        }

        await using var target = await LocalPortableTestLibrary.CreateAsync();
        using var importedStream = new MemoryStream(destination.ToArray(), writable: false);
        var imported = await target.Portability().ImportAsync(importedStream);

        imported.IntegrityVerified.Should().BeTrue();
        imported.Counts.Should().Be(exported.Counts);
        imported.MediaFiles.Should().Be(exported.MediaFiles);

        var bookPath = manifest.Media
            .Single(media =>
                media.Kind == PortableArchiveFormat.BookMediaKind
                && media.BookId == ids.EpubBookId)
            .Path;
        (await PortableArchiveTestSupport.ReadBookAsync(target.Storage, ids.EpubBookId))
            .Should().Equal(entryByPath[bookPath].Bytes);
    }

    [Fact]
    public async Task Export_streams_archive_bytes_before_copying_media_and_never_stages_the_whole_archive()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(library, DeterministicBytes(256 * 1024));

        // The staging workaround wrote no destination byte until the whole
        // archive had been finalized in a temp FileStream. Streaming directly
        // means the data entry is already on the destination when the media
        // copy pass opens its first asset.
        var destination = new SyncForbiddingSink();
        long bytesBeforeMediaCopy = -1;
        var storage = new PortableExportSnapshotTests.ExportProbeStorage(library.Storage)
        {
            TargetBookId = bookId,
            OnCopyPassOpenAsync = _ =>
            {
                bytesBeforeMediaCopy = destination.BytesWritten;
                return Task.CompletedTask;
            },
        };
        var service = new PortableArchiveService(
            library.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance);

        await service.ExportAsync(destination);

        destination.SyncCalls.Should().Be(0);
        bytesBeforeMediaCopy.Should().BeGreaterThan(
            0,
            "the archive must stream as it is produced instead of staging export.nostos first");
        destination.BytesWritten.Should().BeGreaterThan(bytesBeforeMediaCopy);
        AssertCompleteArchive(destination.ToArray());
    }

    [Fact]
    public async Task Export_of_large_media_stays_within_the_buffer_and_capture_caps()
    {
        var mediaBytes = DeterministicBytes(32 * 1024 * 1024);
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PopulateMinimalLibraryAsync(library, mediaBytes);

        var budget = new PortableArchiveBufferBudget(PortableArchiveLimits.MaxExplicitBufferBytes);
        var destination = new SyncForbiddingSink(discard: true);
        var exported = await library.Portability().ExportAsync(destination, budget);

        exported.MediaFiles.Should().Be(2);
        destination.SyncCalls.Should().Be(0);
        budget.HighWaterBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes
            + PortableArchiveLimits.CopyBufferBytes);
        budget.HighWaterBytes.Should().BeGreaterThanOrEqualTo(
            PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes,
            "the capture lease must come from the shared export budget");
        budget.CurrentBytes.Should().Be(0);

        // Capture-sink pending high-water for the same 32 MiB stored media,
        // measured directly through the adapter.
        var captureBudget = new PortableArchiveBufferBudget(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        var captureDestination = new SyncForbiddingSink(discard: true);
        var capture = new BoundedSynchronousCaptureSink(captureDestination, captureBudget);
        try
        {
            await using (var archive = await ZipArchive.CreateAsync(
                capture,
                ZipArchiveMode.Create,
                true,
                null))
            {
                var entry = archive.CreateEntry(
                    $"media/books/{Guid.NewGuid():N}/book.epub",
                    CompressionLevel.NoCompression);
                await using var target = await entry.OpenAsync();
                await target.WriteAsync(mediaBytes);
            }

            await capture.CompleteAsync();
            capture.SynchronousHighWaterBytes.Should().BeGreaterThan(0);
            capture.SynchronousHighWaterBytes.Should().BeLessThan(
                PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes);
            capture.MaxSingleSynchronousWriteBytes.Should().BeLessThan(64 * 1024);
        }
        finally
        {
            await capture.DisposeAsync();
        }

        captureDestination.SyncCalls.Should().Be(0);
        captureBudget.CurrentBytes.Should().Be(0);
        output.WriteLine(
            $"32 MiB media export: budget high-water={budget.HighWaterBytes} bytes, "
            + $"capture pending high-water={capture.SynchronousHighWaterBytes} bytes, "
            + $"max single synchronous write={capture.MaxSingleSynchronousWriteBytes} bytes");
    }

    [Fact]
    public async Task Export_entry_framing_matches_a_native_seekable_writer()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        var destination = new SyncForbiddingSink();
        await library.Portability().ExportAsync(destination);
        destination.SyncCalls.Should().Be(0);

        var streamed = ReadEntries(destination.ToArray());

        // The previous implementation wrote the archive through a seekable
        // temp FileStream (no data descriptors). This reference reproduces
        // that writer mode. Entry payloads, CRCs, compressed sizes and
        // compression methods must match the directly streamed archive; only
        // the descriptor/local-header framing differs by design.
        using var reference = new MemoryStream();
        using (var archive = new ZipArchive(reference, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in streamed)
            {
                var entry = archive.CreateEntry(
                    item.Name,
                    item.Name.EndsWith(".json", StringComparison.Ordinal)
                        ? CompressionLevel.Optimal
                        : CompressionLevel.NoCompression);
                await using var target = await entry.OpenAsync();
                await target.WriteAsync(item.Bytes);
            }
        }

        using var observed = new ZipArchive(new MemoryStream(destination.ToArray()), ZipArchiveMode.Read);
        using var expected = new ZipArchive(new MemoryStream(reference.ToArray()), ZipArchiveMode.Read);

        observed.Entries.Select(entry => entry.FullName)
            .Should().Equal(expected.Entries.Select(entry => entry.FullName));
        observed.Entries.Should().HaveCount(streamed.Count);

        for (var index = 0; index < expected.Entries.Count; index++)
        {
            var expectedEntry = expected.Entries[index];
            var observedEntry = observed.Entries[index];
            observedEntry.FullName.Should().Be(expectedEntry.FullName);
            observedEntry.Length.Should().Be(expectedEntry.Length);
            observedEntry.CompressedLength.Should().Be(expectedEntry.CompressedLength);
            observedEntry.Crc32.Should().Be(expectedEntry.Crc32);
            CompressionMethod(observedEntry).Should().Be(CompressionMethod(expectedEntry));

            await using var contents = await observedEntry.OpenAsync();
            using var buffer = new MemoryStream();
            await contents.CopyToAsync(buffer);
            buffer.ToArray().Should().Equal(streamed[index].Bytes);
        }

        foreach (var entry in observed.Entries)
        {
            CompressionMethod(entry).Should().Be(
                entry.FullName.EndsWith(".json", StringComparison.Ordinal)
                    ? (ushort)8
                    : (ushort)0,
                "relational JSON is deflated and media is stored");
        }
    }

    [Fact]
    public async Task HashingWriteStream_forwards_async_writes_and_fails_past_its_cap()
    {
        using var destination = new MemoryStream();
        await using (var hashing = new HashingWriteStream(
            destination,
            4,
            "data_too_large",
            "Portable relational data exceeds its limit."))
        {
            await hashing.WriteAsync(new byte[] { 1, 2, 3 });

            var exception = await Assert.ThrowsAsync<PortableArchiveException>(
                () => hashing.WriteAsync(new byte[] { 4, 5 }).AsTask());
            exception.Code.Should().Be("data_too_large");

            var syncWrite = () => hashing.Write(new byte[] { 9 }, 0, 1);
            syncWrite.Should().Throw<NotSupportedException>(
                "the serializer path must never fall back to synchronous writes");

            var (length, sha256) = hashing.Complete();
            length.Should().Be(3);
            sha256.Should().Be(Sha256Hex(new byte[] { 1, 2, 3 }));
        }

        destination.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Export_oversized_relational_data_fails_before_the_first_byte()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        // Inject a small cap instead of serializing a 64 MiB payload.
        var service = new PortableArchiveService(
            library.Db,
            library.Storage,
            NullLogger<PortableArchiveService>.Instance)
        {
            MaxExportDataBytes = 1024,
        };
        var destination = new SyncForbiddingSink();

        var exception = await Assert.ThrowsAsync<PortableArchiveException>(
            () => service.ExportAsync(destination));

        exception.Code.Should().Be("data_too_large");
        destination.AsyncWriteCalls.Should().Be(0);
        destination.SyncCalls.Should().Be(0);
        destination.BytesWritten.Should().Be(0);
    }

    [Fact]
    public void Export_data_preflight_mismatch_is_rejected()
    {
        var hash = new string('a', 64);
        var otherHash = new string('b', 64);

        var lengthMismatch = () => PortableArchiveValidation.ValidateStreamedDataMatchesPreflight(
            10,
            hash,
            11,
            hash);
        lengthMismatch.Should().Throw<PortableArchiveException>()
            .Which.Code.Should().Be("data_serialization_mismatch");

        var hashMismatch = () => PortableArchiveValidation.ValidateStreamedDataMatchesPreflight(
            10,
            hash,
            10,
            otherHash);
        hashMismatch.Should().Throw<PortableArchiveException>()
            .Which.Code.Should().Be("data_serialization_mismatch");

        var match = () => PortableArchiveValidation.ValidateStreamedDataMatchesPreflight(
            10,
            hash,
            10,
            hash);
        match.Should().NotThrow();
    }

    [Fact]
    public async Task Export_destination_flush_failure_after_central_directory_still_throws()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        var destination = new SyncForbiddingSink { FailOnFlush = true };

        await Assert.ThrowsAsync<IOException>(
            () => library.Portability().ExportAsync(destination));

        destination.SyncCalls.Should().Be(0);
        destination.FlushCalls.Should().Be(1);
        destination.BytesWritten.Should().BeGreaterThan(
            0,
            "the central directory may already have been drained before the final flush failed");
    }

    [Fact]
    public async Task Export_endpoint_aborts_a_started_response_when_the_export_throws()
    {
        var context = new DefaultHttpContext();
        var lifetime = new RecordingLifetimeFeature();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Features.Set<IHttpResponseFeature>(new StartedHttpResponseFeature());
        var exporter = new DefaultPortableArchiveExporter(
            new StreamingThenFailingArchiveService());

        var exception = await Assert.ThrowsAsync<IOException>(
            () => exporter.ExportAsync(context));

        exception.Message.Should().Be("Injected export failure after the response started.");
        context.Response.HasStarted.Should().BeTrue();
        lifetime.Aborted.Should().BeTrue();
    }

    [Fact]
    public async Task Export_media_changed_after_pinning_throws_and_leaves_an_incomplete_archive()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var bookId = await PopulateMinimalLibraryAsync(
            library,
            Encoding.UTF8.GetBytes("MEDIA-CONTENT"));

        var mutated = Encoding.UTF8.GetBytes("MEDIA-CONTENT");
        mutated[0] ^= 0xFF;
        var storage = new PortableExportSnapshotTests.ExportProbeStorage(library.Storage)
        {
            TargetBookId = bookId,
            CopyPassContent = mutated,
        };
        var service = new PortableArchiveService(
            library.Db,
            storage,
            NullLogger<PortableArchiveService>.Instance);

        var destination = new SyncForbiddingSink();
        var exception = await Assert.ThrowsAsync<PortableArchiveException>(
            () => service.ExportAsync(destination));

        exception.Code.Should().Be("source_media_changed");
        destination.SyncCalls.Should().Be(0);
        destination.BytesWritten.Should().BeGreaterThan(
            0,
            "the data entry is streamed before the media copy pass fails");
        AssertNotCompleteArchive(destination.ToArray());
    }

    [Fact]
    public async Task Export_cancellation_after_output_started_throws_and_leaves_an_incomplete_archive()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        using var cancellation = new CancellationTokenSource();
        var destination = new SyncForbiddingSink
        {
            CancelAfterWriteNumber = 1,
            CancellationSource = cancellation,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => library.Portability().ExportAsync(destination, cancellation.Token));

        destination.SyncCalls.Should().Be(0);
        destination.BytesWritten.Should().BeGreaterThan(
            0,
            "cancellation happens after the response body started");
        AssertNotCompleteArchive(destination.ToArray());
    }

    [Fact]
    public async Task Export_destination_write_failure_throws_and_leaves_an_incomplete_archive()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        var destination = new SyncForbiddingSink { FailOnWriteNumber = 2 };
        await Assert.ThrowsAsync<IOException>(
            () => library.Portability().ExportAsync(destination));

        destination.SyncCalls.Should().Be(0);
        destination.BytesWritten.Should().BeGreaterThan(
            0,
            "the first destination write succeeded before the failure");
        AssertNotCompleteArchive(destination.ToArray());
    }

    [Fact]
    public async Task Export_failure_before_the_first_byte_writes_nothing_to_the_destination()
    {
        // Missing media is detected by the pin pass, before the archive opens.
        await using (var library = await LocalPortableTestLibrary.CreateAsync())
        {
            var bookId = await PopulateMinimalLibraryAsync(
                library,
                Encoding.UTF8.GetBytes("MEDIA"));
            await library.Storage.DeleteBookFileAsync(bookId);

            var destination = new SyncForbiddingSink();
            var exception = await Assert.ThrowsAsync<PortableArchiveException>(
                () => library.Portability().ExportAsync(destination));

            exception.Code.Should().Be("source_media_missing");
            destination.AsyncWriteCalls.Should().Be(0);
            destination.SyncCalls.Should().Be(0);
            destination.BytesWritten.Should().Be(0);
        }

        // A relational snapshot failure also precedes any archive output.
        var interceptor = new FailingSnapshotInterceptor { Enabled = false };
        await using (var library = await LocalPortableTestLibrary.CreateAsync(
            options => options.AddInterceptors(interceptor)))
        {
            await PortableArchiveTestSupport.PopulateRepresentativeAsync(
                library.Db,
                library.Storage);
            interceptor.Enabled = true;

            var destination = new SyncForbiddingSink();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => library.Portability().ExportAsync(destination));

            destination.AsyncWriteCalls.Should().Be(0);
            destination.SyncCalls.Should().Be(0);
            destination.BytesWritten.Should().Be(0);
        }
    }

    [Fact]
    public async Task Export_through_the_provider_neutral_sink_streams_and_reports_progress()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        var underlying = new SyncForbiddingSink();
        var progress = new RecordingProgress();
        await using (var sink = new StreamPortableArchiveSink(underlying))
        {
            var exported = await library.Portability().ExportAsync(sink, progress);
            exported.MediaFiles.Should().Be(5);
        }

        underlying.SyncCalls.Should().Be(0);
        AssertCompleteArchive(underlying.ToArray());
        progress.Reports.Select(report => report.Phase).Should().ContainInOrder(
            PortableArchiveProgressPhase.Snapshotting,
            PortableArchiveProgressPhase.IndexingMedia,
            PortableArchiveProgressPhase.WritingArchive);
    }

    [Fact]
    public async Task Export_to_a_seekable_destination_still_produces_an_importable_archive()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        using var archive = new MemoryStream();
        await library.Portability().ExportAsync(archive);

        await using var target = await LocalPortableTestLibrary.CreateAsync();
        archive.Position = 0;
        var imported = await target.Portability().ImportAsync(archive);

        imported.IntegrityVerified.Should().BeTrue();
        imported.Counts.Books.Should().Be(4);
    }

    private static async Task<Guid> PopulateMinimalLibraryAsync(
        LocalPortableTestLibrary library,
        byte[] mediaBytes)
    {
        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Export Writer Work",
            NormalizedTitle = "EXPORT WRITER WORK",
            NormalizedAuthor = string.Empty,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
        };
        var book = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = "Export Writer Book",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = "book.epub",
                CoverFileName = "cover.jpg",
            },
        };

        library.Db.Works.Add(work);
        library.Db.Books.Add(book);
        await library.Db.SaveChangesAsync();

        await using (var content = new MemoryStream(mediaBytes, writable: false))
            await library.Storage.SaveBookFileAsync(book.Id, content, "book.epub");
        await using (var cover = new MemoryStream(Encoding.UTF8.GetBytes("COVER"), writable: false))
            await library.Storage.SaveBookCoverAsync(book.Id, cover, "cover.jpg");

        return book.Id;
    }

    private static List<TestEntry> ReadEntries(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new List<TestEntry>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            entries.Add(new TestEntry(entry.FullName, output.ToArray()));
        }

        return entries;
    }

    private static void AssertCompleteArchive(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        archive.Entries.Should().Contain(entry => entry.FullName == PortableArchiveFormat.DataPath);
        archive.Entries.Should().Contain(entry => entry.FullName == PortableArchiveFormat.ManifestPath);
    }

    private static void AssertNotCompleteArchive(byte[] bytes)
    {
        bytes.Should().NotBeEmpty();
        var action = () =>
        {
            using var archive = new ZipArchive(
                new MemoryStream(bytes, writable: false),
                ZipArchiveMode.Read);
            _ = archive.Entries.Count;
        };

        action.Should().Throw<InvalidDataException>(
            "a partial export must not be readable as a complete archive");
    }

    private static ushort CompressionMethod(ZipArchiveEntry entry) =>
        Convert.ToUInt16(NativeField<object>(entry, "_storedCompressionMethod"));

    private static T NativeField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name,
            BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull($"the native ZIP field '{name}' must exist");
        return (T)field!.GetValue(instance)!;
    }

    private static byte[] DeterministicBytes(int size)
    {
        var bytes = new byte[size];
        new Random(20261004).NextBytes(bytes);
        return bytes;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record TestEntry(string Name, byte[] Bytes);

    private sealed class RecordingProgress : IProgress<PortableArchiveProgress>
    {
        public List<PortableArchiveProgress> Reports { get; } = [];

        public void Report(PortableArchiveProgress value) => Reports.Add(value);
    }

    private sealed class FailingSnapshotInterceptor : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result) =>
            Enabled
                ? throw new InvalidOperationException("Injected snapshot failure.")
                : base.ReaderExecuting(command, eventData, result);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            Enabled
                ? ValueTask.FromException<InterceptionResult<DbDataReader>>(
                    new InvalidOperationException("Injected snapshot failure."))
                : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private sealed class SyncForbiddingSink(bool discard = false) : Stream
    {
        private readonly MemoryStream _buffer = new();

        public int SyncCalls { get; private set; }
        public int AsyncWriteCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public long BytesWritten => _buffer.Length;
        public bool FailOnFlush { get; init; }
        public int? FailOnWriteNumber { get; init; }
        public int? CancelAfterWriteNumber { get; init; }
        public CancellationTokenSource? CancellationSource { get; init; }

        public byte[] ToArray() => _buffer.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            SyncCalls++;
            throw SyncIoDisallowed();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            if (FailOnFlush)
                throw new IOException("Injected destination flush failure.");
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncWriteCalls++;

            if (FailOnWriteNumber == AsyncWriteCalls)
                throw new IOException("Injected destination write failure.");

            if (!discard)
                _buffer.Write(buffer.Span);

            if (CancelAfterWriteNumber == AsyncWriteCalls)
                CancellationSource?.Cancel();

            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            SyncCalls++;
            throw SyncIoDisallowed();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            SyncCalls++;
            throw SyncIoDisallowed();
        }

        public override void WriteByte(byte value)
        {
            SyncCalls++;
            throw SyncIoDisallowed();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private static InvalidOperationException SyncIoDisallowed() =>
            new(
                "Synchronous operations are disallowed. "
                + "Call WriteAsync or set AllowSynchronousIO to true instead.");
    }

    private sealed class StreamingThenFailingArchiveService : IPortableArchiveService
    {
        public async Task<PortableExportResult> ExportAsync(
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(new byte[16], cancellationToken);
            throw new IOException("Injected export failure after the response started.");
        }

        public Task<PortableExportResult> ExportAsync(
            IPortableArchiveSink destination,
            IProgress<PortableArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PortableImportResult> ImportAsync(
            Stream source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class StartedHttpResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}

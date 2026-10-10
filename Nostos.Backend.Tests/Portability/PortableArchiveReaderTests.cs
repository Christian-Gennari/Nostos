using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Services.Portability;
using Xunit;
using Xunit.Abstractions;
using static Nostos.Backend.Tests.Portability.PortableArchiveTestSupport;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableArchiveReaderTests(ITestOutputHelper output)
{
    private const string ManifestPath = "manifest.json";
    private const string DataPath = "data/library.json";
    private const int SupportedFormatVersion = 1;
    private const int LegacyDataVersion = 1;
    private const int IntermediateDataVersion = 2;
    private const int CurrentDataVersion = 3;
    // What this build writes. Version 4 added multi-track audiobooks (#835);
    // the downgrade fixtures above still describe the shape changes up to 3.
    private const int ExportDataVersion = 4;

    private static readonly DateTime FixedUtc =
        new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Prepare_import_commits_current_archive_with_byte_identical_staged_content()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        var bytes = await ExportAsync(source);

        var filesBefore = SnapshotStorage(source);
        var rowsBefore = await SnapshotRowsAsync(source);

        var outcome = await PrepareRawAsync(bytes);

        outcome.Error.Should().BeNull();
        var prepared = outcome.Prepared!;
        prepared.IntegrityVerified.Should().BeTrue();
        prepared.FormatVersion.Should().Be(SupportedFormatVersion);
        prepared.DataVersion.Should().Be(ExportDataVersion);
        prepared.ArchiveBytes.Should().Be(bytes.LongLength);
        prepared.PreparedAtUtc.Should().Be(FixedUtc);
        prepared.MediaFiles.Should().Be(5);
        prepared.StagingId.Should().Be(outcome.Staging.Created.Single());
        outcome.Store.Areas.Should().ContainKey(prepared.StagingId.Value);

        var counts = prepared.Metadata.Counts;
        counts.Works.Should().Be(3);
        counts.Books.Should().Be(4);
        counts.Collections.Should().Be(2);
        counts.CollectionMemberships.Should().Be(3);
        counts.Notes.Should().Be(1);
        counts.Topics.Should().Be(1);
        counts.NoteTopics.Should().Be(1);
        counts.Writings.Should().Be(2);
        counts.WritingNotes.Should().Be(1);
        counts.Acquisitions.Should().Be(1);
        counts.AssistantSettings.Should().Be(1);
        counts.NoteImportBookLinks.Should().Be(0);
        counts.MediaEntries.Should().Be(5);

        await AssertStagedContentMatchesArchiveAsync(bytes, prepared, outcome.Staging);

        outcome.Physical.SyncCalls.Should().Be(0);
        outcome.Budget.CurrentBytes.Should().Be(0);

        // Ids are only read to keep the representative fixture observable; preparation
        // must not touch the active library that produced the archive.
        ids.EpubBookId.Should().NotBe(Guid.Empty);
        SnapshotStorage(source).Should().Equal(filesBefore);
        (await SnapshotRowsAsync(source)).Should().Be(rowsBefore);
    }

    [Theory]
    [InlineData(LegacyDataVersion)]
    [InlineData(IntermediateDataVersion)]
    [InlineData(CurrentDataVersion)]
    public async Task Prepare_import_commits_legacy_fixtures_with_matching_versions(
        int dataVersion)
    {
        var bytes = await CreateLegacyFixtureAsync(dataVersion);

        var outcome = await PrepareRawAsync(bytes);

        outcome.Error.Should().BeNull();
        var prepared = outcome.Prepared!;
        prepared.FormatVersion.Should().Be(SupportedFormatVersion);
        prepared.DataVersion.Should().Be(dataVersion);
        await AssertStagedContentMatchesArchiveAsync(bytes, prepared, outcome.Staging);
        outcome.Physical.SyncCalls.Should().Be(0);
        outcome.Budget.CurrentBytes.Should().Be(0);

        var dataExpected = ReadEntries(bytes)
            .Single(x => x.Name == DataPath)
            .Bytes;
        using var dataRead = await outcome.Staging.OpenDataReadAsync(prepared.StagingId);
        (await ReadAllAsync(dataRead)).Should().Equal(dataExpected);
    }

    [Fact]
    public async Task Prepare_import_reports_phase_boundaries_and_per_entry_progress()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        var bytes = await ExportAsync(source);

        var reports = new List<PortableArchiveProgress>();
        var outcome = await PrepareRawAsync(
            bytes,
            progress: new InlineProgress<PortableArchiveProgress>(reports.Add));

        outcome.Error.Should().BeNull();
        reports.Select(report => report.Phase).Distinct().Should().Contain(
        [
            PortableArchiveProgressPhase.InspectingArchive,
            PortableArchiveProgressPhase.ValidatingData,
            PortableArchiveProgressPhase.StagingMedia,
            PortableArchiveProgressPhase.Prepared,
        ]);

        var finalReport = reports[^1];
        finalReport.Phase.Should().Be(PortableArchiveProgressPhase.Prepared);
        finalReport.ItemsProcessed.Should().Be(outcome.Prepared!.MediaFiles);
        finalReport.TotalItems.Should().Be(outcome.Prepared.MediaFiles);
        reports.Should().Contain(report =>
            report.Phase == PortableArchiveProgressPhase.StagingMedia
            && report.ItemsProcessed == outcome.Prepared.MediaFiles);
    }

    [Fact]
    public async Task Prepare_import_inspects_metadata_without_reading_media_bodies_and_uses_only_ranged_reads()
    {
        const int mediaBytes = 8 * 1024 * 1024;
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        await source.Storage.SaveBookFileAsync(
            ids.EpubBookId,
            new MemoryStream(GenerateBytes(mediaBytes, seed: 42)),
            "large.epub");
        var bytes = await ExportAsync(source);

        // Metadata inspection alone must not consume the media payload.
        using var physical = new CountingPhysicalStream(bytes);
        var recorder = new SourceRecorder();
        await using var metadataSource = CreateSource(physical, recorder);
        var metadataBudget = new PortableArchiveBufferBudget(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        await using (var zipReader = await PortableArchiveZipReader.OpenAsync(
            metadataSource,
            metadataBudget))
        {
            zipReader.Archive.Entries.Count.Should().BeGreaterThan(2);
        }

        metadataBudget.CurrentBytes.Should().Be(0);
        physical.SyncCalls.Should().Be(0);
        recorder.TotalBytesRead.Should().BeLessThan(mediaBytes);
        recorder.MaxRequestedBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.CopyBufferBytes);
        var outcome = await PrepareRawAsync(bytes);

        outcome.Error.Should().BeNull();
        outcome.Physical.SyncCalls.Should().Be(0);
        outcome.Recorder.MaxRequestedBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.CopyBufferBytes);
        outcome.Recorder.MaxRequestedBytes.Should().BeGreaterThan(0);
        output.WriteLine(
            $"metadata-only: source reads={recorder.AsyncReadCalls}, bytes={recorder.TotalBytesRead}, max request={recorder.MaxRequestedBytes}, media={mediaBytes}; prepare: reads={outcome.Recorder.AsyncReadCalls}, max request={outcome.Recorder.MaxRequestedBytes}");
    }

    [Fact]
    public async Task Prepare_import_of_large_media_keeps_budget_high_water_below_media_size_and_cap()
    {
        const int mediaBytes = 32 * 1024 * 1024;
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        var large = GenerateBytes(mediaBytes, seed: 7);
        await source.Storage.SaveBookFileAsync(
            ids.EpubBookId,
            new MemoryStream(large),
            "large.epub");
        var bytes = await ExportAsync(source);

        var budget = new PortableArchiveBufferBudget(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        var outcome = await PrepareRawAsync(bytes, budget);

        outcome.Error.Should().BeNull();
        budget.CurrentBytes.Should().Be(0);
        budget.HighWaterBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.MaxExplicitBufferBytes);
        budget.HighWaterBytes.Should().BeLessThan(
            mediaBytes,
            "a whole-entry media buffer would push the high-water mark to at least the media length");
        outcome.Physical.SyncCalls.Should().Be(0);
        outcome.Recorder.MaxRequestedBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.CopyBufferBytes,
            "media is copied through the shared copy buffer, never a media-sized range read");
        output.WriteLine(
            $"large media: media={mediaBytes}, archive={bytes.LongLength}, budget high-water={budget.HighWaterBytes}, reads={outcome.Recorder.AsyncReadCalls}, max request={outcome.Recorder.MaxRequestedBytes}");

        var prepared = outcome.Prepared!;
        var staged = prepared.Media.Single(item => item.Descriptor.Path.Contains(ids.EpubBookId.ToString("N")) && item.Descriptor.Kind == "book");
        staged.Descriptor.Length.Should().Be(large.LongLength);
        using var stagedRead = await outcome.Staging.OpenMediaReadAsync(
            prepared.StagingId,
            staged.Reference);
        using var stagedOutput = new MemoryStream();
        await stagedRead.CopyToAsync(stagedOutput);
        stagedOutput.Length.Should().Be(large.LongLength);
        Convert.ToHexString(SHA256.HashData(stagedOutput.ToArray()))
            .ToLowerInvariant()
            .Should().Be(staged.Descriptor.Sha256);
    }

    [Fact]
    public async Task Prepare_import_rejects_over_limit_source_before_reading_or_staging()
    {
        var reads = 0;
        await using var source = new RangePortableArchiveSource(
            PortableArchiveLimits.MaxArchiveBytes + 1,
            (offset, buffer, ct) =>
            {
                reads++;
                return ValueTask.FromResult(buffer.Length);
            });

        var store = new InMemoryPortableImportStagingStore();
        await using var staging = new RecordingPortableImportStaging(
            new InMemoryPortableImportStaging(store));
        var reader = new PortableArchiveReader(timeProvider: new ManualTimeProvider(FixedUtc));

        var act = async () => await reader.PrepareImportAsync(source, staging);
        var exception = await act.Should().ThrowAsync<PortableArchiveException>();
        exception.Which.Code.Should().Be("archive_too_large");
        reads.Should().Be(0);
        staging.Created.Should().BeEmpty();
        store.Areas.Should().BeEmpty();
    }

    [Fact]
    public async Task Prepare_import_rejects_truncated_source_with_archive_source_truncated()
    {
        var bytes = await ExportFixtureAsync();

        // The declared source length exceeds the bytes the source can actually serve.
        await using var source = new RangePortableArchiveSource(
            bytes.Length + 4096,
            (offset, buffer, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (offset >= bytes.Length)
                    return ValueTask.FromResult(0);
                var count = (int)Math.Min(buffer.Length, bytes.Length - offset);
                bytes.AsMemory((int)offset, count).CopyTo(buffer);
                return ValueTask.FromResult(count);
            });

        var store = new InMemoryPortableImportStagingStore();
        var inner = new InMemoryPortableImportStaging(store);
        await using var staging = new RecordingPortableImportStaging(inner);
        var reader = new PortableArchiveReader(timeProvider: new ManualTimeProvider(FixedUtc));

        var act = async () => await reader.PrepareImportAsync(source, staging);
        var exception = await act.Should().ThrowAsync<PortableArchiveException>();
        exception.Which.Code.Should().Be("archive_source_truncated");
        await AssertStagingDeletedAsync(store, staging);
    }

    [Fact]
    public async Task Prepare_import_cancellation_mid_media_deletes_staging_and_escapes_as_cancellation()
    {
        const int mediaBytes = 20 * 1024 * 1024;
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        await source.Storage.SaveBookFileAsync(
            ids.EpubBookId,
            new MemoryStream(GenerateBytes(mediaBytes, seed: 5)),
            "large.epub");
        var bytes = await ExportAsync(source);

        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress<PortableArchiveProgress>(report =>
        {
            if (report.Phase == PortableArchiveProgressPhase.StagingMedia
                && report.BytesProcessed > 0)
            {
                cts.Cancel();
            }
        });

        var outcome = await PrepareRawAsync(
            bytes,
            progress: progress,
            cancellation: cts);

        outcome.Error.Should().BeOfType<OperationCanceledException>(
            "cancellation must never be translated into an archive-corruption failure");
        await AssertStagingDeletedAsync(outcome.Store, outcome.Staging);
        outcome.Budget.CurrentBytes.Should().Be(0);
    }

    // Characterization table captured from the pre-slice legacy ImportAsync on
    // main: the immediate import and the streaming reader must keep producing
    // these exact typed codes for the same hostile archives. Immediate import now
    // runs the reader internally, so the table (not a live comparison against a
    // second implementation) pins the old observable behaviour.
    [Theory]
    [InlineData("unsupported_version", "unsupported_version")]
    [InlineData("unsupported_data_version", "unsupported_data_version")]
    [InlineData("data_version_mismatch", "data_version_mismatch")]
    [InlineData("malformed_manifest", "malformed_manifest")]
    [InlineData("missing_manifest", "missing_manifest")]
    [InlineData("missing_referenced_media", "missing_referenced_media")]
    [InlineData("unexpected_entry", "unexpected_entry")]
    [InlineData("duplicate_path", "duplicate_path")]
    [InlineData("unsafe_archive_path", "unsafe_archive_path")]
    // Main's legacy ImportAsync rejects an explicit directory entry as
    // directory_entry_not_allowed (native ZipArchiveEntry.Name is empty); main's
    // newer reader reports unsafe_archive_path. The legacy client-observable code
    // wins, so both paths here report directory_entry_not_allowed.
    [InlineData("directory_entry", "directory_entry_not_allowed")]
    // Multi-fault precedence captured from main: the manifest-count guard and the
    // data-hash guard each win over the later media faults.
    [InlineData("count_mismatch_and_corrupt_media", "count_mismatch")]
    [InlineData("data_hash_mismatch_and_missing_media", "data_checksum_mismatch")]
    [InlineData("data_checksum_mismatch", "data_checksum_mismatch")]
    [InlineData("data_length_mismatch", "data_length_mismatch")]
    [InlineData("data_length_mismatch_and_hash_mismatch", "data_length_mismatch")]
    [InlineData("media_checksum_mismatch", "media_checksum_mismatch")]
    [InlineData("media_length_mismatch", "media_length_mismatch")]
    [InlineData("media_length_mismatch_negative", "media_length_mismatch")]
    [InlineData("entry_too_large", "entry_too_large")]
    [InlineData("malformed_relationship", "malformed_relationship")]
    [InlineData("duplicate_id", "duplicate_id")]
    [InlineData("count_mismatch", "count_mismatch")]
    [InlineData("suspicious_compression", "suspicious_compression")]
    [InlineData("not_a_portable_archive", "invalid_zip")]
    [InlineData("empty_archive", "empty_archive")]
    public async Task Invalid_archives_fail_with_the_characterized_typed_code_in_prepare_and_immediate_import(
        string scenario,
        string expectedCode)
    {
        var bytes = await BuildInvalidArchiveAsync(scenario);

        var immediateCode = await CaptureImmediateImportCodeAsync(bytes);
        var outcome = await PrepareRawAsync(bytes);

        outcome.Error.Should().BeOfType<PortableArchiveException>();
        var prepareCode = ((PortableArchiveException)outcome.Error!).Code;
        prepareCode.Should().Be(expectedCode);
        immediateCode.Should().Be(expectedCode);
        outcome.Store.Areas.Should().BeEmpty();
        if (scenario == "empty_archive")
        {
            // An empty source is rejected before any staging area is created.
            outcome.Staging.Created.Should().BeEmpty();
        }
        else
        {
            outcome.Staging.Created.Should().NotBeEmpty();
            outcome.Staging.Deleted.Should().Contain(outcome.Staging.Created[0]);
        }
    }

    private static async Task AssertStagedContentMatchesArchiveAsync(
        byte[] archiveBytes,
        PortablePreparedImport prepared,
        RecordingPortableImportStaging staging)
    {
        var expected = ReadEntries(archiveBytes);
        var expectedData = expected.Single(entry => entry.Name == DataPath).Bytes;
        var expectedManifest = expected.Single(entry => entry.Name == ManifestPath).Bytes;

        using (var dataRead = await staging.OpenDataReadAsync(prepared.StagingId))
            (await ReadAllAsync(dataRead)).Should().Equal(expectedData);

        using (var manifestRead = await staging.OpenManifestReadAsync(prepared.StagingId))
            (await ReadAllAsync(manifestRead)).Should().Equal(expectedManifest);

        var mediaEntries = expected
            .Where(entry => entry.Name.StartsWith("media/books/", StringComparison.Ordinal))
            .ToList();
        mediaEntries.Should().HaveCount(prepared.MediaFiles);

        foreach (var mediaEntry in mediaEntries)
        {
            var staged = prepared.Media.Single(item => item.Descriptor.Path == mediaEntry.Name);
            using var mediaRead = await staging.OpenMediaReadAsync(
                prepared.StagingId,
                staged.Reference);
            (await ReadAllAsync(mediaRead)).Should().Equal(mediaEntry.Bytes);
        }

        var inventory = await staging.ListMediaAsync(prepared.StagingId);
        inventory.Select(item => item.Descriptor.Path)
            .Should().BeEquivalentTo(mediaEntries.Select(entry => entry.Name));
    }

    private static async Task AssertPrepareFailureAsync(PrepareOutcome outcome, string expectedCode)
    {
        outcome.Error.Should().BeOfType<PortableArchiveException>();
        ((PortableArchiveException)outcome.Error!).Code.Should().Be(expectedCode);
        await AssertStagingDeletedAsync(outcome.Store, outcome.Staging);
    }

    private static async Task AssertStagingDeletedAsync(
        InMemoryPortableImportStagingStore store,
        RecordingPortableImportStaging staging)
    {
        staging.Created.Should().ContainSingle();
        staging.Deleted.Should().Contain(staging.Created[0]);
        store.Areas.Should().NotContainKey(staging.Created[0].Value);

        var rebuild = async () => await staging.RebuildPreparedImportAsync(staging.Created[0]);
        var rebuildException = await rebuild.Should().ThrowAsync<PortableStagingException>();
        rebuildException.Which.Code.Should().Be(PortableStagingException.NotFoundCode);

        var read = async () => await staging.OpenDataReadAsync(staging.Created[0]);
        var readException = await read.Should().ThrowAsync<PortableStagingException>();
        readException.Which.Code.Should().Be(PortableStagingException.NotFoundCode);
    }

    private static async Task<PrepareOutcome> PrepareRawAsync(
        byte[] bytes,
        PortableArchiveBufferBudget? budget = null,
        IProgress<PortableArchiveProgress>? progress = null,
        CancellationTokenSource? cancellation = null)
    {
        var physical = new CountingPhysicalStream(bytes);
        var recorder = new SourceRecorder();
        await using var source = CreateSource(physical, recorder);
        var store = new InMemoryPortableImportStagingStore();
        var inner = new InMemoryPortableImportStaging(store);
        await using var staging = new RecordingPortableImportStaging(inner);
        var reader = new PortableArchiveReader(
            timeProvider: new ManualTimeProvider(FixedUtc));

        var effectiveBudget = budget ?? new PortableArchiveBufferBudget(
            PortableArchiveLimits.MaxExplicitBufferBytes);

        PortablePreparedImport? prepared = null;
        Exception? error = null;

        try
        {
            prepared = await reader.PrepareImportAsync(
                source,
                staging,
                progress,
                cancellation?.Token ?? CancellationToken.None,
                effectiveBudget);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        return new PrepareOutcome(
            error,
            prepared,
            store,
            staging,
            physical,
            recorder,
            effectiveBudget);
    }

    private static async Task<string> CaptureImmediateImportCodeAsync(byte[] bytes)
    {
        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        using var archive = new MemoryStream(bytes, writable: false);
        string code;
        try
        {
            await destination.Portability().ImportAsync(archive);
            code = "none";
        }
        catch (PortableArchiveException exception)
        {
            code = exception.Code;
        }

        // Every row is a rejection against a fresh library: the immediate import
        // must never have mutated the destination (the individual per-code
        // service tests folded this claim in here).
        (await destination.Db.Books.CountAsync()).Should().Be(0);
        (await destination.Db.Works.CountAsync()).Should().Be(0);
        (await destination.Db.Notes.CountAsync()).Should().Be(0);
        return code;
    }

    private static async Task<byte[]> BuildInvalidArchiveAsync(string scenario)
    {
        if (scenario == "empty_archive")
            return [];

        if (scenario == "not_a_portable_archive")
        {
            var backup = new byte[256];
            Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(backup, 0);
            new Random(2).NextBytes(backup.AsSpan(16));
            return backup;
        }

        if (scenario == "suspicious_compression")
        {
            using var bomb = new MemoryStream();
            using (var archive = new ZipArchive(bomb, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("payload.bin", CompressionLevel.Optimal);
                await using var output = entry.Open();
                await output.WriteAsync(new byte[2 * 1024 * 1024]);
            }

            return bomb.ToArray();
        }

        var bytes = await ExportFixtureAsync();
        var entries = ReadEntries(bytes);
        var media = entries.First(entry =>
            entry.Name.StartsWith("media/books/", StringComparison.Ordinal));

        switch (scenario)
        {
            case "unsupported_version":
                MutateJsonEntry(entries, ManifestPath, root => root["formatVersion"] = 999);
                break;
            case "unsupported_data_version":
                MutateJsonEntry(entries, ManifestPath, root => root["dataVersion"] = 999);
                break;
            case "data_version_mismatch":
                MutateJsonEntry(entries, ManifestPath, root =>
                    root["dataVersion"] = IntermediateDataVersion);
                break;
            case "malformed_manifest":
                ReplaceEntry(
                    entries,
                    ManifestPath,
                    Encoding.UTF8.GetBytes("{ definitely-not-valid-json"));
                break;
            case "missing_manifest":
                entries.RemoveAll(entry => entry.Name == ManifestPath);
                break;
            case "missing_referenced_media":
                entries.RemoveAll(entry => entry.Name == media.Name);
                break;
            case "unexpected_entry":
                entries.Add(new TestArchiveEntry("extra.bin", [1, 2, 3]));
                break;
            case "duplicate_path":
                entries.Add(new TestArchiveEntry(media.Name, media.Bytes.ToArray()));
                break;
            case "unsafe_archive_path":
                entries.Add(new TestArchiveEntry("../escape.txt", [1, 2, 3]));
                break;
            case "directory_entry":
                entries.Add(new TestArchiveEntry("dir/", []));
                break;
            case "count_mismatch_and_corrupt_media":
                MutateJsonEntry(entries, ManifestPath, root =>
                    root["counts"]!["works"] =
                        root["counts"]!["works"]!.GetValue<int>() + 1);
                ReplaceEntry(
                    entries,
                    media.Name,
                    GenerateBytes(media.Bytes.Length, seed: 55));
                break;
            case "data_hash_mismatch_and_missing_media":
                MutateJsonEntry(entries, DataPath, root =>
                    root["works"]!.AsArray()[0]!["title"] = "Tampered after hashing");
                MutateJsonEntry(
                    entries,
                    ManifestPath,
                    root => root["data"]!["length"] =
                        entries.Single(entry => entry.Name == DataPath).Bytes.LongLength);
                entries.RemoveAll(entry => entry.Name == media.Name);
                break;
            case "data_checksum_mismatch":
                MutateJsonEntry(entries, DataPath, root =>
                    root["works"]!.AsArray()[0]!["title"] = "Tampered after hashing");
                MutateJsonEntry(
                    entries,
                    ManifestPath,
                    root => root["data"]!["length"] =
                        entries.Single(entry => entry.Name == DataPath).Bytes.LongLength);
                break;
            case "data_length_mismatch":
                MutateJsonEntry(
                    entries,
                    ManifestPath,
                    root => root["data"]!["length"] =
                        entries.Single(entry => entry.Name == DataPath).Bytes.LongLength + 1);
                break;
            case "data_length_mismatch_and_hash_mismatch":
                MutateJsonEntry(entries, DataPath, root =>
                    root["works"]!.AsArray()[0]!["title"] = "Tampered before the length check");
                MutateJsonEntry(
                    entries,
                    ManifestPath,
                    root => root["data"]!["length"] =
                        entries.Single(entry => entry.Name == DataPath).Bytes.LongLength + 1);
                break;
            case "media_checksum_mismatch":
                ReplaceEntry(
                    entries,
                    media.Name,
                    GenerateBytes(media.Bytes.Length, seed: 77));
                break;
            case "media_length_mismatch":
                MutateMediaDescriptor(
                    entries,
                    media.Name,
                    descriptor => descriptor["length"] =
                        descriptor["length"]!.GetValue<long>() + 1);
                break;
            case "media_length_mismatch_negative":
                MutateMediaDescriptor(
                    entries,
                    media.Name,
                    descriptor => descriptor["length"] =
                        descriptor["length"]!.GetValue<long>() - 1);
                break;
            case "entry_too_large":
                MutateMediaDescriptor(
                    entries,
                    media.Name,
                    descriptor => descriptor["length"] =
                        PortableArchiveLimits.MaxSingleEntryBytes + 1);
                break;
            case "malformed_relationship":
                MutateJsonEntry(entries, DataPath, root =>
                    root["books"]!.AsArray()[0]!["workId"] = Guid.NewGuid());
                RehashDataDescriptor(entries);
                break;
            case "duplicate_id":
                MutateJsonEntry(entries, DataPath, root =>
                {
                    var books = root["books"]!.AsArray();
                    books[1]!["id"] = books[0]!["id"]!.GetValue<string>();
                });
                RehashDataDescriptor(entries);
                break;
            case "count_mismatch":
                MutateJsonEntry(entries, ManifestPath, root =>
                    root["counts"]!["works"] =
                        root["counts"]!["works"]!.GetValue<int>() + 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        return BuildArchive(entries);
    }

    private static async Task<byte[]> CreateLegacyFixtureAsync(int dataVersion)
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            library.Db,
            library.Storage);
        var bytes = await ExportAsync(library);
        var entries = ReadEntries(bytes);

        MutateJsonEntry(entries, ManifestPath, root =>
        {
            root["formatVersion"] = SupportedFormatVersion;
            root["dataVersion"] = dataVersion;
        });

        MutateJsonEntry(entries, DataPath, root =>
        {
            root["version"] = dataVersion;
            if (dataVersion < IntermediateDataVersion)
            {
                root.Remove("writingNotes");
                root.Remove("noteImportBookLinks");
            }
            else if (dataVersion < CurrentDataVersion)
            {
                root.Remove("noteImportBookLinks");
            }
        });

        RehashDataDescriptor(entries);
        return BuildArchive(entries);
    }

    private static async Task<byte[]> ExportFixtureAsync()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        return await ExportAsync(source);
    }

    private static async Task<byte[]> ExportAsync(LocalPortableTestLibrary source)
    {
        using var archive = new MemoryStream();
        await source.Portability().ExportAsync(archive);
        return archive.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task<(int Works, int Books, int Notes, int Writings)> SnapshotRowsAsync(
        LocalPortableTestLibrary library) =>
        (
            await library.Db.Works.CountAsync(),
            await library.Db.Books.CountAsync(),
            await library.Db.Notes.CountAsync(),
            await library.Db.Writings.CountAsync());

    private static IReadOnlyList<string> SnapshotStorage(LocalPortableTestLibrary library) =>
        Directory
            .EnumerateFiles(library.Storage.StorageRoot, "*", SearchOption.AllDirectories)
            .Select(path =>
                $"{Path.GetRelativePath(library.Root, path)}:{new FileInfo(path).Length}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static byte[] GenerateBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static RangePortableArchiveSource CreateSource(
        CountingPhysicalStream physical,
        SourceRecorder recorder) =>
        new(physical.Length, (offset, buffer, ct) =>
        {
            var count = (int)Math.Min(buffer.Length, Math.Max(0, physical.Length - offset));
            recorder.Record(offset, count);
            ct.ThrowIfCancellationRequested();
            physical.Position = offset;
            return physical.ReadAsync(buffer[..count], ct);
        });

    private static void MutateMediaDescriptor(
        List<TestArchiveEntry> entries,
        string mediaPath,
        Action<JsonObject> mutate)
    {
        MutateJsonEntry(entries, ManifestPath, root =>
        {
            var media = root["media"]!.AsArray()
                .First(item => string.Equals(
                    item!["path"]!.GetValue<string>(),
                    mediaPath,
                    StringComparison.Ordinal))!
                .AsObject();
            mutate(media);
        });
    }

    private static void ReplaceEntry(
        List<TestArchiveEntry> entries,
        string name,
        byte[] bytes)
    {
        var index = entries.FindIndex(entry =>
            string.Equals(entry.Name, name, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidDataException($"Archive entry '{name}' is missing.");

        entries[index] = new TestArchiveEntry(name, bytes);
    }

    private sealed record PrepareOutcome(
        Exception? Error,
        PortablePreparedImport? Prepared,
        InMemoryPortableImportStagingStore Store,
        RecordingPortableImportStaging Staging,
        CountingPhysicalStream Physical,
        SourceRecorder Recorder,
        PortableArchiveBufferBudget Budget);

    private sealed class SourceRecorder
    {
        public int AsyncReadCalls { get; private set; }

        public int MaxRequestedBytes { get; private set; }

        public long TotalBytesRead { get; private set; }

        public void Record(long offset, int length)
        {
            AsyncReadCalls++;
            MaxRequestedBytes = Math.Max(MaxRequestedBytes, length);
            TotalBytesRead += length;
        }
    }

    private sealed class CountingPhysicalStream : Stream
    {
        private readonly MemoryStream _inner;

        public CountingPhysicalStream(byte[] bytes) => _inner = new MemoryStream(bytes, false);

        public int SyncCalls { get; private set; }

        public int AsyncCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Sync();

        public override int Read(Span<byte> buffer) => Sync();

        public override int ReadByte() => Sync();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncCalls++;
            return ValueTask.FromResult(_inner.Read(buffer.Span));
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

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private int Sync()
        {
            SyncCalls++;
            throw new InvalidOperationException("Physical synchronous IO forbidden.");
        }
    }

    private sealed class RecordingPortableImportStaging(IPortableImportStaging inner)
        : IPortableImportStaging
    {
        private readonly List<PortableStagingId> _created = [];
        private readonly List<PortableStagingId> _deleted = [];
        private readonly List<PreparedPortableImportMetadata> _committed = [];

        public IReadOnlyList<PortableStagingId> Created => _created;

        public IReadOnlyList<PortableStagingId> Deleted => _deleted;

        public IReadOnlyList<PreparedPortableImportMetadata> Committed => _committed;

        public async Task<PortableStagingId> CreateAsync(
            CancellationToken cancellationToken = default)
        {
            var id = await inner.CreateAsync(cancellationToken);
            _created.Add(id);
            return id;
        }

        public Task<PortableStagingWrite> OpenMediaWriteAsync(
            PortableStagingId stagingId,
            PortableArchiveMediaEntry descriptor,
            CancellationToken cancellationToken = default) =>
            inner.OpenMediaWriteAsync(stagingId, descriptor, cancellationToken);

        public Task CompleteMediaAsync(
            PortableStagingId stagingId,
            PortableStagingWrite write,
            CancellationToken cancellationToken = default) =>
            inner.CompleteMediaAsync(stagingId, write, cancellationToken);

        public Task<Stream> OpenMediaReadAsync(
            PortableStagingId stagingId,
            PortableStagedMediaReference reference,
            CancellationToken cancellationToken = default) =>
            inner.OpenMediaReadAsync(stagingId, reference, cancellationToken);

        public Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default) =>
            inner.ListMediaAsync(stagingId, cancellationToken);

        public Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
            PortableStagingId stagingId,
            PortableArchivePayload descriptor,
            CancellationToken cancellationToken = default) =>
            inner.OpenDataWriteAsync(stagingId, descriptor, cancellationToken);

        public Task CompleteDataAsync(
            PortableStagingId stagingId,
            PortableStagingPayloadWrite write,
            CancellationToken cancellationToken = default) =>
            inner.CompleteDataAsync(stagingId, write, cancellationToken);

        public Task<Stream> OpenDataReadAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default) =>
            inner.OpenDataReadAsync(stagingId, cancellationToken);

        public Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
            PortableStagingId stagingId,
            PortableArchivePayload descriptor,
            CancellationToken cancellationToken = default) =>
            inner.OpenManifestWriteAsync(stagingId, descriptor, cancellationToken);

        public Task CompleteManifestAsync(
            PortableStagingId stagingId,
            PortableStagingPayloadWrite write,
            CancellationToken cancellationToken = default) =>
            inner.CompleteManifestAsync(stagingId, write, cancellationToken);

        public Task<Stream> OpenManifestReadAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default) =>
            inner.OpenManifestReadAsync(stagingId, cancellationToken);

        public async Task CommitPreparedImportAsync(
            PortableStagingId stagingId,
            PreparedPortableImportMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            await inner.CommitPreparedImportAsync(stagingId, metadata, cancellationToken);
            _committed.Add(metadata);
        }

        public Task<IPreparedPortableImport> RebuildPreparedImportAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default) =>
            inner.RebuildPreparedImportAsync(stagingId, cancellationToken);

        public async Task DeleteAsync(
            PortableStagingId stagingId,
            CancellationToken cancellationToken = default)
        {
            await inner.DeleteAsync(stagingId, cancellationToken);
            _deleted.Add(stagingId);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}

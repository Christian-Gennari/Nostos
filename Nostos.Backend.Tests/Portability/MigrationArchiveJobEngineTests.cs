using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 9/10 integration: the real archive engine wired into the durable job
/// worker over a real SQLite file and a real transfer root. Import preparation
/// stops at ReadyToActivate with a committed durable staging area and keeps the
/// activation headroom reserved; export publishes exactly one attempt-unique
/// verified artifact. Restart, cancellation, maintenance, stale-owner, and
/// lease-loss paths are exercised against durable state and real files.
/// </summary>
public sealed class MigrationArchiveJobEngineTests
{
    // ---------------------------------------------------------------- imports

    [Fact]
    public async Task Import_preparation_commits_durable_staging_and_stops_at_ReadyToActivate()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        await SeedLiveWorkAsync(h);
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);

        (await h.Worker.RunCycleAsync(default)).Should().BeGreaterThan(0);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.ReadyToActivate);
        job.LeaseToken.Should().BeNull();

        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        record.PreparedStagingId.Should().NotBeNull();
        record.PreparedImportMetadataJson.Should().NotBeNullOrEmpty();
        record.DestinationRevision.Should().NotBeNull();

        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var staging = scope.ServiceProvider.GetRequiredService<IPortableImportStaging>();
            var rebuilt = await staging.RebuildPreparedImportAsync(
                new PortableStagingId(record.PreparedStagingId!.Value));
            rebuilt.Metadata.FormatVersion.Should().Be(1);
            rebuilt.Metadata.MediaFiles.Should().Be(5);
            rebuilt.Media.Should().HaveCount(5);
        }

        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(1);

        // The live library was not mutated by preparation.
        (await h.WithDb(db => db.Works.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Legacy_v1_archive_prepares_with_its_durable_data_version()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = MigrationArchiveJobTestSupport.ToDataVersion(
            await MigrationArchiveJobTestSupport.ExportRepresentativeAsync(),
            1);
        var jobId = await UploadCompleteImportAsync(h, archive);

        await h.Worker.RunCycleAsync(default);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.ReadyToActivate);
        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        var metadata = MigrationPreparedMetadata.Deserialize(record.PreparedImportMetadataJson!);
        metadata!.DataVersion.Should().Be(1);
        metadata.Counts.WritingNotes.Should().Be(0);
        metadata.Counts.NoteImportBookLinks.Should().Be(0);
    }

    [Fact]
    public async Task Restart_with_uncommitted_staging_deletes_it_and_prepares_once()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);

        // Simulate a crash that created a staging area but never committed it.
        var orphanProvider = new FilePortableImportStaging(h.Paths);
        var orphanId = await orphanProvider.CreateAsync();
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PreparedStagingId, orphanId.Value)));

        await h.Worker.RunCycleAsync(default);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.ReadyToActivate);
        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        record.PreparedStagingId.Should().NotBe(orphanId.Value);
        record.PreparedImportMetadataJson.Should().NotBeNullOrEmpty();
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(1);
        Directory.Exists(h.Paths.GetStagingDirectory(orphanId.Value)).Should().BeFalse();
    }

    [Fact]
    public async Task Restart_after_staging_commit_before_job_update_tombstones_the_stale_area_and_reprepares()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);
        var session = await h.WithDb(db => db.MigrationSessionRecords.SingleAsync(s => s.JobId == jobId));

        // Run the real reader to a committed staging area, then simulate the
        // crash between the staging commit and the fenced job update. The area
        // is not referenced by a durable descriptor, so it is stale: the
        // successor tombstones it rather than adopting bytes a stale owner
        // could still delete.
        var committedProvider = new FilePortableImportStaging(h.Paths);
        var reader = new PortableArchiveReader(timeProvider: h.Clock);
        await using var source = new FilePortableArchiveSource(
            h.Paths.GetUploadArchivePath(session.Id));
        var prepared = await reader.PrepareImportAsync(source, committedProvider);
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PreparedStagingId, prepared.Metadata.StagingId.Value)));

        await h.Worker.RunCycleAsync(default);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.ReadyToActivate);
        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        record.PreparedStagingId.Should().NotBe(prepared.Metadata.StagingId.Value);
        record.PreparedImportMetadataJson.Should().NotBeNullOrEmpty();
        Directory.Exists(h.Paths.GetStagingDirectory(prepared.Metadata.StagingId.Value)).Should().BeFalse();
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(1);
    }

    [Fact]
    public async Task Restart_after_job_update_rebuilds_without_repreparing()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);
        await h.Worker.RunCycleAsync(default);

        var committed = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        var stagingDirectory = h.Paths.GetStagingDirectory(committed.PreparedStagingId!.Value);
        var mediaStamp = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.GetLastWriteTimeUtc);

        // Simulate a crash after the job update but before the terminal transition.
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Validating)
                .SetProperty(j => j.Version, j => j.Version + 1)));
        await h.Worker.RunCycleAsync(default);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.ReadyToActivate);
        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        record.PreparedImportMetadataJson.Should().Be(committed.PreparedImportMetadataJson);
        mediaStamp.Should().OnlyContain(pair => File.GetLastWriteTimeUtc(pair.Key) == pair.Value);
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(1);
    }

    [Fact]
    public async Task Invalid_archive_fails_typed_and_removes_staging_and_releases_the_reservation()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = MigrationArchiveJobTestSupport.CorruptData(
            await MigrationArchiveJobTestSupport.ExportRepresentativeAsync());
        var jobId = await UploadCompleteImportAsync(h, archive);

        await h.Worker.RunCycleAsync(default);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.Failed);
        job.FailureCode.Should().Be("malformed_data");
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(0);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Over_capacity_reservation_fails_typed_without_creating_staging()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);
        await h.WithDb(db => db.MigrationStorageReservations.Where(r => r.ClaimedJobId == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReservedBytes, 1L)));

        await h.Worker.RunCycleAsync(default);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.Failed);
        job.FailureCode.Should().Be(MigrationTransferException.StorageExhausted);
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(0);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Cancellation_mid_prepare_cancels_the_job_and_removes_staging()
    {
        var block = new WriteBarrier();
        await using var h = new MigrationEngineHarness();
        ConfigureBarrierStaging(h, block);
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);

        var run = Task.Run(() => h.Worker.RunCycleAsync(default));
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await h.WithUploads(s => s.CancelAsync(jobId, new(), default));
        block.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.Cancelled);
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(0);
        (await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId)))
            .PreparedImportMetadataJson.Should().BeNull();
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Maintenance_mid_prepare_yields_at_a_checkpoint_and_resumes_later()
    {
        var block = new WriteBarrier();
        await using var h = new MigrationEngineHarness();
        ConfigureBarrierStaging(h, block);
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);

        var run = Task.Run(() => h.Worker.RunCycleAsync(default));
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var exclusive = h.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        block.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Validating);
        var maintenance = await exclusive.WaitAsync(TimeSpan.FromSeconds(20));
        h.Maintenance.IsMaintenanceActive.Should().BeTrue();
        await maintenance.DisposeAsync();

        (await h.Worker.RunCycleAsync(default)).Should().BeGreaterThan(0);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.ReadyToActivate);
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(1);
    }

    [Fact]
    public async Task Lease_loss_mid_prepare_blocks_publication_and_the_new_owner_completes()
    {
        var block = new WriteBarrier();
        await using var h = new MigrationEngineHarness();
        ConfigureBarrierStaging(h, block);
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);

        var run = Task.Run(() => h.Worker.RunCycleAsync(default));
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // A successor takes the row: the old owner's fenced publications must fail.
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.MigrationLeaseToken, "successor")));
        block.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var stale = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        stale.PreparedImportMetadataJson.Should().BeNull("the stale owner must not publish prepared metadata");
        stale.State.Should().Be((int)MigrationJobState.Validating);

        h.Clock.Advance(MigrationJobWorker.LeaseDuration + TimeSpan.FromTicks(1));
        await h.Worker.RunCycleAsync(default);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.ReadyToActivate);
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(1);
    }

    [Fact]
    public async Task Stale_import_owner_cannot_write_into_or_commit_the_successors_area()
    {
        var block = new WriteBarrier();
        await using var h = new MigrationEngineHarness();
        ConfigureBarrierStaging(h, block);
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);

        var staleRun = Task.Run(() => h.Worker.RunCycleAsync(default));
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var staleStagingId = (await h.WithDb(db =>
            db.MigrationJobRecords.SingleAsync(j => j.Id == jobId))).PreparedStagingId;
        staleStagingId.Should().NotBeNull();

        // Cross-process takeover: the successor uses its own slots/registry, so
        // it does not cancel the stale owner in-process.
        h.Clock.Advance(MigrationJobWorker.LeaseDuration + TimeSpan.FromTicks(1));
        var successor = NewWorker(h);
        await successor.RunCycleAsync(default);
        successor.Dispose();

        var committed = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        committed.State.Should().Be((int)MigrationJobState.ReadyToActivate);
        committed.PreparedStagingId.Should().NotBe(staleStagingId!.Value);
        Directory.Exists(h.Paths.GetStagingDirectory(staleStagingId.Value)).Should().BeFalse(
            "the successor tombstones the stale attempt's area");

        block.Release.TrySetResult();
        await staleRun.WaitAsync(TimeSpan.FromSeconds(20));

        // The stale owner could not write into the successor's area or publish.
        var after = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        after.State.Should().Be((int)MigrationJobState.ReadyToActivate);
        after.PreparedStagingId.Should().Be(committed.PreparedStagingId);
        after.PreparedImportMetadataJson.Should().Be(committed.PreparedImportMetadataJson);
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(1);

        await using var scope = h.Provider.CreateAsyncScope();
        var staging = scope.ServiceProvider.GetRequiredService<IPortableImportStaging>();
        var rebuilt = await staging.RebuildPreparedImportAsync(
            new PortableStagingId(after.PreparedStagingId!.Value));
        rebuilt.Metadata.MediaFiles.Should().Be(5);
    }

    [Fact]
    public async Task In_flight_media_stream_rejects_writes_after_the_area_is_tombstoned()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var provider = new FilePortableImportStaging(h.Paths);
        var stagingId = await provider.CreateAsync();
        var bytes = new byte[16];
        var descriptor = new PortableArchiveMediaEntry(
            Guid.NewGuid(),
            "book",
            "media/books/book.epub",
            "book.epub",
            "application/epub+zip",
            bytes.Length,
            MigrationEngineHarness.Hash(bytes));
        var write = await provider.OpenMediaWriteAsync(stagingId, descriptor);

        await provider.DeleteAsync(stagingId);

        var act = async () => await write.Stream.WriteAsync(bytes);
        (await act.Should().ThrowAsync<PortableStagingException>())
            .Which.IsNotFound.Should().BeTrue(
                "a stale in-flight media stream must fail typed once its area is tombstoned");
    }

    [Fact]
    public async Task Engine_registration_reports_both_phases_available()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var availability = h.Provider.GetRequiredService<IMigrationPhaseAvailability>();
        availability.IsAvailable(MigrationDirection.Import).Should().BeTrue();
        availability.IsAvailable(MigrationDirection.Export).Should().BeTrue();
    }

    // ------------------------------------------------------- revision baseline

    [Fact]
    public async Task Job_creation_captures_the_destination_revision_and_preparation_never_overwrites_it()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        await SeedLiveWorkAsync(h);
        await h.WithDb(async db =>
        {
            db.LibraryStates.Add(new LibraryState { StateVersion = "1" });
            await db.SaveChangesAsync();
        });
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();

        var jobId = await CreateImportJobThroughServiceAsync(h, "revision-key", archive.LongLength);
        var creationRevision = (await h.WithDb(db =>
            db.MigrationJobRecords.SingleAsync(j => j.Id == jobId))).DestinationRevision;
        creationRevision.Should().NotBeNullOrEmpty();

        // A portable mutation between creation and preparation must not be
        // folded into the baseline: activation's recheck must detect it.
        await h.WithDb(db => db.LibraryStates
            .Where(s => s.Id == LibraryState.WellKnownId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StateVersion, "9999")));
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var provider = scope.ServiceProvider.GetRequiredService<ILibraryDestinationRevisionProvider>();
            (await provider.GetCurrentAsync(default)).Should().NotBe(creationRevision);
        }

        var session = await h.StartAsync(jobId, archive);
        await h.Upload(jobId, session, archive);
        await h.Complete(jobId, session);
        await h.Worker.RunCycleAsync(default);

        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        record.State.Should().Be((int)MigrationJobState.ReadyToActivate);
        record.DestinationRevision.Should().Be(creationRevision);
    }

    [Fact]
    public void Destination_revision_is_read_only_through_the_provider()
    {
        var directory = FindMigrationSourceDirectory();
        var offenders = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("StateVersion", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty(
            "the revision must be read through ILibraryDestinationRevisionProvider, not directly");
    }

    // -------------------------------------------------- reservation retention

    [Fact]
    public async Task Ready_to_activate_keeps_the_activation_headroom_reserved()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);
        await h.Worker.RunCycleAsync(default);

        var session = await h.WithDb(db => db.MigrationSessionRecords.SingleAsync(s => s.JobId == jobId));
        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId));
        var metadata = MigrationPreparedMetadata.Deserialize(record.PreparedImportMetadataJson!)!;
        var reservation = await h.WithDb(db =>
            db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId));

        reservation.ReleasedAtUtc.Should().BeNull("activation still needs the recovery headroom");
        var expectedMaterialized = Math.Min(
            reservation.ReservedBytes,
            session.TotalBytes + metadata.MediaBytes + metadata.DataBytes);
        reservation.MaterializedBytes.Should().Be(expectedMaterialized);
        (reservation.ReservedBytes - reservation.MaterializedBytes).Should().BeGreaterThan(0);

        // A competing admission that exceeds free space minus the remaining
        // reservation is rejected while the ready job holds its headroom.
        var outstanding = reservation.ReservedBytes - reservation.MaterializedBytes;
        h.Volume.AvailableFreeSpaceBytes = outstanding + 1;
        await using var scope = h.Provider.CreateAsyncScope();
        var capacity = scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>();
        var admitted = await capacity.TryReserveAsync(2, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        admitted.IsAdmitted.Should().BeFalse(
            "the unmaterialized recovery headroom stays reserved until activation");
    }

    [Fact]
    public async Task Cancel_of_a_ready_import_releases_the_reservation_and_staging()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);
        await h.Worker.RunCycleAsync(default);

        await h.WithUploads(s => s.CancelAsync(jobId, new(), default));

        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Cancelled);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(0);
    }

    [Fact]
    public async Task Expiry_of_a_ready_import_releases_the_reservation_and_staging()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);
        await h.Worker.RunCycleAsync(default);

        h.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromTicks(1));
        await h.Sweep();

        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Expired);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
        MigrationArchiveJobTestSupport.CountStagingAreas(h).Should().Be(0);
    }

    // ---------------------------------------------------------------- exports

    [Fact]
    public async Task Export_publishes_one_verified_artifact_that_imports_cleanly()
    {
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);

        (await h.Worker.RunCycleAsync(default)).Should().BeGreaterThan(0);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.Completed);
        (await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == jobId)))
            .CompletedAtUtc.Should().NotBeNull();

        var artifact = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        artifact.State.Should().Be((int)MigrationExportArtifactState.Available);
        artifact.ExpiresAtUtc.Should().BeAfter(h.Clock.GetUtcNow().UtcDateTime);
        var artifactPath = h.Paths.ResolveStorageKey(artifact.StorageKey);
        File.Exists(artifactPath).Should().BeTrue();
        new FileInfo(artifactPath).Length.Should().Be(artifact.SizeBytes);
        MigrationArchiveJobTestSupport.HashFile(artifactPath).Should().Be(artifact.Sha256);
        MigrationArchiveJobTestSupport.CountExportFiles(h, jobId).Should().Be(1);

        await using (var stream = File.OpenRead(artifactPath))
        {
            await using var target = await LocalPortableTestLibrary.CreateAsync();
            var imported = await target.Portability().ImportAsync(stream);
            imported.IntegrityVerified.Should().BeTrue();
            imported.Counts.Books.Should().Be(4);
            imported.MediaFiles.Should().Be(5);
        }

        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Export_restart_mid_generation_discards_partial_temp_and_publishes_once()
    {
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);

        // Crash state: Transferring with a Preparing row and a partial temp.
        var token = await h.WithJobs(s => s.TryAcquireLeaseAsync(jobId, MigrationJobWorker.LeaseDuration, default));
        await h.WithJobs(s => s.TransitionAsync(jobId, MigrationJobState.Preparing, token!, default));
        await h.WithJobs(s => s.TransitionAsync(jobId, MigrationJobState.Transferring, token!, default));
        var tempKey = h.Paths.GetExportAttemptTempStorageKey(jobId, 1, new string('a', 64));
        h.Paths.EnsureDirectoryExists(h.Paths.GetExportDirectory(jobId));
        await File.WriteAllTextAsync(h.Paths.ResolveStorageKey(tempKey), "partial");
        await h.WithDb(async db =>
        {
            db.MigrationExportArtifactRecords.Add(new MigrationExportArtifactRecord
            {
                JobId = jobId,
                State = (int)MigrationExportArtifactState.Preparing,
                StorageKey = tempKey,
                FileName = ExportArtifactPhaseHandler.ArtifactFileName,
                ContentType = "application/vnd.nostos.portable+zip",
                CreatedAtUtc = h.Clock.GetUtcNow().UtcDateTime,
                ExpiresAtUtc = h.Clock.GetUtcNow().AddDays(1).UtcDateTime,
            });
            await db.SaveChangesAsync();
        });
        await h.WithJobs(s => s.ReleaseLeaseAsync(jobId, token!, default));

        await h.Worker.RunCycleAsync(default);

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.Completed);
        var artifact = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        artifact.State.Should().Be((int)MigrationExportArtifactState.Available);
        artifact.StorageKey.Should().NotBe(tempKey);
        artifact.SizeBytes.Should().BeGreaterThan(16);
        File.Exists(h.Paths.ResolveStorageKey(artifact.StorageKey)).Should().BeTrue();
        MigrationArchiveJobTestSupport.CountExportFiles(h, jobId).Should().Be(1);
    }

    [Fact]
    public async Task Export_restart_after_publication_reuses_the_available_artifact()
    {
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);
        await h.Worker.RunCycleAsync(default);

        var artifact = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        var artifactPath = h.Paths.ResolveStorageKey(artifact.StorageKey);
        var stamp = File.GetLastWriteTimeUtc(artifactPath);
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Validating)
                .SetProperty(j => j.Version, j => j.Version + 1)));

        await h.Worker.RunCycleAsync(default);

        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);
        File.GetLastWriteTimeUtc(artifactPath).Should().Be(stamp);
        MigrationArchiveJobTestSupport.CountExportFiles(h, jobId).Should().Be(1);
    }

    [Fact]
    public async Task Available_row_with_missing_file_is_reset_and_regenerated()
    {
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);
        await h.Worker.RunCycleAsync(default);

        var before = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        File.Delete(h.Paths.ResolveStorageKey(before.StorageKey));
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Validating)
                .SetProperty(j => j.Version, j => j.Version + 1)));

        await h.Worker.RunCycleAsync(default);

        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);
        var after = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        after.State.Should().Be((int)MigrationExportArtifactState.Available);
        after.StorageKey.Should().NotBe(before.StorageKey);
        MigrationArchiveJobTestSupport.HashFile(h.Paths.ResolveStorageKey(after.StorageKey))
            .Should().Be(after.Sha256);
    }

    [Fact]
    public async Task Cleanup_sweep_removes_unreferenced_export_files_but_keeps_the_artifact()
    {
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);
        await h.Worker.RunCycleAsync(default);

        var artifact = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        var artifactPath = h.Paths.ResolveStorageKey(artifact.StorageKey);
        var directory = h.Paths.GetExportDirectory(jobId);
        var staleTemp = h.Paths.ResolveStorageKey(
            h.Paths.GetExportAttemptTempStorageKey(jobId, 1, new string('b', 64)));
        var staleFinal = h.Paths.ResolveStorageKey(
            h.Paths.GetExportAttemptArtifactStorageKey(jobId, 1, new string('c', 64)));
        await File.WriteAllTextAsync(staleTemp, "stale");
        await File.WriteAllTextAsync(staleFinal, "stale");

        await h.Sweep();

        File.Exists(artifactPath).Should().BeTrue();
        File.Exists(staleTemp).Should().BeFalse();
        File.Exists(staleFinal).Should().BeFalse();
        Directory.EnumerateFiles(directory).Should().ContainSingle();
        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId)))
            .State.Should().Be((int)MigrationExportArtifactState.Available);
    }

    [Fact]
    public async Task Export_lease_loss_mid_generation_cannot_publish_and_the_new_owner_completes()
    {
        var blocking = new BlockingArchiveService();
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage, blocking);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);

        var run = Task.Run(() => h.Worker.RunCycleAsync(default));
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.MigrationLeaseToken, "successor")));
        blocking.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId)))
            .State.Should().Be((int)MigrationExportArtifactState.Preparing,
                "the stale owner must not publish an artifact row");

        h.Clock.Advance(MigrationJobWorker.LeaseDuration + TimeSpan.FromTicks(1));
        await h.Worker.RunCycleAsync(default);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);
        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId)))
            .State.Should().Be((int)MigrationExportArtifactState.Available);
        MigrationArchiveJobTestSupport.CountExportFiles(h, jobId).Should().Be(1);
    }

    [Fact]
    public async Task Stale_export_owner_cannot_rename_after_a_successor_publishes()
    {
        await using var h = new MigrationEngineHarness();
        IBookAssetStorage storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        var hooks = new MigrationArchivePhaseTestHooks();
        h.Configure = services =>
        {
            services.RemoveAll<IBookAssetStorage>();
            services.AddSingleton(storage);
            services.AddSingleton(hooks);
        };
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);

        var entered = Signal();
        var release = Signal();
        var blocked = 0;
        hooks.BeforeExportRename = () =>
        {
            if (Interlocked.Exchange(ref blocked, 1) != 0)
            {
                return;
            }

            entered.TrySetResult();
            release.Task.Wait(TimeSpan.FromSeconds(30));
        };

        var staleRun = Task.Run(() => h.Worker.RunCycleAsync(default));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        h.Clock.Advance(MigrationJobWorker.LeaseDuration + TimeSpan.FromTicks(1));
        var successor = NewWorker(h);
        await successor.RunCycleAsync(default);
        successor.Dispose();

        var published = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        published.State.Should().Be((int)MigrationExportArtifactState.Available);
        var publishedPath = h.Paths.ResolveStorageKey(published.StorageKey);
        var publishedHash = MigrationArchiveJobTestSupport.HashFile(publishedPath);

        release.TrySetResult();
        await staleRun.WaitAsync(TimeSpan.FromSeconds(20));

        // The stale owner never renamed: the successor's artifact is untouched.
        File.Exists(publishedPath).Should().BeTrue();
        MigrationArchiveJobTestSupport.HashFile(publishedPath).Should().Be(publishedHash);
        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId)))
            .StorageKey.Should().Be(published.StorageKey);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);

        await h.Sweep();
        MigrationArchiveJobTestSupport.CountExportFiles(h, jobId).Should().Be(1);
    }

    [Fact]
    public async Task Fenced_publication_failure_removes_the_attempts_own_file()
    {
        await using var h = new MigrationEngineHarness();
        IBookAssetStorage storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        var hooks = new MigrationArchivePhaseTestHooks();
        h.Configure = services =>
        {
            services.RemoveAll<IBookAssetStorage>();
            services.AddSingleton(storage);
            services.AddSingleton(hooks);
        };
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);

        string? renamedPath = null;
        var swapped = 0;
        hooks.AfterExportRename = path =>
        {
            if (Interlocked.Exchange(ref swapped, 1) != 0)
            {
                return;
            }

            renamedPath = path;
            // Lose the lease between the rename and the fenced row update.
            h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.MigrationLeaseToken, "successor")))
                .GetAwaiter().GetResult();
        };

        await h.Worker.RunCycleAsync(default);

        renamedPath.Should().NotBeNull();
        File.Exists(renamedPath!).Should().BeFalse("the worker deletes its own unpublished file");
        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId)))
            .State.Should().Be((int)MigrationExportArtifactState.Preparing);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Validating);

        h.Clock.Advance(MigrationJobWorker.LeaseDuration + TimeSpan.FromTicks(1));
        await h.Worker.RunCycleAsync(default);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);
        MigrationArchiveJobTestSupport.CountExportFiles(h, jobId).Should().Be(1);
    }

    [Fact]
    public async Task Export_cancellation_mid_generation_leaves_no_artifact_or_temp()
    {
        var blocking = new BlockingArchiveService();
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage, blocking);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);

        var run = Task.Run(() => h.Worker.RunCycleAsync(default));
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await h.WithUploads(s => s.CancelAsync(jobId, new(), default));
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var job = await h.WithJobs(s => s.GetAsync(jobId, default));
        job!.State.Should().Be(MigrationJobState.Cancelled);
        MigrationArchiveJobTestSupport.CountExportFiles(h, jobId).Should().Be(0);
        var artifact = await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId));
        artifact.State.Should().NotBe((int)MigrationExportArtifactState.Available);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Export_artifact_expires_on_retention_and_sweep_deletes_it_without_rewriting_history()
    {
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);
        var jobId = await RunnableExportAsync(h);
        await h.Worker.RunCycleAsync(default);
        var artifactPath = h.Paths.ResolveStorageKey(
            (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId))).StorageKey);
        File.Exists(artifactPath).Should().BeTrue();

        h.Clock.Advance(h.Settings.ExportRetentionTtl + TimeSpan.FromTicks(1));
        await h.Sweep();

        File.Exists(artifactPath).Should().BeFalse();
        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId)))
            .State.Should().Be((int)MigrationExportArtifactState.Deleted);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);
    }

    [Fact]
    public async Task Export_charges_the_capture_lease_to_the_operation_budget()
    {
        await using var h = new MigrationEngineHarness();
        var storage = MigrationArchiveJobTestSupport.CreateFileStorage(h.DirectoryPath);
        ConfigureExportLibrary(h, storage);
        await h.InitializeAsync();
        await MigrationArchiveJobTestSupport.SeedRepresentativeAsync(h, storage);

        var budget = new PortableArchiveBufferBudget(PortableArchiveLimits.MaxExplicitBufferBytes);
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var service = (PortableArchiveService)scope.ServiceProvider.GetRequiredService<IPortableArchiveService>();
            await using var sink = new StreamPortableArchiveSink(Stream.Null, leaveOpen: true);
            await service.ExportAsync(sink, progress: null, budget, default);
        }

        budget.HighWaterBytes.Should().BeGreaterThanOrEqualTo(
            PortableArchiveLimits.MaxSynchronousZipWriteBufferBytes,
            "the synchronous capture lease must be charged to the caller's operation budget");
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Real_staging_cleanup_sweeps_only_unprotected_areas_past_the_ttl()
    {
        await using var h = new MigrationEngineHarness();
        h.Configure = services =>
            services.AddScoped<IMigrationStagingCleanup, FilePortableImportStagingCleanup>();
        await h.InitializeAsync();

        var provider = new FilePortableImportStaging(h.Paths);
        var abandoned = await provider.CreateAsync();
        var fresh = await provider.CreateAsync();
        var protectedId = await provider.CreateAsync();

        var job = await h.NewJobAsync();
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == job)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.PreparedStagingId, protectedId.Value)
                .SetProperty(j => j.State, (int)MigrationJobState.ReadyToActivate)
                .SetProperty(j => j.ExpiresAtUtc, h.Clock.GetUtcNow().AddDays(2).UtcDateTime)));

        var stale = h.Clock.GetUtcNow().AddHours(-25).UtcDateTime;
        Directory.SetLastWriteTimeUtc(h.Paths.GetStagingDirectory(abandoned.Value), stale);
        Directory.SetLastWriteTimeUtc(h.Paths.GetStagingDirectory(protectedId.Value), stale);
        Directory.SetLastWriteTimeUtc(
            h.Paths.GetStagingDirectory(fresh.Value),
            h.Clock.GetUtcNow().UtcDateTime);

        await h.Sweep();

        Directory.Exists(h.Paths.GetStagingDirectory(abandoned.Value)).Should().BeFalse();
        Directory.Exists(h.Paths.GetStagingDirectory(fresh.Value)).Should().BeTrue();
        Directory.Exists(h.Paths.GetStagingDirectory(protectedId.Value)).Should().BeTrue();
        await h.Sweep();
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<Guid> UploadCompleteImportAsync(MigrationEngineHarness h, byte[] archive)
    {
        var jobId = await h.NewJobAsync();
        var session = await h.StartAsync(jobId, archive);
        await h.Upload(jobId, session, archive);
        await h.Complete(jobId, session);
        return jobId;
    }

    private static async Task<Guid> CreateImportJobThroughServiceAsync(
        MigrationEngineHarness h,
        string key,
        long archiveBytes)
    {
        await using var scope = h.Provider.CreateAsyncScope();
        var capacity = scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>();
        var required = TransferCapacityMath.CalculateHostPeakReservationBytes(
            archiveBytes,
            MigrationContractLimits.MinChunkBytes,
            h.Settings);
        var reservation = (await capacity.TryReserveAsync(
            required,
            MigrationSessionPurpose.Import,
            TimeSpan.FromMinutes(15),
            default)).ReservationId!.Value;
        var service = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationJobService>();
        var result = await service.CreateAsync(
            new MigrationCreateJobRequest(MigrationDirection.Import, key, reservation),
            default);
        return result.Resource!.Id;
    }

    private static async Task<Guid> RunnableExportAsync(MigrationEngineHarness h)
    {
        var id = await h.NewJobAsync(MigrationDirection.Export);
        await using var scope = h.Provider.CreateAsyncScope();
        var capacity = scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>();
        var reservation = (await capacity.TryReserveAsync(
            1024,
            MigrationSessionPurpose.Export,
            TimeSpan.FromMinutes(15),
            default)).ReservationId!.Value;
        await capacity.ClaimAsync(reservation, id, default);
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(j => j.ReservationId, reservation)
                .SetProperty(j => j.ReservedStorageBytes, 1024)));
        return id;
    }

    private static async Task SeedLiveWorkAsync(MigrationEngineHarness h) =>
        await h.WithDb(async db =>
        {
            db.Works.Add(new WorkModel
            {
                Id = Guid.NewGuid(),
                Title = "Live work",
                Author = "Live author",
                NormalizedTitle = "LIVE WORK",
                NormalizedAuthor = "LIVE AUTHOR",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });

    private static void ConfigureExportLibrary(
        MigrationEngineHarness h,
        IBookAssetStorage storage,
        BlockingArchiveService? blocking = null)
    {
        h.Configure = services =>
        {
            services.RemoveAll<IBookAssetStorage>();
            services.AddSingleton(storage);
            if (blocking is not null)
            {
                services.RemoveAll<IPortableArchiveService>();
                services.AddScoped<IPortableArchiveService>(provider =>
                {
                    blocking.Inner = new PortableArchiveService(
                        provider.GetRequiredService<NostosDbContext>(),
                        provider.GetRequiredService<IBookAssetStorage>(),
                        NullLogger<PortableArchiveService>.Instance,
                        bookTextScheduler: null,
                        timeProvider: provider.GetRequiredService<TimeProvider>());
                    return blocking;
                });
            }
        };
    }

    private static void ConfigureBarrierStaging(MigrationEngineHarness h, WriteBarrier barrier)
    {
        var hooks = new FilePortableImportStagingHooks
        {
            BeforeStreamWrite = barrier.WaitOnce,
        };
        h.Configure = services =>
        {
            services.RemoveAll<IPortableImportStaging>();
            services.AddScoped<IPortableImportStaging>(_ => new FilePortableImportStaging(
                new TransferPathResolver(Path.Combine(h.DirectoryPath, "transfers")),
                hooks,
                coordinatorScope: string.Empty));
        };
    }

    private static MigrationJobWorker NewWorker(MigrationEngineHarness h) =>
        new(
            h.Provider.GetRequiredService<IServiceScopeFactory>(),
            h.Clock,
            new MigrationJobCancellationRegistry(),
            new MigrationProcessingSlots(Options.Create(h.Settings)),
            NullLogger<MigrationJobWorker>.Instance,
            h.Provider.GetRequiredService<IMigrationMaintenanceGate>());

    private static string FindMigrationSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Nostos.Product",
                "Services",
                "Portability",
                "Migration");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the migration source directory.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Blocks the first staged write until the test releases it.</summary>
    private sealed class WriteBarrier
    {
        private int _waited;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();

        internal void WaitOnce()
        {
            if (Interlocked.Exchange(ref _waited, 1) != 0)
            {
                return;
            }

            Entered.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Wraps the real export service to hold one generation open.</summary>
    private sealed class BlockingArchiveService : IPortableArchiveService
    {
        internal IPortableArchiveService? Inner { get; set; }

        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();

        public Task<PortableExportResult> ExportAsync(Stream destination, CancellationToken cancellationToken = default) =>
            Inner!.ExportAsync(destination, cancellationToken);

        public async Task<PortableExportResult> ExportAsync(
            IPortableArchiveSink destination,
            IProgress<PortableArchiveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await Inner!.ExportAsync(destination, progress, cancellationToken);
        }

        public Task<PortableImportResult> ImportAsync(Stream source, CancellationToken cancellationToken = default) =>
            Inner!.ImportAsync(source, cancellationToken);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 9/10 integration: the real archive engine wired into the durable job
/// worker over a real SQLite file and a real transfer root. Import preparation
/// stops at ReadyToActivate with a committed durable staging area; export
/// publishes exactly one verified artifact. Every restart, cancellation,
/// maintenance, and lease-loss path is exercised against durable state.
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
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.ClaimedJobId == jobId)))
            .ReleasedAtUtc.Should().NotBeNull();
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
    public async Task Restart_after_staging_commit_before_job_update_adopts_the_committed_descriptor()
    {
        await using var h = new MigrationEngineHarness();
        await h.InitializeAsync();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();
        var jobId = await UploadCompleteImportAsync(h, archive);
        var session = await h.WithDb(db => db.MigrationSessionRecords.SingleAsync(s => s.JobId == jobId));

        // Run the real reader to a committed staging area, then simulate the
        // crash between the staging commit and the fenced job update.
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
        record.PreparedStagingId.Should().Be(prepared.Metadata.StagingId.Value);
        var metadata = MigrationPreparedMetadata.Deserialize(record.PreparedImportMetadataJson!);
        metadata!.PreparedAtUtc.Should().Be(prepared.Metadata.PreparedAtUtc);
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
        var artifactPath = h.Paths.GetExportArtifactPath(jobId);
        File.Exists(artifactPath).Should().BeTrue();
        new FileInfo(artifactPath).Length.Should().Be(artifact.SizeBytes);
        MigrationArchiveJobTestSupport.HashFile(artifactPath).Should().Be(artifact.Sha256);
        File.Exists(h.Paths.GetExportTempPath(jobId)).Should().BeFalse();

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
        h.Paths.EnsureDirectoryExists(h.Paths.GetExportDirectory(jobId));
        await File.WriteAllTextAsync(h.Paths.GetExportTempPath(jobId), "partial");
        await h.WithDb(async db =>
        {
            db.MigrationExportArtifactRecords.Add(new MigrationExportArtifactRecord
            {
                JobId = jobId,
                State = (int)MigrationExportArtifactState.Preparing,
                StorageKey = h.Paths.GetExportArtifactStorageKey(jobId),
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
        artifact.SizeBytes.Should().BeGreaterThan(16);
        File.Exists(h.Paths.GetExportTempPath(jobId)).Should().BeFalse();
        Directory.EnumerateFiles(h.Paths.GetExportDirectory(jobId)).Should().ContainSingle(
            path => Path.GetFileName(path) == TransferPathResolver.ExportFileName);
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

        var artifactPath = h.Paths.GetExportArtifactPath(jobId);
        var stamp = File.GetLastWriteTimeUtc(artifactPath);
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Validating)
                .SetProperty(j => j.Version, j => j.Version + 1)));

        await h.Worker.RunCycleAsync(default);

        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);
        File.GetLastWriteTimeUtc(artifactPath).Should().Be(stamp);
        File.Exists(h.Paths.GetExportTempPath(jobId)).Should().BeFalse();
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
        File.Exists(h.Paths.GetExportArtifactPath(jobId)).Should().BeFalse();
        File.Exists(h.Paths.GetExportTempPath(jobId)).Should().BeFalse();
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
        File.Exists(h.Paths.GetExportArtifactPath(jobId)).Should().BeTrue();

        h.Clock.Advance(h.Settings.ExportRetentionTtl + TimeSpan.FromTicks(1));
        await h.Sweep();

        File.Exists(h.Paths.GetExportArtifactPath(jobId)).Should().BeFalse();
        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync(a => a.JobId == jobId)))
            .State.Should().Be((int)MigrationExportArtifactState.Deleted);
        (await h.WithJobs(s => s.GetAsync(jobId, default)))!.State.Should().Be(MigrationJobState.Completed);
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

    // ---------------------------------------------------------------- helpers

    private static async Task<Guid> UploadCompleteImportAsync(MigrationEngineHarness h, byte[] archive)
    {
        var jobId = await h.NewJobAsync();
        var session = await h.StartAsync(jobId, archive);
        await h.Upload(jobId, session, archive);
        await h.Complete(jobId, session);
        return jobId;
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

    /// <summary>Blocks the first staged write until the test releases it.</summary>
    private sealed class WriteBarrier
    {
        private int _waited;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

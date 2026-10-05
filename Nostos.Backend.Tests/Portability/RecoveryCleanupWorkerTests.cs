using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 10 scheduling tests for the recovery expiry cleanup worker. Deletion
/// itself is the existing guarded cleanup; these tests prove the scheduled pass
/// releases reservations, keeps every protected copy, is idempotent, converges
/// after an interrupted deletion, skips maintenance/fail-closed and projects a
/// deleted copy onto the job's durable recovery status.
/// </summary>
public sealed class RecoveryCleanupWorkerTests
{
    [Fact]
    public async Task ExpiredCopy_IsRemovedAndReleased_WhileUnexpiredRestoringAndJournalReferencedAreKept()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var expiredAt = bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays);
        var expired = bed.SeedExpiredCopy(expiredAt);
        var unexpired = bed.SeedExpiredCopy(bed.Clock.UtcNow);
        var restoring = bed.SeedExpiredCopy(expiredAt, MigrationRecoveryStatus.Restoring);
        var referenced = bed.SeedExpiredCopy(expiredAt);
        bed.SeedJournal(referenced.JobId);

        var result = await bed.CreateCleanupWorker().RunBatchAsync(default);

        result.Ran.Should().BeTrue();
        result.RemovedRecoveryCopies.Should().Be(1);
        Directory.Exists(bed.Paths.PreviousMedia(expired.JobId)).Should().BeFalse();
        File.Exists(bed.Paths.PreviousDatabase(expired.JobId)).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(bed.Paths.RecoveryManifest(expired.JobId))).Should().BeFalse();

        Directory.Exists(bed.Paths.PreviousMedia(unexpired.JobId)).Should().BeTrue("an unexpired copy is retained");
        Directory.Exists(bed.Paths.PreviousMedia(restoring.JobId)).Should().BeTrue("a restore in progress is untouchable");
        Directory.Exists(bed.Paths.PreviousMedia(referenced.JobId)).Should().BeTrue("an unresolved journal protects the copy");

        await using (var db = bed.OpenDatabase())
        {
            var rows = await db.MigrationStorageReservations.AsNoTracking()
                .Where(row => row.Id == expired.ReservationId || row.Id == unexpired.ReservationId)
                .ToArrayAsync();
            rows.Single(row => row.Id == expired.ReservationId).ReleasedAtUtc.Should().NotBeNull(
                "the reservation is released only after the physical copy is gone");
            rows.Single(row => row.Id == unexpired.ReservationId).ReleasedAtUtc.Should().BeNull();
        }

        var again = await bed.CreateCleanupWorker().RunBatchAsync(default);
        again.RemovedRecoveryCopies.Should().Be(0, "repeated cleanup is idempotent");
    }

    [Fact]
    public async Task Pass_IsSkippedDuringExclusiveMaintenance_AndWhenFailClosed()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var expired = bed.SeedExpiredCopy(
            bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays));

        var exclusive = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var duringMaintenance = await bed.CreateCleanupWorker().RunBatchAsync(default);
        duringMaintenance.Ran.Should().BeFalse("no pass may run inside the exclusive window");
        duringMaintenance.RemovedRecoveryCopies.Should().Be(0);
        Directory.Exists(bed.Paths.PreviousMedia(expired.JobId)).Should().BeTrue();
        await exclusive.DisposeAsync();

        bed.Gate.FailClosedForRecovery();
        var failClosed = await bed.CreateCleanupWorker().RunBatchAsync(default);
        failClosed.Ran.Should().BeFalse("a host awaiting startup reconciliation must not sweep");
        Directory.Exists(bed.Paths.PreviousMedia(expired.JobId)).Should().BeTrue();
    }

    [Fact]
    public async Task InterruptedDeletion_ConvergesOnTheNextPass()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var expiredAt = bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays);
        var mediaGone = bed.SeedExpiredCopy(expiredAt);
        bed.Manifests.CreateDeletionMarker(mediaGone.JobId);
        Directory.Delete(bed.Paths.PreviousMedia(mediaGone.JobId), recursive: true);
        var databaseGone = bed.SeedExpiredCopy(expiredAt);
        bed.Manifests.CreateDeletionMarker(databaseGone.JobId);
        File.Delete(bed.Paths.PreviousDatabase(databaseGone.JobId));

        var result = await bed.CreateCleanupWorker().RunBatchAsync(default);

        result.RemovedRecoveryCopies.Should().Be(2);
        foreach (var jobId in new[] { mediaGone.JobId, databaseGone.JobId })
        {
            Directory.Exists(Path.GetDirectoryName(bed.Paths.RecoveryManifest(jobId))).Should().BeFalse();
            bed.Manifests.HasDeletionMarker(jobId).Should().BeFalse();
        }

        await using var db = bed.OpenDatabase();
        var released = await db.MigrationStorageReservations.AsNoTracking()
            .Where(row => row.ReleasedAtUtc != null)
            .Select(row => row.Id)
            .ToArrayAsync();
        released.Should().Contain(mediaGone.ReservationId).And.Contain(databaseGone.ReservationId);
    }

    [Fact]
    public async Task DeletedCopy_IsProjectedOntoTheJobRecoveryStatus()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var expiredAt = bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays);
        var deletedJob = Guid.NewGuid();
        bed.SeedExpiredCopy(expiredAt, jobId: deletedJob);
        await bed.SeedJobAsync(deletedJob, MigrationJobState.Completed, MigrationRecoveryStatus.Available);
        var retainedJob = Guid.NewGuid();
        bed.SeedExpiredCopy(bed.Clock.UtcNow, jobId: retainedJob);
        await bed.SeedJobAsync(retainedJob, MigrationJobState.Completed, MigrationRecoveryStatus.Available);
        var before = await bed.ReadJobAsync(deletedJob);

        await bed.CreateCleanupWorker().RunBatchAsync(default);

        var after = await bed.ReadJobAsync(deletedJob);
        after.RecoveryStatus.Should().Be((int)MigrationRecoveryStatus.Expired,
            "a deleted copy must not keep reporting an available recovery");
        after.Version.Should().BeGreaterThan(before.Version);
        (await bed.ReadJobAsync(retainedJob)).RecoveryStatus.Should().Be(
            (int)MigrationRecoveryStatus.Available, "an unexpired copy keeps its status");
    }

    [Fact]
    public async Task RestoredSourceCopy_ExpiresAndReleasesItsReservation_WhileReplacementAvailableRemains()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var source = bed.SeedExpiredCopy(
            bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays - 1),
            MigrationRecoveryStatus.Restored);
        var replacement = bed.SeedExpiredCopy(bed.Clock.UtcNow, MigrationRecoveryStatus.Available);

        var result = await bed.CreateCleanupWorker().RunBatchAsync(default);

        result.RemovedRecoveryCopies.Should().Be(1, "an expired Restored provenance copy is cleanup-eligible");
        Directory.Exists(bed.Paths.PreviousMedia(source.JobId)).Should().BeFalse();
        File.Exists(bed.Paths.PreviousDatabase(source.JobId)).Should().BeFalse();
        bed.Manifests.Read(source.JobId).Should().BeNull();
        await using (var db = bed.OpenDatabase())
        {
            var rows = await db.MigrationStorageReservations.AsNoTracking()
                .Where(row => row.Id == source.ReservationId || row.Id == replacement.ReservationId)
                .ToArrayAsync();
            rows.Single(row => row.Id == source.ReservationId).ReleasedAtUtc.Should().NotBeNull(
                "the consumed source reservation is released after physical removal");
            rows.Single(row => row.Id == replacement.ReservationId).ReleasedAtUtc.Should().BeNull();
        }

        Directory.Exists(bed.Paths.PreviousMedia(replacement.JobId)).Should().BeTrue(
            "the replacement recovery copy remains available");
        bed.Manifests.Read(replacement.JobId)!.Status.Should().Be(MigrationRecoveryStatus.Available);
    }

    [Fact]
    public async Task RestoredSourceCopy_IsKeptBeforeItsRetentionExpires()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var source = bed.SeedExpiredCopy(bed.Clock.UtcNow, MigrationRecoveryStatus.Restored);

        var result = await bed.CreateCleanupWorker().RunBatchAsync(default);

        result.RemovedRecoveryCopies.Should().Be(0);
        Directory.Exists(bed.Paths.PreviousMedia(source.JobId)).Should().BeTrue();
        File.Exists(bed.Paths.PreviousDatabase(source.JobId)).Should().BeTrue();
    }
}

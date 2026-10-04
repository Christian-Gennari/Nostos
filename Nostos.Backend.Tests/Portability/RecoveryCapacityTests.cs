using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class RecoveryCapacityTests
{
    [Fact]
    public void CandidateAdmission_AcceptsAndRejectsAtExactPerVolumeBoundaries()
    {
        using var bed = new RecoveryTestBed();
        var capacity = new SelfHostedActivationCapacity(bed.Probe, Options.Create(bed.Options));
        var sizing = new SelfHostedActivationSizing(1_000, 2_000, 5_000, 7_000, StagedBytes: 300);

        bed.Probe.Set(bed.Paths.LiveDatabase, 1_000, 1_000_000);
        bed.Probe.Set(bed.Paths.LiveMedia, 2_000, 1_000_000);
        bed.Probe.Set(bed.Root, 300, 1_000_000);
        Action exact = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, bed.Root, sizing);
        exact.Should().NotThrow();

        bed.Probe.Set(bed.Paths.LiveDatabase, 999, 1_000_000);
        Action databaseShort = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, bed.Root, sizing);
        databaseShort.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.StorageExhausted);

        bed.Probe.Set(bed.Paths.LiveDatabase, 1_000_000, 1_000_000);
        bed.Probe.Set(bed.Paths.LiveMedia, 1_999, 1_000_000);
        Action mediaShort = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, bed.Root, sizing);
        mediaShort.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.StorageExhausted,
                "the media volume is a different volume and must be checked independently");

        bed.Probe.Set(bed.Paths.LiveMedia, 1_000_000, 1_000_000);
        bed.Probe.Set(bed.Root, 299, 1_000_000);
        Action stagedShort = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, bed.Root, sizing);
        stagedShort.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.StorageExhausted);
    }

    [Fact]
    public void CandidateAdmission_OnASharedVolume_ChargesBothComponentsTogether()
    {
        using var bed = new RecoveryTestBed();
        var capacity = new SelfHostedActivationCapacity(bed.Probe, Options.Create(bed.Options));
        var sizing = new SelfHostedActivationSizing(600, 500, 5_000, 7_000);

        bed.Probe.Set(bed.Paths.LiveDatabase, 1_100, 1_000_000, volume: "shared");
        bed.Probe.Set(bed.Paths.LiveMedia, 1_100, 1_000_000, volume: "shared");
        Action exact = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, transferRoot: null, sizing);
        exact.Should().NotThrow("600 + 500 exactly fills the shared volume");

        bed.Probe.Set(bed.Paths.LiveDatabase, 1_099, 1_000_000, volume: "shared");
        bed.Probe.Set(bed.Paths.LiveMedia, 1_099, 1_000_000, volume: "shared");
        Action over = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, transferRoot: null, sizing);
        over.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.StorageExhausted,
                "each candidate alone fits, but together they do not");
    }

    [Fact]
    public void CandidateAdmission_AppliesTheGlobalMarginPerVolumeExactlyOnce()
    {
        using var bed = new RecoveryTestBed();
        bed.Options.DiskSafetyMarginBytes = 100;
        var capacity = new SelfHostedActivationCapacity(bed.Probe, Options.Create(bed.Options));
        var sizing = new SelfHostedActivationSizing(1_000, 0, 0, 0);

        bed.Probe.Set(bed.Paths.LiveDatabase, 1_099, 1_000_000);
        bed.Probe.Set(bed.Paths.LiveMedia, 0, 1_000_000);
        Action tooSmall = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, transferRoot: null, sizing);
        tooSmall.Should().Throw<MigrationActivationException>();

        bed.Probe.Set(bed.Paths.LiveDatabase, 1_100, 1_000_000);
        Action boundary = () => capacity.EnsureActivationFits(
            bed.Paths.LiveDatabase, bed.Paths.LiveMedia, transferRoot: null, sizing);
        boundary.Should().NotThrow();
    }

    [Fact]
    public void RetentionClaim_AddsOnlyWhatTheTransferReservationDoesNotAlreadyCover()
    {
        using var bed = new RecoveryTestBed();
        var capacity = new SelfHostedActivationCapacity(bed.Probe, Options.Create(bed.Options));
        var snapshot = new TransferCapacitySnapshot(
            PhysicalAvailableBytes: 1_000,
            PhysicalTotalBytes: 10_000,
            GlobalSafetyMarginBytes: 0,
            OutstandingReservedBytes: 800,
            ActiveReservationCount: 1,
            UsableAvailableBytes: 200,
            MeasuredAtUtc: bed.Clock.UtcNow);

        Action exact = () => capacity.EnsureRetentionClaimFits(snapshot, previousBytes: 1_000);
        exact.Should().NotThrow("only 200 bytes are added on top of the existing 800-byte claim");
        Action over = () => capacity.EnsureRetentionClaimFits(snapshot, previousBytes: 1_001);
        over.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.StorageExhausted);
        Action nothing = () => capacity.EnsureRetentionClaimFits(snapshot, previousBytes: 0);
        nothing.Should().NotThrow();
    }

    [Fact]
    public async Task EnsureAdmitted_ProjectsTheRetentionClaimThroughTheRealCapacityService()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        await using (var db = bed.OpenDatabase())
        {
            var capacity = new TransferStorageCapacity(db, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
            var reserved = await capacity.TryReserveAsync(10_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
            await capacity.ClaimAsync(reserved.ReservationId!.Value, bed.JobId, default);
        }

        var service = bed.CreateService();
        var sizing = new SelfHostedActivationSizing(1_000, 1_000, 6_000, 0);
        Func<Task> admitted = () => service.EnsureAdmittedAsync(sizing, transferRoot: null, default);
        await admitted.Should().NotThrowAsync();

        var impossible = new SelfHostedActivationSizing(1_000, 1_000, 2_000_000_000, 0);
        Func<Task> rejected = () => service.EnsureAdmittedAsync(impossible, transferRoot: null, default);
        await rejected.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.StorageExhausted);
    }

    [Fact]
    public void RealVolumeProbe_MeasuresTheVolumeContainingThePath()
    {
        using var bed = new RecoveryTestBed();
        var probe = new VolumeSpaceProbe();
        var total = probe.TotalSizeBytes(bed.Root);
        var available = probe.AvailableFreeSpaceBytes(bed.Root);
        total.Should().BeGreaterThan(0);
        available.Should().BeGreaterThanOrEqualTo(0).And.BeLessThanOrEqualTo(total);
        probe.TotalSizeBytes(Path.Combine(bed.Root, "does", "not", "exist")).Should().Be(total,
            "an absent path is measured through its nearest existing ancestor");
        probe.AreSameVolume(bed.Paths.LiveDatabase, bed.Paths.LiveMedia).Should().BeTrue(
            "both components live under the same disposable temp root");
    }

    [Fact]
    public async Task EnsureMaterializedAtLeast_is_an_absolute_idempotent_target_capped_at_the_reservation()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        await using var db = bed.OpenDatabase();
        var capacity = new TransferStorageCapacity(db, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
        var reserved = await capacity.TryReserveAsync(1_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        var reservationId = reserved.ReservationId!.Value;
        await capacity.ClaimAsync(reservationId, bed.JobId, default);
        await capacity.AddMaterializedBytesAsync(reservationId, 200, default);

        await capacity.EnsureMaterializedAtLeastAsync(reservationId, 400, default);
        await capacity.EnsureMaterializedAtLeastAsync(reservationId, 400, default);
        await capacity.EnsureMaterializedAtLeastAsync(reservationId, 250, default);
        await capacity.EnsureMaterializedAtLeastAsync(reservationId, 5_000, default);
        var row = await db.MigrationStorageReservations.AsNoTracking().SingleAsync(r => r.Id == reservationId);
        row.MaterializedBytes.Should().Be(1_000, "the absolute target is capped at the reserved amount and never regresses");

        var missing = () => capacity.EnsureMaterializedAtLeastAsync(Guid.NewGuid(), 1, default);
        (await missing.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.NotFound);

        await capacity.ReleaseAsync(reservationId, default);
        var released = () => capacity.EnsureMaterializedAtLeastAsync(reservationId, 1, default);
        (await released.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.Released);
    }

    [Fact]
    public async Task FullLifecycle_NoDoubleCharge_KeepsCountingAfterTransferRelease_AndCleanupReleases()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var probeService = bed.CreateService();
        var capture = await probeService.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var previousBytes = capture.DatabaseBytes + capture.MediaBytes;

        Guid transferReservationId;
        await using (var seed = bed.OpenDatabase())
        {
            var capacity = new TransferStorageCapacity(seed, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
            var reserved = await capacity.TryReserveAsync(previousBytes + 1_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
            await capacity.ClaimAsync(reserved.ReservationId!.Value, bed.JobId, default);
            await capacity.AddMaterializedBytesAsync(reserved.ReservationId.Value, 500, default);
            transferReservationId = reserved.ReservationId.Value;
        }

        bed.CloneLiveDatabaseToCandidate();
        var service = bed.CreateService();
        var journal = bed.SeedJournal();
        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        await service.RetainMediaAsync(bed.JobId, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
        await service.RetainDatabaseAsync(bed.JobId, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousDatabaseRetained, lease);
        Directory.Move(bed.Paths.CandidateMedia(bed.JobId), bed.Paths.LiveMedia);
        File.Move(bed.Paths.CandidateDatabase(bed.JobId), bed.Paths.LiveDatabase);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CandidateMediaActivated, lease);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CandidateDatabaseActivated, lease);
        var manifest = await service.FinalizeRetentionAsync(bed.JobId, transferReservationId, lease, default);

        await using var verify = bed.OpenDatabase();
        var capacityService = new TransferStorageCapacity(verify, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
        var transferOutstandingBefore = (previousBytes + 1_000) - 500;
        (await capacityService.GetSnapshotAsync(default)).OutstandingReservedBytes
            .Should().Be(Math.Max(transferOutstandingBefore, manifest.TotalBytes),
                "the retained copy is offset against the unmaterialized transfer claim, never added to it");

        await capacityService.ReleaseAsync(transferReservationId, default);
        (await capacityService.GetSnapshotAsync(default)).OutstandingReservedBytes
            .Should().Be(manifest.TotalBytes, "the claimed retention outlives the transfer reservation");

        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PostActivationVerified, lease);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.Committed, lease);
        bed.Journals.MarkResolved(journal.JobId, lease);
        await lease.DisposeAsync();

        bed.Clock.Advance(TimeSpan.FromDays(MigrationContractLimits.RecoveryRetentionDays + 1));
        (await service.DeleteExpiredAsync(default)).Should().Be(1);
        Directory.Exists(bed.Paths.PreviousMedia(bed.JobId)).Should().BeFalse();
        File.Exists(bed.Paths.PreviousDatabase(bed.JobId)).Should().BeFalse();
        (await capacityService.GetSnapshotAsync(default)).OutstandingReservedBytes.Should().Be(0,
            "cleanup releases the retention reservation only after removing the material");
    }
}

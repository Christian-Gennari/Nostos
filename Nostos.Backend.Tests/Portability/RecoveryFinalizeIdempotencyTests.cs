using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 6 rework: the transfer-to-retention capacity offset is one durable
/// absolute target, so every retry and every crash point converges on the same
/// charge, exactly like a single clean finalization.
/// </summary>
public sealed class RecoveryFinalizeIdempotencyTests
{
    private sealed class SimulatedCrash : Exception;

    private sealed class Scenario : IAsyncDisposable
    {
        internal required RecoveryTestBed Bed { get; init; }
        internal required SelfHostedMigrationRecoveryService Service { get; init; }
        internal required IAsyncDisposable Lease { get; init; }
        internal required Guid TransferReservationId { get; init; }
        internal required long RetainedBytes { get; init; }
        internal required long TransferReserved { get; init; }
        internal required long TransferMaterialized { get; init; }

        internal long ExpectedOutstanding => Math.Max(TransferReserved - TransferMaterialized, RetainedBytes);

        public async ValueTask DisposeAsync()
        {
            await Lease.DisposeAsync();
            Bed.Dispose();
        }
    }

    private static async Task<Scenario> BuildAsync()
    {
        var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var capture = await bed.CreateService().CaptureAsync(
            bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var retainedBytes = capture.DatabaseBytes + capture.MediaBytes;
        const long materialized = 500;
        var reserve = retainedBytes + 1_000;

        Guid transferReservationId;
        await using (var seed = bed.OpenDatabase())
        {
            var capacity = new TransferStorageCapacity(seed, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
            var reserved = await capacity.TryReserveAsync(
                reserve, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
            await capacity.ClaimAsync(reserved.ReservationId!.Value, bed.JobId, default);
            await capacity.AddMaterializedBytesAsync(reserved.ReservationId.Value, materialized, default);
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

        return new Scenario
        {
            Bed = bed,
            Service = service,
            Lease = lease,
            TransferReservationId = transferReservationId,
            RetainedBytes = retainedBytes,
            TransferReserved = reserve,
            TransferMaterialized = materialized,
        };
    }

    [Fact]
    public async Task Finalize_twice_is_a_capacity_and_manifest_no_op()
    {
        await using var scenario = await BuildAsync();
        var first = await scenario.Service.FinalizeRetentionAsync(
            scenario.Bed.JobId, scenario.TransferReservationId, scenario.Lease, default);
        await using var db = scenario.Bed.OpenDatabase();
        var capacity = new TransferStorageCapacity(
            db, scenario.Bed.TransferVolume, Options.Create(scenario.Bed.Options), scenario.Bed.Clock);
        var afterFirst = await capacity.GetSnapshotAsync(default);

        var second = await scenario.Service.FinalizeRetentionAsync(
            scenario.Bed.JobId, scenario.TransferReservationId, scenario.Lease, default);
        var third = await scenario.Service.FinalizeRetentionAsync(
            scenario.Bed.JobId, scenario.TransferReservationId, scenario.Lease, default);

        second.Should().BeEquivalentTo(first);
        third.Should().BeEquivalentTo(first);
        (await capacity.GetSnapshotAsync(default)).OutstandingReservedBytes
            .Should().Be(afterFirst.OutstandingReservedBytes);
        (await db.MigrationStorageReservations.AsNoTracking()
                .Where(r => r.Purpose == (int)MigrationSessionPurpose.RecoveryRetention)
                .ToArrayAsync())
            .Should().HaveCount(1);
    }

    [Theory]
    [InlineData((int)SelfHostedRecoveryFinalizeStep.BeforeTopUp)]
    [InlineData((int)SelfHostedRecoveryFinalizeStep.AfterTopUp)]
    [InlineData((int)SelfHostedRecoveryFinalizeStep.AfterReservationRecorded)]
    [InlineData((int)SelfHostedRecoveryFinalizeStep.AfterClaim)]
    [InlineData((int)SelfHostedRecoveryFinalizeStep.BeforeAvailable)]
    public async Task Finalize_retry_after_a_crash_at_any_step_matches_the_single_clean_charge(
        int crashAtValue)
    {
        var crashAt = (SelfHostedRecoveryFinalizeStep)crashAtValue;
        await using var scenario = await BuildAsync();
        scenario.Service.FinalizeStepForTesting = step =>
        {
            if (step == crashAt) throw new SimulatedCrash();
        };

        Func<Task> crashed = () => scenario.Service.FinalizeRetentionAsync(
            scenario.Bed.JobId, scenario.TransferReservationId, scenario.Lease, default);
        await crashed.Should().ThrowAsync<SimulatedCrash>();

        scenario.Service.FinalizeStepForTesting = null;
        var manifest = await scenario.Service.FinalizeRetentionAsync(
            scenario.Bed.JobId, scenario.TransferReservationId, scenario.Lease, default);

        manifest.Status.Should().Be(MigrationRecoveryStatus.Available);
        await using var db = scenario.Bed.OpenDatabase();
        var capacity = new TransferStorageCapacity(
            db, scenario.Bed.TransferVolume, Options.Create(scenario.Bed.Options), scenario.Bed.Clock);
        var snapshot = await capacity.GetSnapshotAsync(default);
        snapshot.OutstandingReservedBytes.Should().Be(scenario.ExpectedOutstanding,
            "every crash point converges on max(transfer outstanding, retained bytes)");
        snapshot.OutstandingReservedBytes.Should().BeGreaterOrEqualTo(manifest.TotalBytes,
            "accounting must never fall below the bytes physically retained");

        var retention = await db.MigrationStorageReservations.AsNoTracking()
            .Where(r => r.Purpose == (int)MigrationSessionPurpose.RecoveryRetention)
            .ToArrayAsync();
        retention.Should().HaveCount(1, "a retry must reuse the deterministic retention claim");
        retention[0].ClaimedJobId.Should().Be(scenario.Bed.JobId);
        retention[0].ReleasedAtUtc.Should().BeNull();

        var transfer = await db.MigrationStorageReservations.AsNoTracking()
            .SingleAsync(r => r.Id == scenario.TransferReservationId);
        transfer.MaterializedBytes.Should().Be(Math.Min(scenario.TransferReserved, scenario.TransferMaterialized + scenario.RetainedBytes));
    }

    [Fact]
    public async Task Finalize_never_offsets_another_jobs_transfer_reservation()
    {
        await using var scenario = await BuildAsync();
        var otherJobId = Guid.NewGuid();
        const long otherBytes = 10_000;
        Guid otherReservationId;
        await using (var seed = scenario.Bed.OpenDatabase())
        {
            var capacity = new TransferStorageCapacity(
                seed, scenario.Bed.TransferVolume, Options.Create(scenario.Bed.Options), scenario.Bed.Clock);
            var other = await capacity.TryReserveAsync(otherBytes, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
            await capacity.ClaimAsync(other.ReservationId!.Value, otherJobId, default);
            otherReservationId = other.ReservationId.Value;
        }

        var manifest = await scenario.Service.FinalizeRetentionAsync(
            scenario.Bed.JobId, otherReservationId, scenario.Lease, default);

        manifest.TransferTopUpSettled.Should().BeTrue();
        manifest.TransferTopUpReservationId.Should().BeNull("another job's claim must never be offset");
        await using var db = scenario.Bed.OpenDatabase();
        var otherRow = await db.MigrationStorageReservations.AsNoTracking().SingleAsync(r => r.Id == otherReservationId);
        otherRow.MaterializedBytes.Should().Be(0);
        var capacityService = new TransferStorageCapacity(
            db, scenario.Bed.TransferVolume, Options.Create(scenario.Bed.Options), scenario.Bed.Clock);
        var snapshot = await capacityService.GetSnapshotAsync(default);
        snapshot.OutstandingReservedBytes.Should().Be(
            (scenario.TransferReserved - scenario.TransferMaterialized) + scenario.RetainedBytes + otherBytes,
            "the untouched transfer claim, the other job's claim and the retention claim all remain charged");
    }
}

using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 9 failure handling: a restore whose in-process rollback itself fails
/// keeps the host fail-closed until a restart's reconciler restores the
/// pre-restore generation, and a busy exclusive gate leaves the durable claim
/// pending so the runner can finish it later.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class RecoveryRestoreFailureTests
{
    [Fact]
    public async Task RollbackFailure_KeepsAdmissionClosed_UntilRestartReconciles()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        await bed.WriteHostRecordAsync();
        var preRestore = bed.SnapshotLiveGeneration();

        bed.MediaRecoveryRenameFailure = () =>
            throw new InvalidOperationException("injected restore media rollback failure");

        var result = await bed.RestoreAsync(observer: reached =>
        {
            if (string.Equals(reached, SelfHostedRecoveryRestoreSteps.AfterReopen, StringComparison.Ordinal))
            {
                // Force the post-activation verification to fail so the restore
                // attempts its in-process rollback.
                ActivationCoordinatorTestBed.CorruptFirstFile(bed.Paths.LiveMedia);
            }
        });

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Interrupted);
        result.ErrorCode.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);

        // The mixed/partial state must never reopen admission.
        bed.Maintenance.IsRecoveryRequired.Should().BeTrue();
        bed.Maintenance.IsMaintenanceActive.Should().BeTrue();
        bed.Maintenance.TryEnterOperation().Should().BeNull(
            "admission must stay closed until startup reconciliation succeeds");
        var manifest = bed.ReadRecoveryManifest()!;
        var restoreId = manifest.RestoreOperationId!.Value;
        bed.Journals.Read(restoreId)!.Phase.Should().Be(SelfHostedActivationPhase.RollingBack);
        File.Exists(new LibraryMaintenanceMarker(bed.Paths.LiveDatabase).MarkerPath).Should().BeTrue();
        manifest.Status.Should().Be(MigrationRecoveryStatus.Restoring);
        manifest.RestoreError.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);

        var frozen = bed.SnapshotActivationTree();
        await Task.Yield();
        bed.SnapshotActivationTree().Should().BeEquivalentTo(
            frozen, "no further file mutation may occur while closed");

        // A fresh host reconciles the partial rollback back to the pre-restore library.
        bed.MediaRecoveryRenameFailure = null;
        await bed.RecoverHostAsync();
        bed.AssertGenerationEquals(preRestore, "restart reconciliation must restore the pre-restore library");
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse();
        var probe = bed.Maintenance.TryEnterOperation();
        probe.Should().NotBeNull();
        probe!.Dispose();

        // The durable claim is still resumable and completes the restore.
        var resumed = await bed.ResumeRestoreAsync();
        resumed.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        await bed.AssertRestoredPortableGenerationAsync();
    }

    [Fact]
    public async Task BusyExclusiveGate_LeavesTheClaimPending_AndTheRunnerFinishesLater()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        var before = bed.SnapshotLiveGeneration();

        await bed.RequestRestoreOnlyAsync();

        await using (await bed.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            var busy = await bed.ResumeRestoreAsync();
            busy.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Busy);
            busy.ErrorCode.Should().Be(MigrationActivationErrorCodes.Busy);
        }

        bed.AssertGenerationEquals(before, "a busy gate must not touch the library");
        bed.ReadRecoveryManifest()!.Status.Should().Be(
            MigrationRecoveryStatus.Restoring, "the accepted restore stays claimed for the next scan");

        var resumed = await bed.ResumeRestoreAsync();
        resumed.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        await bed.AssertRestoredPortableGenerationAsync();
    }
}

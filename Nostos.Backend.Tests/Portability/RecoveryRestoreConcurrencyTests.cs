using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 9 concurrency: two restore requests claim one restore, and a restore
/// that arrives while an exclusive library operation (for example another
/// job's activation) owns the host is answered busy without touching anything;
/// the claim then succeeds once the owner leaves.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class RecoveryRestoreConcurrencyTests
{
    [Fact]
    public async Task TwoRestoreRequests_ClaimExactlyOneRestore()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        var beforeRevision = await bed.CurrentRevisionAsync();
        var token = await bed.CurrentRevisionTokenAsync();

        var dispatcher = bed.Host.GetRequiredService<SelfHostedActivationDispatcher>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeforeRestoreRunForTesting = (_, ct) => release.Task.WaitAsync(ct);

        await using var scope = bed.Host.CreateAsyncScope();
        var host = scope.ServiceProvider.GetRequiredService<ISelfHostedRecoveryRestore>();

        var first = await host.RequestRestoreAsync(
            bed.RecoveryId, new MigrationRecoveryRestoreRequest(token, true), default);
        first.Outcome.Should().Be(MigrationActivationOutcome.Accepted);
        first.Accepted.Should().BeTrue();

        // The second request replays the accepted run; it never starts another.
        var second = await host.RequestRestoreAsync(
            bed.RecoveryId, new MigrationRecoveryRestoreRequest(token, true), default);
        second.Outcome.Should().Be(MigrationActivationOutcome.Accepted);
        second.RecoveryId.Should().Be(bed.RecoveryId);

        release.SetResult();
        await dispatcher.AwaitRestoreFinishedAsync(bed.RecoveryId, default);
        dispatcher.StartedRestoreRunCount.Should().Be(1, "a duplicate request never starts a second run");

        (await bed.CurrentRevisionAsync()).Should().Be(
            beforeRevision + 1, "the library revision is advanced exactly once");
        await bed.AssertRestoredPortableGenerationAsync();
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Restored);
    }

    [Fact]
    public async Task AGatedActivationQueuesARestore_AndTheStaleRestoreIsRefused()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        var token = await bed.CurrentRevisionTokenAsync();
        var job = await bed.SeedReadyToActivateJobAsync(token);
        var dispatcher = bed.Host.GetRequiredService<SelfHostedActivationDispatcher>();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeforeRunForTesting = (_, ct) => gate.Task.WaitAsync(ct);

        try
        {
            var activation = await bed.Host.GetRequiredService<IMigrationActivationDispatcher>()
                .RequestAsync(job, new MigrationActivateRequest(token, true), default);
            activation.Outcome.Should().Be(MigrationActivationRequestOutcome.Accepted);

            var restore = await bed.Host.GetRequiredService<ISelfHostedRecoveryRestore>()
                .RequestRestoreAsync(
                    bed.RecoveryId, new MigrationRecoveryRestoreRequest(token, true), default);
            restore.Outcome.Should().Be(MigrationActivationOutcome.Accepted);
            restore.Accepted.Should().BeTrue();

            // One library-switch pump: the restore waits behind the gated
            // activation instead of running concurrently.
            dispatcher.StartedRunCount.Should().Be(0);
            dispatcher.StartedRestoreRunCount.Should().Be(0);

            gate.SetResult();
            await dispatcher.AwaitActivationFinishedAsync(job, default);
            await dispatcher.AwaitRestoreFinishedAsync(bed.RecoveryId, default);

            dispatcher.StartedRunCount.Should().Be(1);
            dispatcher.StartedRestoreRunCount.Should().Be(1);

            // The activation committed first; the restore's confirmation is now
            // stale, so it refuses to overwrite the new library.
            (await dispatcher.GetStatusAsync(job, default)).Outcome
                .Should().Be(MigrationActivationOutcome.Completed);
            var restoreStatus = await dispatcher.GetRestoreStatusAsync(bed.RecoveryId, default);
            restoreStatus.Outcome.Should().Be(MigrationActivationOutcome.Failed);
            restoreStatus.ErrorCode.Should().Be(MigrationActivationErrorCodes.DestinationConflict);
            restoreStatus.CanRestore.Should().BeTrue();
            bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available);
            await bed.AssertImportedGenerationAsync();
        }
        finally
        {
            gate.TrySetResult();
            dispatcher.BeforeRunForTesting = null;
        }
    }

    [Fact]
    public async Task AGatedRestoreQueuesAnActivation_AndTheStaleActivationIsRefused()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        var token = await bed.CurrentRevisionTokenAsync();
        var job = await bed.SeedReadyToActivateJobAsync(token);
        var dispatcher = bed.Host.GetRequiredService<SelfHostedActivationDispatcher>();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeforeRestoreRunForTesting = (_, ct) => gate.Task.WaitAsync(ct);

        try
        {
            var restore = await bed.Host.GetRequiredService<ISelfHostedRecoveryRestore>()
                .RequestRestoreAsync(
                    bed.RecoveryId, new MigrationRecoveryRestoreRequest(token, true), default);
            restore.Outcome.Should().Be(MigrationActivationOutcome.Accepted);

            var activation = await bed.Host.GetRequiredService<IMigrationActivationDispatcher>()
                .RequestAsync(job, new MigrationActivateRequest(token, true), default);
            activation.Outcome.Should().Be(MigrationActivationRequestOutcome.Accepted);

            dispatcher.StartedRestoreRunCount.Should().Be(0);
            dispatcher.StartedRunCount.Should().Be(0);

            gate.SetResult();
            await dispatcher.AwaitRestoreFinishedAsync(bed.RecoveryId, default);
            await dispatcher.AwaitActivationFinishedAsync(job, default);

            dispatcher.StartedRestoreRunCount.Should().Be(1);
            dispatcher.StartedRunCount.Should().Be(1);

            // The restore committed first; the activation's stored revision is
            // stale and the job stays retryable.
            await bed.AssertRestoredPortableGenerationAsync();
            var jobStatus = await dispatcher.GetStatusAsync(job, default);
            jobStatus.Outcome.Should().Be(MigrationActivationOutcome.Failed);
            jobStatus.CanActivate.Should().BeTrue();
            jobStatus.ErrorCode.Should().Be(MigrationActivationErrorCodes.DestinationConflict);
        }
        finally
        {
            gate.TrySetResult();
            dispatcher.BeforeRestoreRunForTesting = null;
        }
    }

    [Fact]
    public async Task RestoreWhileAnExclusiveOperationOwnsTheLibrary_IsBusy_AndNothingChanges()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        var before = bed.SnapshotLiveGeneration();

        await using (await bed.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            var failure = await FluentActions
                .Awaiting(() => bed.RequestRestoreOnlyAsync())
                .Should().ThrowAsync<MigrationActivationException>();
            failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Busy);
        }

        bed.AssertGenerationEquals(before, "a busy restore must not touch the library");
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available,
            "a busy attempt leaves the copy claimable");

        // Once the exclusive owner leaves, the same request succeeds.
        (await bed.RestoreAsync()).Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        await bed.AssertRestoredPortableGenerationAsync();
    }

    [Fact]
    public async Task ACorruptManifest_DoesNotBlockTheResumeScanForOtherCopies()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        bed.CreateCorruptRecoveryCopy();
        await bed.RequestRestoreOnlyAsync();

        var dispatcher = bed.Host.GetRequiredService<SelfHostedActivationDispatcher>();
        await dispatcher.ScanPendingRestoresAsync(default);
        await dispatcher.AwaitRestoreFinishedAsync(bed.RecoveryId, default);

        await bed.AssertRestoredPortableGenerationAsync();
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Restored);
    }

    [Fact]
    public async Task CleanupDecision_AfterAClaim_CannotMarkTheCopy()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        var available = bed.ReadRecoveryManifest()!;

        await bed.RequestRestoreOnlyAsync();

        bed.Manifests.TryCreateDeletionMarker(bed.RecoveryId, available).Should().BeFalse(
            "a claimed copy can no longer be marked for deletion");
        bed.Manifests.HasDeletionMarker(bed.RecoveryId).Should().BeFalse();
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Restoring);

        (await bed.ResumeRestoreAsync()).Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        await bed.AssertRestoredPortableGenerationAsync();
    }

    [Fact]
    public async Task Claim_AfterACleanupDecision_IsRefused()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        var before = bed.SnapshotLiveGeneration();
        bed.Manifests.CreateDeletionMarker(bed.RecoveryId);

        var failure = await FluentActions
            .Awaiting(() => bed.RequestRestoreOnlyAsync())
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryNotFound);

        bed.AssertGenerationEquals(before, "a pruned copy must never be claimed");
        File.Exists(bed.Paths.PreviousDatabase(bed.RecoveryId)).Should().BeTrue(
            "the cleanup decision alone must not touch the copy");
        Directory.Exists(bed.Paths.PreviousMedia(bed.RecoveryId)).Should().BeTrue();
    }

    [Fact]
    public async Task ConditionalClaimWrite_LosesWhenTheManifestChanged()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        var available = bed.ReadRecoveryManifest()!;
        await bed.RequestRestoreOnlyAsync();
        var claiming = bed.ReadRecoveryManifest()!;

        bed.Manifests.TryWriteIfUnchanged(
                bed.RecoveryId, available, available with { RestoreError = "stale" })
            .Should().BeNull("the old Available manifest is no longer the current one");
        bed.Manifests.TryWriteIfUnchanged(
                bed.RecoveryId, claiming, claiming with { RestoreError = "current" })
            .Should().NotBeNull();
    }
}

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

        await using var scope = bed.Host.CreateAsyncScope();
        var host = scope.ServiceProvider.GetRequiredService<ISelfHostedRecoveryRestore>();

        var first = await host.RequestRestoreAsync(
            bed.RecoveryId, new MigrationRecoveryRestoreRequest(token, true), default);
        first.Status.Should().Be(MigrationRecoveryStatus.Restoring);

        var failure = await FluentActions
            .Awaiting(() => host.RequestRestoreAsync(
                bed.RecoveryId, new MigrationRecoveryRestoreRequest(token, true), default))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryRestoreConflict);

        var runner = bed.Host.GetRequiredService<SelfHostedRecoveryRestoreRunner>();
        if (runner.RunningTask(bed.RecoveryId) is { } running) await running;

        (await bed.CurrentRevisionAsync()).Should().Be(
            beforeRevision + 1, "the library revision is advanced exactly once");
        await bed.AssertRestoredPortableGenerationAsync();
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Restored);
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
}

using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 7 concurrency: Phase A runs beside live readers and writers, the
/// exclusive window refuses new work, and two concurrent activation requests
/// produce exactly one cutover.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorConcurrencyTests
{
    [Fact]
    public async Task OpenReaderDuringPhaseA_IsUnaffected_AndTheWindowWaitsForIt()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = await bed.Maintenance.EnterOperationAsync(default);

        var activation = bed.ActivateAsync(confirm: true, observer: step =>
        {
            if (string.Equals(step, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
            {
                reached.TrySetResult();
            }
        });

        // Phase A finished with a live reader still holding a shared lease.
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        activation.IsCompleted.Should().BeFalse(
            "the exclusive window must wait for the existing reader to drain");

        await reader.DisposeAsync();

        var result = await activation;
        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse("the lease is released on the success path");
    }

    [Fact]
    public async Task TwoConcurrentActivationRequests_ProduceExactlyOneCutover()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var results = await Task.WhenAll(
            bed.ActivateAsync(confirm: true),
            bed.ActivateAsync(confirm: true));

        results.Count(result => result.Outcome == SelfHostedActivationOutcome.Completed)
            .Should().Be(1, "only one request may perform the cutover");
        results.Count(result => result.Outcome is SelfHostedActivationOutcome.InProgress
            or SelfHostedActivationOutcome.AlreadyCompleted).Should().Be(1);

        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision,
            "the library revision is advanced exactly once");
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Completed);
        await bed.AssertImportedGenerationAsync();

        // A later request is an idempotent success with no second cutover.
        (await bed.ActivateAsync(confirm: true)).Outcome
            .Should().Be(SelfHostedActivationOutcome.AlreadyCompleted);
        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision);
    }
}

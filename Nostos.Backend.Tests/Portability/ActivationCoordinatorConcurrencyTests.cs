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

    /// <summary>
    /// The swap window deliberately has no database at the live path. A second
    /// activation request reaching the coordinator inside that window must not
    /// open SQLite there: doing so materializes an empty database, the owner's
    /// candidate rename then finds the destination occupied, and a load-timed
    /// parallel request turns a successful cutover into a generic failure.
    /// This is the deterministic reproduction of the CI flake.
    /// </summary>
    [Fact]
    public async Task SecondRequestInsideTheSwapWindow_DoesNotCreateAnEmptyLiveDatabase()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var atRetainedDatabase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The observer blocks a pool thread at the boundary, so run the first
        // activation away from the test's synchronization context.
        var first = Task.Run(() => bed.ActivateAsync(confirm: true, observer: step =>
        {
            if (string.Equals(step, SelfHostedActivationSteps.PhasePreviousDatabaseRetained, StringComparison.Ordinal))
            {
                atRetainedDatabase.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        }));

        await atRetainedDatabase.Task.WaitAsync(TimeSpan.FromSeconds(30));
        File.Exists(bed.Paths.LiveDatabase).Should().BeFalse(
            "the swap window deliberately vacates the live database path");

        var second = await bed.ActivateAsync(confirm: true);
        second.Outcome.Should().Be(SelfHostedActivationOutcome.InProgress,
            "the durable lease belongs to the in-flight cutover");
        File.Exists(bed.Paths.LiveDatabase).Should().BeFalse(
            "a concurrent request must not materialize an empty database at the vacated live path");

        release.TrySetResult();
        (await first).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
    }
}

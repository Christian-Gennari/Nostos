using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableArchiveResourceBudgetTests
{
    [Fact]
    public async Task Budget_tracks_rents_returns_and_high_water()
    {
        var budget = new PortableArchiveBufferBudget(4 * 1024 * 1024);
        var first = await budget.RentAsync(1024 * 1024, CancellationToken.None);
        var second = await budget.RentAsync(2 * 1024 * 1024, CancellationToken.None);

        budget.CurrentBytes.Should().Be(3 * 1024 * 1024);
        budget.HighWaterBytes.Should().Be(3 * 1024 * 1024);
        first.Length.Should().Be(1024 * 1024);
        second.Length.Should().Be(2 * 1024 * 1024);

        first.Dispose();
        first.Dispose();
        budget.CurrentBytes.Should().Be(2 * 1024 * 1024);
        (await budget.RentAsync(1024 * 1024, CancellationToken.None)).Dispose();
        budget.HighWaterBytes.Should().Be(3 * 1024 * 1024);

        await second.DisposeAsync();
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Budget_fails_closed_at_the_64_mib_limit()
    {
        var budget = new PortableArchiveBufferBudget(PortableArchiveLimits.MaxExplicitBufferBytes);
        var fullBudgetLease = await budget.RentAsync(
            PortableArchiveLimits.MaxExplicitBufferBytes,
            CancellationToken.None);
        budget.CurrentBytes.Should().Be(PortableArchiveLimits.MaxExplicitBufferBytes);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => budget.RentAsync(1, CancellationToken.None).AsTask());

        exception.Message.Should().Contain("budget");
        budget.CurrentBytes.Should().Be(PortableArchiveLimits.MaxExplicitBufferBytes);
        budget.HighWaterBytes.Should().Be(PortableArchiveLimits.MaxExplicitBufferBytes);
        fullBudgetLease.Dispose();
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Budget_rejects_limits_above_64_mib_and_honors_cancellation()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new PortableArchiveBufferBudget((long)PortableArchiveLimits.MaxExplicitBufferBytes + 1));
        exception.ParamName.Should().Be("maxBytes");

        var budget = new PortableArchiveBufferBudget(1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => budget.RentAsync(16, cancellation.Token).AsTask());
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_rents_never_exceed_the_budget_high_water()
    {
        const int leaseBytes = 4 * 1024 * 1024;
        const int renterCount = 16;
        var budget = new PortableArchiveBufferBudget(PortableArchiveLimits.MaxExplicitBufferBytes);
        var allRentsAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRents = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiredCount = 0;

        var renters = Enumerable.Range(0, renterCount).Select(async _ =>
        {
            await using var lease = await budget.RentAsync(leaseBytes, CancellationToken.None);
            if (Interlocked.Increment(ref acquiredCount) == renterCount)
                allRentsAcquired.TrySetResult();
            await releaseRents.Task;
        }).ToArray();

        await allRentsAcquired.Task;
        budget.CurrentBytes.Should().Be(PortableArchiveLimits.MaxExplicitBufferBytes);
        budget.HighWaterBytes.Should().Be(PortableArchiveLimits.MaxExplicitBufferBytes);
        releaseRents.TrySetResult();
        await Task.WhenAll(renters);

        budget.CurrentBytes.Should().Be(0);
        budget.HighWaterBytes.Should().BeLessThanOrEqualTo(PortableArchiveLimits.MaxExplicitBufferBytes);
        budget.HighWaterBytes.Should().Be(PortableArchiveLimits.MaxExplicitBufferBytes);
    }
}

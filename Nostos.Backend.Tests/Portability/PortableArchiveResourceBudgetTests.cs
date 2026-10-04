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

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task Budget_rejects_invalid_rent_sizes_without_overflow(int bytes)
    {
        var budget = new PortableArchiveBufferBudget(1024);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => budget.RentAsync(bytes, CancellationToken.None).AsTask());
        budget.CurrentBytes.Should().Be(0);
        budget.HighWaterBytes.Should().Be(0);
    }

    [Fact]
    public async Task Budget_rejects_a_request_larger_than_the_whole_budget()
    {
        var budget = new PortableArchiveBufferBudget(1024);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => budget.RentAsync(1025, CancellationToken.None).AsTask());
        budget.CurrentBytes.Should().Be(0);
        budget.HighWaterBytes.Should().Be(0);
    }

    [Fact]
    public async Task Lease_concurrent_sync_and_async_disposal_returns_bytes_once()
    {
        var budget = new PortableArchiveBufferBudget(32);
        using var otherLease = await budget.RentAsync(16, CancellationToken.None);
        var lease = await budget.RentAsync(16, CancellationToken.None);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposers = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            await start.Task;
            if (index % 2 == 0)
                lease.Dispose();
            else
                await lease.DisposeAsync();
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(disposers);

        budget.CurrentBytes.Should().Be(16);
        using (await budget.RentAsync(16, CancellationToken.None))
            budget.CurrentBytes.Should().Be(32);
        otherLease.Dispose();
        budget.CurrentBytes.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_over_cap_rents_fail_without_exceeding_64_mib()
    {
        const int leaseBytes = 4 * 1024 * 1024;
        const int renterCount = 17;
        var budget = new PortableArchiveBufferBudget(PortableArchiveLimits.MaxExplicitBufferBytes);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptedCount = 0;
        var acquiredCount = 0;
        var rejectedCount = 0;
        var renters = Enumerable.Range(0, renterCount).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            PortableBufferLease? lease = null;
            try
            {
                try
                {
                    lease = await budget.RentAsync(leaseBytes, CancellationToken.None);
                    Interlocked.Increment(ref acquiredCount);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref rejectedCount);
                }

                budget.CurrentBytes.Should().BeLessThanOrEqualTo(PortableArchiveLimits.MaxExplicitBufferBytes);
            }
            finally
            {
                if (Interlocked.Increment(ref attemptedCount) == renterCount)
                    allAttempted.TrySetResult();
                await release.Task;
                lease?.Dispose();
            }
        })).ToArray();

        start.SetResult();
        try
        {
            await allAttempted.Task;
            acquiredCount.Should().Be(16);
            rejectedCount.Should().Be(1);
            budget.CurrentBytes.Should().Be(PortableArchiveLimits.MaxExplicitBufferBytes);
            budget.HighWaterBytes.Should().BeLessThanOrEqualTo(PortableArchiveLimits.MaxExplicitBufferBytes);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(renters);
        }

        budget.CurrentBytes.Should().Be(0);
    }

}

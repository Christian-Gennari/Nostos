using System.Diagnostics;
using FluentAssertions;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class MigrationWorkerWakeupTests
{
    [Fact]
    public async Task A_signal_wakes_the_idle_worker_at_once()
    {
        var wakeup = new MigrationWorkerWakeup();
        var stopwatch = Stopwatch.StartNew();
        var waiting = wakeup.WaitAsync(TimeSpan.FromMinutes(5), TimeProvider.System, CancellationToken.None);

        wakeup.Signal();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_signal_given_while_busy_is_kept_for_the_next_wait()
    {
        var wakeup = new MigrationWorkerWakeup();
        wakeup.Signal();
        wakeup.Signal();

        await wakeup.WaitAsync(TimeSpan.FromMinutes(5), TimeProvider.System, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // Signals coalesce: the second wait sleeps until its timeout.
        var second = wakeup.WaitAsync(TimeSpan.FromMilliseconds(200), TimeProvider.System, CancellationToken.None);
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Workers;
using Xunit;

namespace Nostos.Backend.Tests.Workers;

// Hosted-worker tests for the topic cleanup loop (#1621). The timer runs on a
// clock the test advances, so the interval, cancellation, error recovery and
// timer disposal are all deterministic: no wall-clock sleep and no waiting an
// hour for a tick.
public sealed class TopicCleanupWorkerTests : IDisposable
{
    private readonly List<string> _databasePaths = [];

    [Fact]
    public async Task Timer_fires_on_its_interval_and_is_disposed_on_shutdown()
    {
        var path = NewDatabasePath();
        await using (var db = CreateContext(path))
        {
            db.Topics.Add(new TopicModel { Topic = "orphaned-topic" });
            await db.SaveChangesAsync();
        }

        using var provider = BuildProvider(path);
        var clock = new FakeWorkerClock();
        var logger = new CapturingLogger<TopicCleanupWorker>();
        var worker = new TestWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), logger, clock);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        await clock.WaitForTimerCountAsync(1);

        clock.Advance(TimeSpan.FromMinutes(59));
        worker.AttemptedCycles.Should().Be(0, "no cleanup runs before the interval elapses");

        clock.Advance(TimeSpan.FromMinutes(1));
        await worker.WaitForCycleAsync(0);

        await using (var verify = CreateContext(path))
        {
            (await verify.Topics.CountAsync())
                .Should().Be(0, "the cycle cleaned up the topic with no note links");
        }

        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("Cleaned up 1 orphaned topic"));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        clock.LiveTimerCount.Should().Be(0, "the worker owns its PeriodicTimer and disposes it on shutdown");
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Cancellation_during_a_cycle_logs_no_error()
    {
        using var provider = BuildProvider(NewDatabasePath());
        var clock = new FakeWorkerClock();
        var logger = new CapturingLogger<TopicCleanupWorker>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new TestWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            clock,
            async stoppingToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, stoppingToken);
            });

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        await clock.WaitForTimerCountAsync(1);

        clock.Advance(TimeSpan.FromMinutes(60));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error,
            "host shutdown cancels the cycle; that is not a failure");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("shutting down"));
    }

    [Fact]
    public async Task Failed_cycle_is_logged_and_the_worker_keeps_running()
    {
        using var provider = BuildProvider(NewDatabasePath());
        var clock = new FakeWorkerClock();
        var logger = new CapturingLogger<TopicCleanupWorker>();
        var attempts = 0;
        var worker = new TestWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            clock,
            _ => Interlocked.Increment(ref attempts) == 1
                ? throw new InvalidOperationException("simulated cleanup failure")
                : Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        await clock.WaitForTimerCountAsync(1);

        clock.Advance(TimeSpan.FromMinutes(60));
        await worker.WaitForCycleAsync(0);

        clock.Advance(TimeSpan.FromMinutes(60));
        await worker.WaitForCycleAsync(1);

        worker.AttemptedCycles.Should().Be(2, "a failed cycle must not stop the loop");
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("Topic Cleanup cycle"));

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private string NewDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nostos-topic-worker-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        return path;
    }

    private static NostosDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        var context = new NostosDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static ServiceProvider BuildProvider(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new NostosDbContext(options));
        services.AddScoped<ITopicRepository, TopicRepository>();
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }

    // Drives the real loop on the test's clock; a cycle delegate replaces the
    // database work for the cancellation and error-recovery cases.
    private sealed class TestWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<TopicCleanupWorker> logger,
        FakeWorkerClock clock,
        Func<CancellationToken, Task>? cycle = null)
        : TopicCleanupWorker(scopeFactory, logger)
    {
        private readonly List<TaskCompletionSource> _cycles = [];
        private int _attempted;

        protected override TimeProvider Clock => clock;

        public int AttemptedCycles => Volatile.Read(ref _attempted);

        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);

        public Task WaitForCycleAsync(int index)
        {
            TaskCompletionSource completed;
            lock (_cycles)
            {
                while (_cycles.Count <= index)
                    _cycles.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                completed = _cycles[index];
            }

            return completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        protected override async Task RunCycleAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (cycle is null)
                    await base.RunCycleAsync(stoppingToken);
                else
                    await cycle(stoppingToken);
            }
            finally
            {
                var index = Interlocked.Increment(ref _attempted) - 1;
                lock (_cycles)
                {
                    while (_cycles.Count <= index)
                        _cycles.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    _cycles[index].TrySetResult();
                }
            }
        }
    }
}

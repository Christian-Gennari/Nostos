using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Services;

namespace Nostos.Backend.Workers;

public class TopicCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TopicCleanupWorker> logger,
    ILibraryMaintenanceCoordinator? maintenance = null
) : BackgroundService
{
    // Run every 1 hour. Virtual so tests can drive the real loop with a clock
    // they control instead of waiting for wall-clock time.
    protected virtual TimeSpan CleanupInterval => TimeSpan.FromHours(1);

    /// <summary>
    /// The clock the cleanup timer runs on. Overridden in tests so the interval
    /// fires deterministically and no test waits for an hour of real time.
    /// </summary>
    protected virtual TimeProvider Clock => TimeProvider.System;

    /// <summary>
    /// Runs the Topic Cleanup Worker loop until the app is stopped.
    /// </summary>
    /// <remarks>
    /// This method is called by the ASP.NET Core framework as part of the BackgroundService.
    /// It will run every hour until the app is stopped.
    /// If an exception occurs in the loop, it will be caught and logged, but the loop will continue.
    /// A cancellation caused by host shutdown ends the loop quietly (no error entry).
    /// If the timer itself crashes, it will be logged as critical.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Topic Cleanup Worker started.");

        try
        {
            // Owned by this method, so it is disposed on every exit path,
            // including host shutdown while a tick is pending.
            using var timer = new PeriodicTimer(CleanupInterval, Clock);

            // Loop until the app stops
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunCycleAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Host shutdown in the middle of a cycle is not a failure.
                    break;
                }
                catch (Exception ex)
                {
                    // 🔴 CRITICAL: Log the error, but swallow the exception
                    // so the loop continues next tick.
                    logger.LogError(ex, "An error occurred during the Topic Cleanup cycle.");
                }
            }

            if (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Topic Cleanup Worker received stop signal and is shutting down."
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Log graceful shutdown so you see it in the logs
            logger.LogInformation(
                "Topic Cleanup Worker received stop signal and is shutting down."
            );
        }
        catch (Exception ex)
        {
            // 🔴 Failsafe: If the timer itself crashes (rare), log it as critical.
            logger.LogCritical(ex, "Topic Cleanup Worker crashed fatally and will not restart.");
        }
    }

    /// <summary>
    /// One cleanup cycle. Virtual so tests can drive the loop's cancellation and
    /// error handling without a database.
    /// </summary>
    protected virtual Task RunCycleAsync(CancellationToken stoppingToken) =>
        DoWorkAsync(stoppingToken);

    /// <summary>
    /// Merges topic case variants still waiting under a pending key (a restore
    /// activated since startup can bring them; startup itself merges them in
    /// DatabaseBootstrapService), then cleans up orphaned topics (topics with
    /// no links to notes).
    /// </summary>
    private async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        await using var operation = maintenance is null ? null : await maintenance.EnterOperationAsync(stoppingToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var topicRepo = scope.ServiceProvider.GetRequiredService<ITopicRepository>();

        var mergedCount = await topicRepo.MergeCaseVariantsAsync(stoppingToken);
        if (mergedCount > 0)
        {
            logger.LogInformation("Merged {Count} topics that differed from another only in case.", mergedCount);
        }

        logger.LogInformation("Scanning for orphaned topics...");

        // Efficient Bulk Delete for topics with no links
        var deletedCount = await topicRepo.DeleteOrphanedAsync(stoppingToken);

        if (deletedCount > 0)
        {
            logger.LogInformation("Cleaned up {Count} orphaned topics.", deletedCount);
        }
    }
}

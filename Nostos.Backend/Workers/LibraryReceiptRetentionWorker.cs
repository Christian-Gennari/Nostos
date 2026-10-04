using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services;

namespace Nostos.Backend.Workers;

// Low-frequency housekeeper for library command receipts (issue #51). It
// runs one prune immediately after the application signals readiness, then
// once every CleanupIntervalHours. A failed cycle is logged and swallowed so
// the loop always reaches the next tick; the worker can therefore never
// prevent application startup, and cancellation (host shutdown) ends the
// loop gracefully.
public class LibraryReceiptRetentionWorker(
    IServiceScopeFactory scopeFactory,
    LibraryReceiptRetentionOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<LibraryReceiptRetentionWorker> logger,
    ILibraryMaintenanceCoordinator? maintenance = null) : BackgroundService
{
    // The interval is clamped to a calm 1..720h window regardless of
    // configuration, so a misconfigured value can never turn the worker into
    // a hot loop.
    public const int MinCleanupIntervalHours = 1;
    public const int MaxCleanupIntervalHours = 720;

    // Virtual so tests can drive the full loop with a short interval; the
    // production value is the clamped configured interval.
    protected virtual TimeSpan ScanInterval =>
        TimeSpan.FromHours(Math.Clamp(
            options.CleanupIntervalHours,
            MinCleanupIntervalHours,
            MaxCleanupIntervalHours));

    /// <summary>
    /// Runs one scoped prune cycle: resolve <see cref="LibraryReceiptRetentionService"/>
    /// from a fresh scope and prune library receipts, then note receipts
    /// (issue #260 §3) with the same service and policy. Exposed so tests can
    /// drive a single scan deterministically without starting the background
    /// loop.
    /// </summary>
    public async Task ScanOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = maintenance is null ? null : await maintenance.EnterOperationAsync(cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<LibraryReceiptRetentionService>();
        await service.PruneAsync(cancellationToken);
        await service.PruneNoteReceiptsAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Library receipt retention worker started.");

        try
        {
            // The first scan waits for application readiness so it never
            // races the database bootstrap at startup.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            {
                await started.Task.WaitAsync(stoppingToken);
            }

            using var timer = new PeriodicTimer(ScanInterval);
            do
            {
                try
                {
                    await ScanOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    // A failed cycle never stops the loop; the next interval
                    // retries the cleanup.
                    logger.LogError(ex, "Library receipt retention scan failed; will retry on the next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "Library receipt retention worker received stop signal and is shutting down.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Library receipt retention worker crashed fatally.");
        }
    }
}

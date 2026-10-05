using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Periodic driver for the post-activation derived rebuild. It starts after the
/// startup reconciler has finished (hosted services begin only after Program's
/// awaited reconciliation) and after the configured startup delay. A failed
/// pass is logged and retried with an exponential backoff (capped at 15
/// minutes) so a permanently failing control tree cannot become a 15-second log
/// storm; the backoff never touches the durable activation outcome.
/// </summary>
internal sealed class SelfHostedDerivedRebuildWorker(
    SelfHostedDerivedRebuildService rebuild,
    IOptions<SelfHostedActivationMaintenanceOptions> options,
    TimeProvider clock,
    ILogger<SelfHostedDerivedRebuildWorker> logger) : BackgroundService
{
    private int _consecutiveFailures;

    internal Task<SelfHostedDerivedRebuildPassResult> RunBatchAsync(CancellationToken ct) =>
        rebuild.RunPendingCoreAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.Value.StartupDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var failed = 0;
            try
            {
                var result = await RunBatchAsync(stoppingToken);
                failed = result.Failed;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failed = 1;
                logger.LogWarning(exception,
                    "Derived rebuild pass failed; the next pass retries and activation remains committed.");
            }

            _consecutiveFailures = failed > 0 ? Math.Min(_consecutiveFailures + 1, 6) : 0;
            var delay = _consecutiveFailures == 0
                ? options.Value.DerivedRebuildInterval
                : TimeSpan.FromSeconds(Math.Min(
                    options.Value.DerivedRebuildIntervalSeconds * (1 << _consecutiveFailures),
                    (int)TimeSpan.FromMinutes(15).TotalSeconds));
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

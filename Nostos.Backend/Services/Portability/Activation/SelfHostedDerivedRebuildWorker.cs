using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Periodic driver for the post-activation derived rebuild. It starts after the
/// startup reconciler has finished (hosted services begin only after Program's
/// awaited reconciliation) and after the configured startup delay. A failed
/// pass is logged and retried; it never throws past the loop and never touches
/// the durable activation outcome.
/// </summary>
internal sealed class SelfHostedDerivedRebuildWorker(
    SelfHostedDerivedRebuildService rebuild,
    IOptions<SelfHostedActivationMaintenanceOptions> options,
    TimeProvider clock,
    ILogger<SelfHostedDerivedRebuildWorker> logger) : BackgroundService
{
    internal Task<int> RunBatchAsync(CancellationToken ct) => rebuild.RunPendingAsync(ct);

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
            try
            {
                await RunBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Derived rebuild pass failed; the next pass retries and activation remains committed.");
            }

            try
            {
                await Task.Delay(options.Value.DerivedRebuildInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

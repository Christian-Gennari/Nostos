using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Startup/periodic scan that enqueues durably claimed (<c>Restoring</c>)
/// recovery copies into the shared library-switch dispatcher. Runs after the
/// activation reconciler and database bootstrap in the host startup order, so a
/// crash-interrupted cutover is first resolved to one complete generation; the
/// dispatcher's slot CAS makes repeated scans idempotent.
/// </summary>
internal sealed class SelfHostedRecoveryRestoreProcessor(
    SelfHostedActivationDispatcher dispatcher,
    TimeProvider clock,
    ILogger<SelfHostedRecoveryRestoreProcessor> logger) : BackgroundService
{
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.ScanPendingRestoresAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The recovery restore scan failed; it retries on the next pass.");
            }

            try
            {
                await Task.Delay(ScanInterval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

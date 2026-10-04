using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>Immediate startup sweep and periodic TTL cleanup using a fresh scoped DbContext.</summary>
public sealed class MigrationTransferCleanupWorker(IServiceScopeFactory scopes,
    IOptions<TransferStorageOptions> options, TimeProvider clock, ILogger<MigrationTransferCleanupWorker> logger,
    IMigrationMaintenanceGate maintenance) : BackgroundService
{
    internal async Task RunBatchAsync(CancellationToken ct)
    {
        await using var operation = await maintenance.EnterAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        try { await scope.ServiceProvider.GetRequiredService<MigrationTransferCleanup>().SweepAsync(ct); }
        catch (MigrationMaintenanceRequestedException) { /* Resumable detached/terminal cleanup queue. */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBatchAsync(stoppingToken);
            }
            catch (MigrationMaintenanceRequestedException) { }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Migration TTL sweep failed; next sweep will retry"); }
            try { await Task.Delay(options.Value.CleanupInterval, clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

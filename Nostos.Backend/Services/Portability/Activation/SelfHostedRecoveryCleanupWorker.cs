using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>Observable result of one activation maintenance pass.</summary>
internal sealed record SelfHostedMaintenancePassResult(
    bool Ran,
    int RemovedRecoveryCopies,
    int RemovedOrphanEntries,
    int LeftAmbiguousOrphans,
    int ReconciledRecoveryStatuses);

/// <summary>
/// Scheduled recovery expiry cleanup and activation orphan sweep (issue #681,
/// Slice 10). Each pass takes the shared library operation lease, so it can
/// never run inside or race the exclusive maintenance window; when admission is
/// closed (maintenance active or fail-closed after an unrepaired cutover) the
/// pass is skipped and retried on the next interval. Deletion itself goes
/// exclusively through the guarded expiry cleanup, which refuses unexpired
/// copies, copies in <c>Restoring</c>, and copies an activation/restore journal
/// still references; a crash mid-delete resumes from the durable deletion
/// marker. After physical removal the pass projects the copy's absence onto the
/// job's durable recovery status so activation status cannot keep claiming an
/// available copy that no longer exists.
/// </summary>
internal sealed class SelfHostedRecoveryCleanupWorker(
    IServiceScopeFactory scopes,
    IOptions<SelfHostedActivationMaintenanceOptions> options,
    TimeProvider clock,
    LibraryMaintenanceCoordinator maintenance,
    SelfHostedRecoveryManifestStore manifests,
    ILogger<SelfHostedRecoveryCleanupWorker> logger) : BackgroundService
{
    internal async Task<SelfHostedMaintenancePassResult> RunBatchAsync(CancellationToken ct)
    {
        if (maintenance.IsRecoveryRequired)
        {
            return new SelfHostedMaintenancePassResult(false, 0, 0, 0, 0);
        }

        using var operation = maintenance.TryEnterOperation();
        if (operation is null)
        {
            // Exclusive maintenance owns the library: never touch a copy that a
            // cutover or restore may be retaining, and never race a rename.
            return new SelfHostedMaintenancePassResult(false, 0, 0, 0, 0);
        }

        await using var scope = scopes.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredService<ISelfHostedRecoveryCleanup>();
        var removedCopies = await cleanup.DeleteExpiredAsync(ct);
        var sweep = scope.ServiceProvider.GetRequiredService<SelfHostedActivationOrphanSweep>();
        var sweepResult = await sweep.SweepAsync(ct);
        var reconciled = await ReconcileExpiredRecoveryStatusAsync(scope, ct);
        return new SelfHostedMaintenancePassResult(true, removedCopies, sweepResult.RemovedEntries,
            sweepResult.LeftAmbiguous, reconciled);
    }

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
                logger.LogWarning(exception, "Activation recovery cleanup sweep failed; the next pass retries.");
            }

            try
            {
                await Task.Delay(options.Value.SweepInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// The guarded cleanup deletes the physical copy; the job row's recovery
    /// status is a separate projection that must not keep reporting
    /// <c>Available</c> after the copy is conclusively gone. A partial deletion
    /// (marker or material still present) is left for the next pass.
    /// </summary>
    private async Task<int> ReconcileExpiredRecoveryStatusAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var candidates = await db.MigrationJobRecords.AsNoTracking()
            .Where(job => job.RecoveryStatus == (int)MigrationRecoveryStatus.Available)
            .Select(job => job.Id)
            .ToListAsync(ct);
        var reconciled = 0;
        foreach (var jobId in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!manifests.HasDeletionMarker(jobId) && manifests.Read(jobId) is not null)
                {
                    continue;
                }

                if (manifests.MaterialExists(jobId))
                {
                    continue; // interrupted deletion: the next pass resumes it
                }

                var now = clock.GetUtcNow().UtcDateTime;
                reconciled += await db.MigrationJobRecords
                    .Where(job => job.Id == jobId
                        && job.RecoveryStatus == (int)MigrationRecoveryStatus.Available)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.RecoveryStatus, (int)MigrationRecoveryStatus.Expired)
                        .SetProperty(job => job.UpdatedAtUtc, now)
                        .SetProperty(job => job.Version, job => job.Version + 1), ct);
            }
            catch (MigrationActivationException)
            {
                // A corrupt manifest is never interpreted as a deleted copy.
                logger.LogWarning("Recovery status projection skipped one corrupt manifest; an operator must inspect it.");
            }
        }

        return reconciled;
    }
}

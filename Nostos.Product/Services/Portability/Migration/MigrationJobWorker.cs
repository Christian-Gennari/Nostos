using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

// Singleton host load control; the database lease is the job correctness gate.
internal sealed class MigrationProcessingSlots(IOptions<TransferStorageOptions> options) : IDisposable
{
    internal SemaphoreSlim Gate { get; } = new(options.Value.MaxConcurrentJobs);
    public void Dispose() => Gate.Dispose();
}

/// <summary>
/// Browser-independent, scoped job runner. Starts with immediate recovery,
/// renews every 60 seconds for a five-minute lease, and releases on shutdown.
/// Hosts sharing the DB must synchronize clocks (the store owns expiry time).
/// </summary>
public sealed class MigrationJobWorker : BackgroundService
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(MigrationContractLimits.WorkerLeaseDurationMinutes);
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly MigrationJobCancellationRegistry _cancellations;
    private readonly MigrationProcessingSlots _slots;
    private readonly ILogger<MigrationJobWorker> _logger;
    private readonly IMigrationMaintenanceGate _maintenance;

    internal MigrationJobWorker(IServiceScopeFactory scopes, TimeProvider clock, MigrationJobCancellationRegistry cancellations,
        MigrationProcessingSlots slots, ILogger<MigrationJobWorker> logger, IMigrationMaintenanceGate maintenance)
    { _scopes = scopes; _clock = clock; _cancellations = cancellations; _slots = slots; _logger = logger; _maintenance = maintenance; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var count = 0;
            try { count = await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Migration worker cycle failed; recovery will retry"); }
            try { await Task.Delay(TimeSpan.FromSeconds(count > 0 ? 2 : 10), _clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task<int> RunCycleAsync(CancellationToken ct)
    {
        IReadOnlyList<MigrationJob> candidates;
        await using (var operation = await _maintenance.EnterAsync(ct))
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var now = _clock.GetUtcNow();
            var recovered = await store.GetJobsNeedingRecoveryAsync(now, ct);
            var complete = await db.MigrationSessionRecords.AsNoTracking().Where(s => s.State == (int)MigrationSessionState.Complete
                && s.ExpiresAtUtc > now.UtcDateTime).Select(s => s.JobId).ToListAsync(ct);
            var hasSession = await db.MigrationSessionRecords.AsNoTracking().Where(s => s.ExpiresAtUtc > now.UtcDateTime
                && s.State != (int)MigrationSessionState.Cancelled && s.State != (int)MigrationSessionState.Expired)
                .Select(s => s.JobId).ToListAsync(ct);
            var alive = await db.MigrationJobRecords.AsNoTracking().Where(j => j.ExpiresAtUtc > now.UtcDateTime).Select(j => j.Id).ToListAsync(ct);
            candidates = recovered.Where(j => alive.Contains(j.Id) && j.State < MigrationJobState.ReadyToActivate
                && (j.Direction == MigrationDirection.Export
                    || j.State <= MigrationJobState.Preparing && hasSession.Contains(j.Id)
                    || complete.Contains(j.Id))).ToList();
        }
        var runs = new List<Task>();
        foreach (var candidate in candidates)
        {
            if (!await _slots.Gate.WaitAsync(0, ct)) break;
            runs.Add(RunJobAsync(candidate.Id, ct));
        }
        await Task.WhenAll(runs);
        return runs.Count;
    }

    private async Task RunJobAsync(Guid id, CancellationToken stoppingToken)
    {
        try
        {
            while (await RunPhaseUnitAsync(id, stoppingToken)) { }
        }
        finally { _slots.Gate.Release(); }
    }

    private async Task<bool> RunPhaseUnitAsync(Guid id, CancellationToken stoppingToken)
    {
        // Admission precedes every DI scope. Release/cleanup happens inside this
        // unit, then all scopes dispose, then admission releases. No sleep here.
        await using var operation = await _maintenance.EnterAsync(stoppingToken);
        string? token = null;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task? heartbeat = null;
        IDisposable? registration = null;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
            token = await store.TryAcquireLeaseAsync(id, LeaseDuration, stoppingToken);
            if (token is null) return false;
            var job = await store.GetAsync(id, running.Token) ?? throw MigrationJobStoreException.NotFound(id);
            // Never borrow a successor's token from this post-acquisition read.
            if (job.LeaseToken != token || job.LeaseExpiresAtUtc is null || job.LeaseExpiresAtUtc <= _clock.GetUtcNow()) return false;
            registration = _cancellations.Register(id, running, job.LeaseExpiresAtUtc.Value);
            running.Token.ThrowIfCancellationRequested();
            heartbeat = HeartbeatAsync(id, token, running);
            return await scope.ServiceProvider.GetRequiredService<MigrationJobProcessor>().ProcessStepAsync(job, running);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested) { return false; }
        catch (MigrationJobStoreException ex) when (ex.Code == MigrationJobStoreErrorCodes.LeaseConflict
            || ex.Code == MigrationJobStoreErrorCodes.InvalidState) { running.Cancel(); return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Migration job {JobId} failed", id);
            if (token is not null && !running.IsCancellationRequested)
            {
                try { await FailAsync(id, token, ex); }
                catch (Exception failure) { _logger.LogWarning(failure, "Could not record migration failure {JobId}; recovery will retry", id); }
            }
            return false;
        }
        finally
        {
            running.Cancel();
            if (heartbeat is not null) await heartbeat;
            registration?.Dispose();
            if (token is not null)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>().ReleaseLeaseAsync(id, token, CancellationToken.None);
                    // A maintenance request checkpoints here too. Terminal rows
                    // retain the cleanup queue until admission reopens.
                    var released = await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>().GetAsync(id, CancellationToken.None);
                    if (!_maintenance.IsMaintenanceRequested && released is not null && MigrationJobTransitions.IsTerminal(released.State))
                        await scope.ServiceProvider.GetRequiredService<MigrationTransferCleanup>().CleanupJobAsync(id, CancellationToken.None);
                }
                catch (MigrationMaintenanceRequestedException) { }
                catch (Exception ex) { _logger.LogWarning(ex, "Migration release/cleanup will retry {JobId}", id); }
            }
        }
    }

    private async Task HeartbeatAsync(Guid id, string token, CancellationTokenSource running)
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, _clock, running.Token);
                if (_maintenance.IsMaintenanceRequested) { running.Cancel(); return; }
                await using var operation = await _maintenance.EnterAsync(running.Token);
                await using var scope = _scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>().RenewLeaseAsync(id, token, LeaseDuration, running.Token))
                { running.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested) { }
        catch (Exception ex)
        { _logger.LogWarning(ex, "Migration heartbeat failed {JobId}; stopping this owner", id); running.Cancel(); }
    }

    private async Task FailAsync(Guid id, string token, Exception error)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
        await using var transaction = await MigrationMutation.BeginAsync(db, CancellationToken.None);
        await MigrationMutation.LockLeaseAsync(db, id, token, _clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        await store.TransitionAsync(id, MigrationJobState.Failed, token, CancellationToken.None);
        var code = error switch
        {
            MigrationTransferException typed => typed.Code,
            PortableArchiveException archive => archive.Code,
            IOException => MigrationTransferException.StorageExhausted,
            _ => "migration_processing_failed",
        };
        await db.MigrationJobRecords.Where(j => j.Id == id && j.State == (int)MigrationJobState.Failed && j.MigrationLeaseToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.FailureCode, code).SetProperty(j => j.FailureMessage, code)
                .SetProperty(j => j.Version, j => j.Version + 1));
        await transaction.CommitAsync();
    }
}

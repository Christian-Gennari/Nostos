using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Internal archive-integration seam. A phase must be restart-idempotent from
/// durable facts, observe CancellationToken, report progress through context,
/// fence filesystem writes through ExecuteWriteAsync and durable metadata
/// through ExecuteMutationAsync (using its supplied DbContext, never a second
/// context while the fence holds the row lock). No handler may activate an import. Slices 9/10 replace the explicit unavailable handler.
/// </summary>
internal interface IMigrationPhaseHandler
{
    bool CanHandle(MigrationDirection direction, MigrationJobState state);
    Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct);
}

internal sealed class ArchiveIntegrationNotYetAvailableHandler : IMigrationPhaseHandler
{
    public bool CanHandle(MigrationDirection direction, MigrationJobState state) => true;
    public Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct) => throw MigrationTransferException.Error(
        context.Job.Direction == MigrationDirection.Import
            ? MigrationTransferException.ImportPreparationUnavailable : MigrationTransferException.ExportArtifactUnavailable);
}

/// <summary>
/// A fresh context/buffer budget per running job. Writes are fenced by the job
/// row lock and current token/expiry, so takeover or cancellation cannot commit
/// concurrently with a write. Each callback is one bounded, cancellable write;
/// handlers must not retain the callback or continue IO after it returns.
/// Progress and heartbeat use independent DI scopes/DbContexts.
/// </summary>
internal sealed class MigrationPhaseContext(IServiceScopeFactory scopes, TimeProvider clock,
    MigrationJob job, CancellationTokenSource running)
{
    internal MigrationJob Job { get; set; } = job;
    internal PortableArchiveBufferBudget BufferBudget { get; } = new(PortableArchiveLimits.MaxExplicitBufferBytes);
    internal Task ExecuteWriteAsync(Func<CancellationToken, Task> write, CancellationToken ct) =>
        ExecuteMutationAsync((_, token) => write(token), ct);

    // Slices 9/10 persist prepared-import/artifact metadata on the SAME context
    // holding the fence. Opening a second writer inside the callback would block
    // on our own row lock. The callback's DB changes commit/roll back with it.
    internal async Task ExecuteMutationAsync(Func<NostosDbContext, CancellationToken, Task> mutation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, running.Token);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        try
        {
            await using var transaction = await MigrationMutation.BeginAsync(db, linked.Token);
            await MigrationMutation.LockLeaseAsync(db, Job.Id, Job.LeaseToken!, clock.GetUtcNow().UtcDateTime, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            await mutation(db, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            await MigrationMutation.LockLeaseAsync(db, Job.Id, Job.LeaseToken!, clock.GetUtcNow().UtcDateTime, linked.Token);
            await transaction.CommitAsync(linked.Token);
        }
        catch (MigrationJobStoreException) { running.Cancel(); throw; }
    }

    internal async Task ReportProgressAsync(MigrationProgress progress, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
                .UpdateProgressAsync(Job.Id, progress, Job.LeaseToken!, ct);
        }
        catch (MigrationJobStoreException) { running.Cancel(); throw; }
    }
}

internal sealed class MigrationJobProcessor(NostosDbContext db, IMigrationJobStore store,
    IEnumerable<IMigrationPhaseHandler> handlers, IServiceScopeFactory scopes, TimeProvider clock)
{
    internal async Task ProcessAsync(MigrationJob job, CancellationTokenSource running)
    {
        var ct = running.Token;
        var context = new MigrationPhaseContext(scopes, clock, job, running);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            job = await store.GetAsync(job.Id, ct) ?? throw MigrationJobStoreException.NotFound(job.Id);
            if (job.LeaseToken != context.Job.LeaseToken || job.LeaseExpiresAtUtc is null || job.LeaseExpiresAtUtc <= clock.GetUtcNow())
                throw MigrationJobStoreException.LeaseConflict(job.Id);
            context.Job = job;
            if (job.State is MigrationJobState.ReadyToActivate or MigrationJobState.Activating || MigrationJobTransitions.IsTerminal(job.State)) return;
            if (job.State == MigrationJobState.Pending)
            {
                await store.TransitionAsync(job.Id, MigrationJobState.Preparing, job.LeaseToken!, ct);
                continue;
            }
            if (job.Direction == MigrationDirection.Import)
            {
                var session = await db.MigrationSessionRecords.AsNoTracking().SingleOrDefaultAsync(s => s.JobId == job.Id, ct);
                if (session is null || session.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime) return;
                if (job.State == MigrationJobState.Preparing)
                {
                    if (session.State is (int)MigrationSessionState.Cancelled or (int)MigrationSessionState.Expired) return;
                    await store.TransitionAsync(job.Id, MigrationJobState.Transferring, job.LeaseToken!, ct);
                    continue;
                }
                if (session.State != (int)MigrationSessionState.Complete) return;
                if (job.State == MigrationJobState.Transferring)
                {
                    await store.TransitionAsync(job.Id, MigrationJobState.Validating, job.LeaseToken!, ct);
                    continue;
                }
            }
            var handler = handlers.Last(h => h.CanHandle(job.Direction, job.State));
            await handler.ExecuteAsync(context, ct);
            ct.ThrowIfCancellationRequested();
            var target = job.State switch
            {
                MigrationJobState.Preparing => MigrationJobState.Transferring,
                MigrationJobState.Transferring => MigrationJobState.Validating,
                MigrationJobState.Validating when job.Direction == MigrationDirection.Import => MigrationJobState.ReadyToActivate,
                MigrationJobState.Validating => MigrationJobState.Completed,
                _ => throw MigrationTransferException.Error(MigrationTransferException.InvalidState),
            };
            await store.TransitionAsync(job.Id, target, job.LeaseToken!, ct);
        }
    }
}

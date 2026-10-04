using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Optional hook for durable staging providers. Sweep only abandoned areas older
/// than cutoff and never any protected identifier. The provider owns staging
/// metadata and concurrent-write exclusion; the engine never guesses its format.
/// </summary>
public interface IMigrationStagingCleanup
{
    Task CleanupAbandonedAsync(DateTimeOffset cutoffUtc, IReadOnlySet<PortableStagingId> protectedIds, CancellationToken ct);
    Task DeleteAsync(PortableStagingId stagingId, CancellationToken ct);
}

/// <summary>
/// Idempotent cleanup of generated scopes only. Never recursively deletes the
/// root, unknown siblings, or follows links below the operator-owned root. State
/// is invalidated before deletion; rows remain so failed deletions retry. Host
/// clocks must agree with the job store's authoritative clock.
/// </summary>
public sealed class MigrationTransferCleanup(
    NostosDbContext db, TransferPathResolver paths, ITransferStorageCapacity capacity,
    IEnumerable<IMigrationStagingCleanup> stagingHooks, ILogger<MigrationTransferCleanup> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task SweepAsync(CancellationToken ct)
    {
        // An active lease fences expiry/cleanup even when a resource TTL elapsed.
        var candidates = await db.MigrationJobRecords.AsNoTracking()
            .Where(j => j.State != (int)MigrationJobState.Activating
                && (j.ExpiresAtUtc <= Now || j.State >= (int)MigrationJobState.Completed
                    || db.MigrationSessionRecords.Any(s => s.JobId == j.Id && s.ExpiresAtUtc <= Now)))
            .Select(j => j.Id).ToListAsync(ct);
        foreach (var id in candidates)
        {
            try { await CleanupJobAsync(id, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransferPathException)
            { logger.LogWarning(ex, "Transfer cleanup will retry job {JobId}", id); }
        }
        await db.MigrationStorageReservations.Where(r => r.ClaimedJobId == null && r.ReleasedAtUtc == null && r.ExpiresAtUtc <= Now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleasedAtUtc, Now).SetProperty(r => r.Version, r => r.Version + 1), ct);

        // BEGIN IMMEDIATE / owning-row locks serialize creation with orphan scan.
        await using (var transaction = await MigrationMutation.BeginAsync(db, ct))
        {
            // PostgreSQL: creation first locks a job row; we never delete a scope
            // whose session insertion is uncommitted: young orphans have a TTL grace.
            var root = paths.VerifyPathWithinRoot(paths.GetUploadsRoot());
            if (Directory.Exists(root))
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty) continue;
                    try
                    {
                        paths.VerifyPathWithinRoot(directory);
                        if (Directory.GetLastWriteTimeUtc(directory) > Now.AddHours(-MigrationContractLimits.SessionExpiryHours)) continue;
                        if (!await db.MigrationSessionRecords.AnyAsync(s => s.Id == id, ct))
                            DeleteScope(paths.GetUploadSessionDirectory(id));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransferPathException)
                    { logger.LogWarning(ex, "Transfer cleanup will retry orphan {SessionId}", id); }
                }
            }
            await transaction.CommitAsync(ct);
        }
        var protectedIds = (await db.MigrationJobRecords.AsNoTracking()
            .Where(j => j.PreparedStagingId != null && (j.ExpiresAtUtc > Now && j.State < (int)MigrationJobState.Completed
                || j.MigrationLeaseToken != null && j.LeaseExpiresAtUtc > Now))
            .Select(j => j.PreparedStagingId!.Value).ToListAsync(ct)).Select(id => new PortableStagingId(id)).ToHashSet();
        foreach (var hook in stagingHooks)
            await hook.CleanupAbandonedAsync(_clock.GetUtcNow().AddHours(-MigrationContractLimits.SessionExpiryHours), protectedIds, ct);
    }

    public async Task CleanupJobAsync(Guid jobId, CancellationToken ct, bool discardUploads = false)
    {
        await using var transaction = await MigrationMutation.BeginAsync(db, ct);
        var now = Now;
        // This conditional write locks the job and excludes newly renewed/active work.
        if (await db.MigrationJobRecords.Where(j => j.Id == jobId && j.State != (int)MigrationJobState.Activating
                && (j.MigrationLeaseToken == null || j.LeaseExpiresAtUtc == null || j.LeaseExpiresAtUtc <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Version, j => j.Version + 1), ct) != 1) return;
        var job = await db.MigrationJobRecords.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
        var sessions = await db.MigrationSessionRecords.AsNoTracking().Where(s => s.JobId == jobId).ToListAsync(ct);
        var terminal = MigrationJobTransitions.IsTerminal((MigrationJobState)job.State);
        var expires = job.ExpiresAtUtc <= now || sessions.Any(s => s.ExpiresAtUtc <= now);
        if (!terminal && expires)
        {
            MigrationJobStateMachine.EnsureTransitionAllowed(jobId, (MigrationDirection)job.Direction,
                (MigrationJobState)job.State, MigrationJobState.Expired);
            if (await db.MigrationJobRecords.Where(j => j.Id == jobId && j.Version == job.Version && j.State == job.State)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Expired)
                    .SetProperty(j => j.MigrationLeaseToken, (string?)null).SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(j => j.UpdatedAtUtc, now).SetProperty(j => j.Version, j => j.Version + 1), ct) != 1) return;
            terminal = true;
        }
        if (!terminal) return;
        // Commit invalidation before attempting deletion. On failure later, the
        // terminal rows are the durable retry queue. The lock transaction below
        // protects each deletion from concurrent RetryAsync/session reactivation.
        await transaction.CommitAsync(ct);
        await using var deleteTransaction = await MigrationMutation.BeginAsync(db, ct);
        if (await db.MigrationJobRecords.Where(j => j.Id == jobId && j.State >= (int)MigrationJobState.Completed
                && (j.MigrationLeaseToken == null || j.LeaseExpiresAtUtc == null || j.LeaseExpiresAtUtc <= Now))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Version, j => j.Version + 1), ct) != 1) return;
        job = await db.MigrationJobRecords.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
        sessions = await db.MigrationSessionRecords.AsNoTracking().Where(s => s.JobId == jobId).ToListAsync(ct);
        foreach (var session in sessions)
        {
            var expired = session.ExpiresAtUtc <= Now || job.State == (int)MigrationJobState.Expired;
            var remove = discardUploads || expired || job.State == (int)MigrationJobState.Completed;
            // Cancel/failed chunks may be retained for identity-bound retry until
            // session TTL, while their peak reservation is released immediately.
            if (remove || job.State is (int)MigrationJobState.Cancelled or (int)MigrationJobState.Failed)
                await db.MigrationSessionRecords.Where(s => s.Id == session.Id && s.Version == session.Version)
                    .ExecuteUpdateAsync(s => s.SetProperty(s => s.State, expired ? (int)MigrationSessionState.Expired : (int)MigrationSessionState.Cancelled)
                        .SetProperty(s => s.UpdatedAtUtc, Now).SetProperty(s => s.Version, s => s.Version + 1), ct);
            if (remove) DeleteScope(paths.GetUploadSessionDirectory(session.Id));
            else DeleteChunkTemps(session.Id);
        }
        if (job.PreparedStagingId is { } stagingId)
        {
            foreach (var hook in stagingHooks) await hook.DeleteAsync(new(stagingId), ct);
            // If a durable provider has not supplied a hook, only the generated
            // known-owned directory can be removed; never enumerate arbitrary staging.
            DeleteScope(paths.GetStagingDirectory(stagingId));
        }
        var artifact = await db.MigrationExportArtifactRecords.AsNoTracking().SingleOrDefaultAsync(a => a.JobId == jobId, ct);
        if (artifact is null || artifact.State != (int)MigrationExportArtifactState.Available || artifact.ExpiresAtUtc <= Now)
        {
            DeleteScope(paths.GetExportDirectory(jobId));
            if (artifact is not null)
                await db.MigrationExportArtifactRecords.Where(a => a.JobId == jobId && a.Version == artifact.Version)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, (int)MigrationExportArtifactState.Deleted)
                        .SetProperty(a => a.DeletedAtUtc, Now).SetProperty(a => a.Version, a => a.Version + 1), ct);
        }
        if (job.ReservationId is { } reservationId) await capacity.ReleaseAsync(reservationId, ct);
        await deleteTransaction.CommitAsync(ct);
    }

    private void DeleteChunkTemps(Guid id)
    {
        var directory = paths.VerifyPathWithinRoot(paths.GetUploadSessionDirectory(id));
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, ".chunk-*.tmp"))
            File.Delete(paths.VerifyPathWithinRoot(file));
    }

    private void DeleteScope(string directory)
    {
        directory = paths.VerifyPathWithinRoot(directory);
        if (!Directory.Exists(directory)) return;
        // Verify every entry individually and never use recursive Delete (which
        // could traverse a linked descendant). Unknown root siblings stay intact.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var verified = paths.VerifyPathWithinRoot(entry);
            if (Directory.Exists(verified)) DeleteScope(verified);
            else File.Delete(verified);
        }
        Directory.Delete(paths.VerifyPathWithinRoot(directory));
    }
}

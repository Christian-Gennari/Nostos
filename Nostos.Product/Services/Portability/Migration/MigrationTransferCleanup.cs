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
    IMigrationMaintenanceGate maintenance, FileMigrationUploadStore files, TimeProvider? timeProvider = null)
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
            Checkpoint(ct);
            try { await CleanupJobAsync(id, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransferPathException)
            { logger.LogWarning(ex, "Transfer cleanup will retry job {JobId}", id); }
        }
        await db.MigrationStorageReservations.Where(r => r.ClaimedJobId == null && r.ReleasedAtUtc == null && r.ExpiresAtUtc <= Now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleasedAtUtc, Now).SetProperty(r => r.Version, r => r.Version + 1), ct);

        // Unknown generated scopes have a full TTL grace. No transaction spans
        // enumeration, path validation, atomic detach, or physical deletion.
        var root = paths.VerifyPathWithinRoot(paths.GetUploadsRoot());
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                Checkpoint(ct);
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty) continue;
                try
                {
                    if (await db.MigrationSessionRecords.AnyAsync(s => s.Id == id, ct)) continue;
                    await using var orphanLease = await files.EnterJobAsync(id, ct);
                    paths.VerifyPathWithinRoot(directory);
                    if (Directory.GetLastWriteTimeUtc(directory) > Now.AddHours(-MigrationContractLimits.SessionExpiryHours)) continue;
                    if (!await db.MigrationSessionRecords.AnyAsync(s => s.Id == id, ct))
                    {
                        var detached = DetachScope(paths.GetUploadSessionDirectory(id), ct);
                        if (detached is not null) await DeleteDetachedAsync(detached, ct);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransferPathException)
                { logger.LogWarning(ex, "Transfer cleanup will retry orphan {SessionId}", id); }
            }
        }
        // Detached names are never reused. They are the durable queue when a
        // deletion crashes or yields to maintenance halfway through a directory.
        var garbage = paths.VerifyPathWithinRoot(paths.GetDetachedScopesRoot());
        if (Directory.Exists(garbage))
            foreach (var directory in Directory.EnumerateDirectories(garbage))
            {
                Checkpoint(ct);
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && id != Guid.Empty)
                    await DeleteDetachedAsync(directory, ct);
            }
        var protectedIds = (await db.MigrationJobRecords.AsNoTracking()
            .Where(j => j.PreparedStagingId != null && (j.ExpiresAtUtc > Now && j.State < (int)MigrationJobState.Completed
                || j.MigrationLeaseToken != null && j.LeaseExpiresAtUtc > Now))
            .Select(j => j.PreparedStagingId!.Value).ToListAsync(ct)).Select(id => new PortableStagingId(id)).ToHashSet();
        foreach (var hook in stagingHooks)
        {
            Checkpoint(ct);
            await hook.CleanupAbandonedAsync(_clock.GetUtcNow().AddHours(-MigrationContractLimits.SessionExpiryHours), protectedIds, ct);
        }
    }

    public async Task CleanupJobAsync(Guid jobId, CancellationToken ct, bool discardUploads = false)
    {
        if (!await db.MigrationJobRecords.AnyAsync(j => j.Id == jobId, ct)) return;
        var detached = new List<string>();
        await using (var fileLease = await files.EnterJobAsync(jobId, ct))
        {
            if (!await InvalidateAsync(jobId, discardUploads, ct)) return;
            // No transaction during file work. Engine retry takes this same mutex.
            Checkpoint(ct);
            var job = await db.MigrationJobRecords.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
            if (!MigrationJobTransitions.IsTerminal((MigrationJobState)job.State)) return;
            var sessions = await db.MigrationSessionRecords.AsNoTracking().Where(s => s.JobId == jobId).ToListAsync(ct);
            foreach (var session in sessions)
            {
                Checkpoint(ct);
                var remove = discardUploads || session.ExpiresAtUtc <= Now || job.State is (int)MigrationJobState.Expired or (int)MigrationJobState.Completed;
                // Retain failed/cancelled verified uploads until TTL for retry.
                if (remove) Detach(paths.GetUploadSessionDirectory(session.Id));
                else DeleteChunkTemps(session.Id, ct);
            }
            if (job.PreparedStagingId is { } stagingId)
            {
                foreach (var hook in stagingHooks)
                {
                    Checkpoint(ct);
                    await hook.DeleteAsync(new(stagingId), ct);
                }
                // Providers own their metadata. Without a hook, only the generated
                // known-owned staging directory is eligible; no arbitrary scan.
                Detach(paths.GetStagingDirectory(stagingId));
            }
            var artifact = await db.MigrationExportArtifactRecords.AsNoTracking().SingleOrDefaultAsync(a => a.JobId == jobId, ct);
            if (artifact is null || artifact.State != (int)MigrationExportArtifactState.Available || artifact.ExpiresAtUtc <= Now)
            {
                Detach(paths.GetExportDirectory(jobId));
                if (artifact is not null)
                    await db.MigrationExportArtifactRecords.Where(a => a.JobId == jobId && a.Version == artifact.Version)
                        .ExecuteUpdateAsync(set => set.SetProperty(a => a.State, (int)MigrationExportArtifactState.Deleted)
                            .SetProperty(a => a.DeletedAtUtc, Now).SetProperty(a => a.Version, a => a.Version + 1), ct);
            }
        }
        // Detached names are never reused. Release the job mutex before deleting
        // old data, so retry can prepare a fresh scope concurrently with deletion.
        foreach (var directory in detached) await DeleteDetachedAsync(directory, ct);

        void Detach(string directory)
        { var moved = DetachScope(directory, ct); if (moved is not null) detached.Add(moved); }
    }

    private async Task<bool> InvalidateAsync(Guid jobId, bool discardUploads, CancellationToken ct)
    {
        await using var transaction = await MigrationMutation.BeginAsync(db, ct);
        var now = Now;
        if (await db.MigrationJobRecords.Where(j => j.Id == jobId && j.State != (int)MigrationJobState.Activating
                && (j.MigrationLeaseToken == null || j.LeaseExpiresAtUtc == null || j.LeaseExpiresAtUtc <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.Version, j => j.Version + 1), ct) != 1) return false;
        var job = await db.MigrationJobRecords.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
        var sessions = await db.MigrationSessionRecords.AsNoTracking().Where(s => s.JobId == jobId).ToListAsync(ct);
        var terminal = MigrationJobTransitions.IsTerminal((MigrationJobState)job.State);
        var expires = job.ExpiresAtUtc <= now || job.State <= (int)MigrationJobState.Validating && sessions.Any(s => s.ExpiresAtUtc <= now);
        if (!terminal && expires)
        {
            MigrationJobStateMachine.EnsureTransitionAllowed(jobId, (MigrationDirection)job.Direction,
                (MigrationJobState)job.State, MigrationJobState.Expired);
            if (await db.MigrationJobRecords.Where(j => j.Id == jobId && j.Version == job.Version && j.State == job.State)
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.State, (int)MigrationJobState.Expired)
                    .SetProperty(j => j.MigrationLeaseToken, (string?)null).SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(j => j.UpdatedAtUtc, now).SetProperty(j => j.Version, j => j.Version + 1), ct) != 1) return false;
            terminal = true;
        }
        if (!terminal) return false;
        // Terminal metadata is the durable deletion queue; invalidation/release
        // commits before detaching anything. No filesystem callback belongs here.
        foreach (var session in sessions)
        {
            var expired = session.ExpiresAtUtc <= now || expires;
            if (expired || discardUploads || job.State >= (int)MigrationJobState.Completed)
                await db.MigrationSessionRecords.Where(s => s.Id == session.Id && s.Version == session.Version)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.State, expired ? (int)MigrationSessionState.Expired : (int)MigrationSessionState.Cancelled)
                        .SetProperty(s => s.UpdatedAtUtc, now).SetProperty(s => s.Version, s => s.Version + 1), ct);
        }
        var artifact = await db.MigrationExportArtifactRecords.AsNoTracking().SingleOrDefaultAsync(a => a.JobId == jobId, ct);
        if (artifact is not null && artifact.State != (int)MigrationExportArtifactState.Deleted
            && (artifact.State != (int)MigrationExportArtifactState.Available || artifact.ExpiresAtUtc <= now))
            await db.MigrationExportArtifactRecords.Where(a => a.JobId == jobId && a.Version == artifact.Version)
                .ExecuteUpdateAsync(set => set.SetProperty(a => a.State, (int)MigrationExportArtifactState.Expired)
                    .SetProperty(a => a.Version, a => a.Version + 1), ct);
        if (job.ReservationId is { } reservationId) await capacity.ReleaseAsync(reservationId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task DeleteDetachedAsync(string directory, CancellationToken ct)
    {
        var id = Guid.ParseExact(Path.GetFileName(directory), "N");
        await using var deletionLease = await files.EnterJobAsync(id, ct);
        await files.BeforeDetachedDeleteAsync(ct);
        DeleteScope(directory, ct);
    }

    private void DeleteChunkTemps(Guid id, CancellationToken ct)
    {
        var directory = paths.VerifyPathWithinRoot(paths.GetUploadSessionDirectory(id));
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, ".chunk-*.tmp"))
        { Checkpoint(ct); File.Delete(paths.VerifyPathWithinRoot(file)); }
    }

    private string? DetachScope(string directory, CancellationToken ct)
    {
        Checkpoint(ct);
        directory = paths.VerifyPathWithinRoot(directory);
        if (!Directory.Exists(directory)) return null;
        VerifyTree(directory, ct); // Refuse linked descendants before detaching.
        var detached = paths.GetDetachedScopeDirectory(Guid.NewGuid());
        paths.EnsureParentDirectoryExists(detached);
        Directory.Move(directory, paths.VerifyPathWithinRoot(detached));
        return detached;
    }

    private void VerifyTree(string directory, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.VerifyPathWithinRoot(directory)))
        {
            Checkpoint(ct);
            var verified = paths.VerifyPathWithinRoot(entry);
            if (Directory.Exists(verified)) VerifyTree(verified, ct);
        }
    }

    private void Checkpoint(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (maintenance.IsMaintenanceRequested) throw new MigrationMaintenanceRequestedException();
    }

    private void DeleteScope(string directory, CancellationToken ct)
    {
        Checkpoint(ct);
        directory = paths.VerifyPathWithinRoot(directory);
        if (!Directory.Exists(directory)) return;
        // Verify every entry individually and never use recursive Delete (which
        // could traverse a linked descendant). Unknown root siblings stay intact.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            Checkpoint(ct);
            var verified = paths.VerifyPathWithinRoot(entry);
            if (Directory.Exists(verified)) DeleteScope(verified, ct);
            else File.Delete(verified);
        }
        Directory.Delete(paths.VerifyPathWithinRoot(directory));
    }
}

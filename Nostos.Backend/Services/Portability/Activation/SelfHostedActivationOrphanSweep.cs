using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>Observable result of one orphan sweep pass.</summary>
internal sealed record SelfHostedOrphanSweepResult(int RemovedEntries, int LeftAmbiguous);

/// <summary>
/// Activation orphan sweep (issue #681, Slice 10). Removes only leftovers that
/// are provably unreferenced: no unresolved activation journal, the entry is
/// older than the safety age, and every path resolves under the configured
/// activation/recovery roots without following a symlink or reparse point out
/// of the tree. A valid or corrupt recovery manifest, retained material without
/// a manifest, an unrecognized entry and an active job are all left in place.
///
/// <para><b>Ownership fence.</b> A job's state is never decided from a pass-wide
/// snapshot. Immediately before deleting one job's leftovers the sweep re-reads
/// that job and, for a terminal job, publishes a fenced cleanup claim in the
/// existing lease fields with a row-version CAS. <see cref="EfMigrationJobStore"/>
/// refuses to reactivate a job while an unexpired cleanup claim is held, so a
/// concurrent retry either wins before the claim (and the sweep skips the job)
/// or loses against it (and changes nothing); exactly one side wins.</para>
/// </summary>
internal sealed class SelfHostedActivationOrphanSweep(
    SelfHostedActivationPaths paths,
    SelfHostedActivationJournalStore journals,
    SelfHostedRecoveryManifestStore manifests,
    NostosDbContext db,
    IOptions<SelfHostedActivationMaintenanceOptions> options,
    TimeProvider clock,
    ILogger<SelfHostedActivationOrphanSweep> logger)
{
    private static readonly Guid ProbeId = Guid.Parse("f0e1d2c3b4a5968778695a4b3c2d1e0f");

    /// <summary>Test seam: runs immediately before a job's cleanup claim is attempted.</summary>
    internal Action<Guid>? BeforeJobClaimForTesting { get; set; }

    /// <summary>Test seam: runs immediately after a job's cleanup claim is won.</summary>
    internal Action<Guid>? AfterJobClaimForTesting { get; set; }

    private string MediaActivationRoot =>
        Path.GetDirectoryName(Path.GetDirectoryName(paths.CandidateMedia(ProbeId)))!;

    private string MediaRecoveryRoot =>
        Path.GetDirectoryName(Path.GetDirectoryName(paths.PreviousMedia(ProbeId)))!;

    internal async Task<SelfHostedOrphanSweepResult> SweepAsync(CancellationToken ct)
    {
        var cutoffUtc = (clock.GetUtcNow() - options.Value.OrphanSafetyAge).UtcDateTime;
        var ambiguous = 0;
        var work = new Dictionary<Guid, JobSweepWork>();
        CollectDatabaseActivationRoot(cutoffUtc, work, ref ambiguous, ct);
        CollectMediaActivationRoot(cutoffUtc, work, ref ambiguous, ct);
        CollectDatabaseRecoveryRoot(cutoffUtc, work, ref ambiguous, ct);
        CollectMediaRecoveryRoot(cutoffUtc, work, ref ambiguous, ct);

        var removed = 0;
        foreach (var (jobId, items) in work.OrderBy(pair => pair.Key))
        {
            ct.ThrowIfCancellationRequested();
            var claim = await TryClaimAsync(jobId, ct);
            if (claim is null)
            {
                continue; // the job is active or a concurrent retry won the claim race
            }

            try
            {
                AfterJobClaimForTesting?.Invoke(jobId);
                foreach (var deletion in items.Deletions)
                {
                    ct.ThrowIfCancellationRequested();
                    removed += deletion();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one job's leftovers.");
            }
            finally
            {
                await claim.ReleaseAsync(db, clock, logger);
            }
        }

        return new SelfHostedOrphanSweepResult(removed, ambiguous);
    }

    /// <summary>
    /// Fresh per-job ownership check immediately before deletion. A missing job
    /// row can never be retried, so it needs no claim. A terminal job is claimed
    /// with a version-guarded CAS; a non-terminal or actively leased job is left
    /// alone. A lost CAS (retry or other writer won) skips the job.
    /// </summary>
    private async Task<JobSweepClaim?> TryClaimAsync(Guid jobId, CancellationToken ct)
    {
        BeforeJobClaimForTesting?.Invoke(jobId);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await db.MigrationJobRecords.AsNoTracking()
                .SingleOrDefaultAsync(job => job.Id == jobId, ct);
            if (snapshot is null)
            {
                return JobSweepClaim.Absent(jobId);
            }

            if (!MigrationJobTransitions.IsTerminal((MigrationJobState)snapshot.State))
            {
                return null;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var token = MigrationJobCleanupClaim.NewToken();
            var updated = await db.MigrationJobRecords
                .Where(job => job.Id == jobId
                    && job.Version == snapshot.Version
                    && (job.State == (int)MigrationJobState.Completed
                        || job.State == (int)MigrationJobState.Failed
                        || job.State == (int)MigrationJobState.Cancelled
                        || job.State == (int)MigrationJobState.Expired)
                    && (job.MigrationLeaseToken == null
                        || job.LeaseExpiresAtUtc == null
                        || job.LeaseExpiresAtUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.MigrationLeaseToken, token)
                    .SetProperty(job => job.LeaseExpiresAtUtc, now.Add(MigrationJobCleanupClaim.DefaultDuration))
                    .SetProperty(job => job.UpdatedAtUtc, now)
                    .SetProperty(job => job.Version, job => job.Version + 1), ct);
            if (updated == 1)
            {
                return JobSweepClaim.Claimed(jobId, token);
            }
        }

        return null;
    }

    private void CollectDatabaseActivationRoot(
        DateTime cutoffUtc,
        Dictionary<Guid, JobSweepWork> work,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = paths.JournalRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        paths.VerifyDatabasePath(root);
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                paths.VerifyDatabasePath(directory);
                if (!TryReadJobDirectory(directory, out var jobId))
                {
                    logger.LogWarning("Activation orphan sweep left an unrecognized directory under the activation root.");
                    ambiguous++;
                    continue;
                }

                if (HasUnresolvedJournal(jobId))
                {
                    continue;
                }

                var deletions = new List<Func<int>>();
                foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
                {
                    paths.VerifyDatabasePath(file);
                    var name = Path.GetFileName(file);
                    if (!IsDisposableActivationLeftover(name)
                        || File.GetLastWriteTimeUtc(file) >= cutoffUtc)
                    {
                        continue;
                    }

                    deletions.Add(() =>
                    {
                        paths.VerifyDatabasePath(file);
                        if (!File.Exists(file))
                        {
                            return 0;
                        }

                        if (name == "candidate.db" || name is "candidate.db-wal" or "candidate.db-shm")
                        {
                            SelfHostedSqliteFile.ClearPoolFor(Path.Combine(directory, "candidate.db"));
                        }

                        File.Delete(file);
                        return 1;
                    });
                }

                foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
                {
                    paths.VerifyDatabasePath(child);
                    logger.LogWarning("Activation orphan sweep left an unrecognized directory inside an activation job area.");
                }

                if (!HasResolvedJournal(jobId) && Directory.GetLastWriteTimeUtc(directory) < cutoffUtc)
                {
                    deletions.Add(() =>
                    {
                        paths.VerifyDatabasePath(directory);
                        if (Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            return 0;
                        }

                        Directory.Delete(directory);
                        return 0;
                    });
                }

                AddWork(work, jobId, deletions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one activation directory.");
            }
        }
    }

    private void CollectMediaActivationRoot(
        DateTime cutoffUtc,
        Dictionary<Guid, JobSweepWork> work,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = MediaActivationRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        paths.VerifyMediaPath(root);
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                paths.VerifyMediaPath(directory);
                if (!TryReadJobDirectory(directory, out var jobId))
                {
                    logger.LogWarning("Activation orphan sweep left an unrecognized directory under the media activation root.");
                    ambiguous++;
                    continue;
                }

                if (HasUnresolvedJournal(jobId))
                {
                    continue;
                }

                var deletions = new List<Func<int>>();
                var candidate = paths.CandidateMedia(jobId);
                paths.VerifyMediaPath(candidate);
                if (Directory.Exists(candidate)
                    && TransferPathResolver.NewestWriteTimeUtc(candidate) < cutoffUtc)
                {
                    deletions.Add(() =>
                    {
                        VerifyTree(candidate, database: false, ct);
                        DeleteTreeVerified(candidate, database: false, ct);
                        return 1;
                    });
                }

                foreach (var child in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    paths.VerifyMediaPath(child);
                    if (SamePath(child, candidate))
                    {
                        continue;
                    }

                    logger.LogWarning("Activation orphan sweep left an unrecognized entry in a media activation job area.");
                }

                if (!HasResolvedJournal(jobId) && Directory.GetLastWriteTimeUtc(directory) < cutoffUtc)
                {
                    deletions.Add(() =>
                    {
                        paths.VerifyMediaPath(directory);
                        if (Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            return 0;
                        }

                        Directory.Delete(directory);
                        return 0;
                    });
                }

                AddWork(work, jobId, deletions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one media activation directory.");
            }
        }
    }

    private void CollectDatabaseRecoveryRoot(
        DateTime cutoffUtc,
        Dictionary<Guid, JobSweepWork> work,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = paths.RecoveryRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        paths.VerifyDatabasePath(root);
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                paths.VerifyDatabasePath(directory);
                if (!TryReadJobDirectory(directory, out var jobId))
                {
                    logger.LogWarning("Activation orphan sweep left an unrecognized directory under the recovery root.");
                    ambiguous++;
                    continue;
                }

                if (HasUnresolvedJournal(jobId) || manifests.HasDeletionMarker(jobId))
                {
                    continue;
                }

                var state = ReadManifestState(jobId, ref ambiguous);
                if (state != ManifestState.Absent)
                {
                    continue; // valid copies belong to the guarded cleanup; corrupt ones to an operator
                }

                if (manifests.MaterialExists(jobId))
                {
                    // Retained material without a manifest cannot be proven
                    // unreferenced; never let a sweep destroy it.
                    logger.LogWarning("Activation orphan sweep left recovery material without a valid manifest for operator inspection.");
                    ambiguous++;
                    continue;
                }

                if (TransferPathResolver.NewestWriteTimeUtc(directory) < cutoffUtc)
                {
                    AddWork(work, jobId, [() =>
                    {
                        paths.VerifyDatabasePath(directory);
                        manifests.DeleteUnusedPlan(jobId);
                        return 1;
                    }]);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one recovery directory.");
            }
        }
    }

    private void CollectMediaRecoveryRoot(
        DateTime cutoffUtc,
        Dictionary<Guid, JobSweepWork> work,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = MediaRecoveryRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        paths.VerifyMediaPath(root);
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                paths.VerifyMediaPath(directory);
                if (!TryReadJobDirectory(directory, out var jobId))
                {
                    logger.LogWarning("Activation orphan sweep left an unrecognized directory under the media recovery root.");
                    ambiguous++;
                    continue;
                }

                if (HasUnresolvedJournal(jobId) || manifests.HasDeletionMarker(jobId))
                {
                    continue;
                }

                var state = ReadManifestState(jobId, ref ambiguous);
                if (state != ManifestState.Absent)
                {
                    continue;
                }

                if (manifests.MaterialExists(jobId))
                {
                    logger.LogWarning("Activation orphan sweep left retained media without a valid manifest for operator inspection.");
                    ambiguous++;
                    continue;
                }

                var old = TransferPathResolver.NewestWriteTimeUtc(directory) < cutoffUtc;
                var deletions = new List<Func<int>>();
                foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
                {
                    paths.VerifyMediaPath(file);
                    if (!Path.GetFileName(file).EndsWith(".tmp", StringComparison.Ordinal))
                    {
                        logger.LogWarning("Activation orphan sweep left an unrecognized entry in a media recovery job area.");
                        continue;
                    }

                    if (!old)
                    {
                        continue;
                    }

                    deletions.Add(() =>
                    {
                        paths.VerifyMediaPath(file);
                        if (!File.Exists(file))
                        {
                            return 0;
                        }

                        File.Delete(file);
                        return 1;
                    });
                }

                foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
                {
                    paths.VerifyMediaPath(child);
                    logger.LogWarning("Activation orphan sweep left an unrecognized entry in a media recovery job area.");
                }

                if (old)
                {
                    deletions.Add(() =>
                    {
                        paths.VerifyMediaPath(directory);
                        if (Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            return 0;
                        }

                        Directory.Delete(directory);
                        return 0;
                    });
                }

                AddWork(work, jobId, deletions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one media recovery directory.");
            }
        }
    }

    private enum ManifestState
    {
        Absent,
        Valid,
        Corrupt,
    }

    /// <summary>
    /// Only a manifest file that does not exist counts as absent. A directory
    /// where the manifest file belongs, an unreadable file, truncated or
    /// invalid JSON, a checksum or validation failure, or a job-id mismatch are
    /// all corrupt: the directory is left untouched and logged once per pass.
    /// </summary>
    private ManifestState ReadManifestState(Guid jobId, ref int ambiguous)
    {
        var path = paths.RecoveryManifest(jobId);
        paths.VerifyDatabasePath(path);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return ManifestState.Absent;
        }

        if (File.Exists(path))
        {
            try
            {
                if (manifests.Read(jobId) is not null)
                {
                    return ManifestState.Valid;
                }
            }
            catch (Exception exception) when (exception is MigrationActivationException or IOException or UnauthorizedAccessException)
            {
            }
        }

        logger.LogWarning("Activation orphan sweep left a corrupt recovery manifest for operator inspection.");
        ambiguous++;
        return ManifestState.Corrupt;
    }

    private bool HasUnresolvedJournal(Guid jobId)
    {
        try
        {
            return journals.Read(jobId) is not null;
        }
        catch (MigrationActivationException)
        {
            // A corrupt unresolved journal protects the area until an operator
            // or the startup reconciler decides the generation.
            return true;
        }
    }

    private bool HasResolvedJournal(Guid jobId)
    {
        try
        {
            return journals.ReadResolved(jobId) is not null;
        }
        catch (MigrationActivationException)
        {
            return true;
        }
    }

    private static bool TryReadJobDirectory(string directory, out Guid jobId)
    {
        jobId = Guid.Empty;
        var name = Path.GetFileName(directory);
        return Guid.TryParseExact(name, "N", out jobId)
            && jobId != Guid.Empty
            && string.Equals(name, jobId.ToString("N"), StringComparison.Ordinal);
    }

    private static bool IsDisposableActivationLeftover(string name)
    {
        if (name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return true;
        }

        return name is "candidate.db" or "candidate.db-wal" or "candidate.db-shm"
            or "portable-data.json.tmp" or "candidate.finalized.json";
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void AddWork(Dictionary<Guid, JobSweepWork> work, Guid jobId, List<Func<int>> deletions)
    {
        if (deletions.Count == 0)
        {
            return;
        }

        if (!work.TryGetValue(jobId, out var entry))
        {
            entry = new JobSweepWork();
            work[jobId] = entry;
        }

        entry.Deletions.AddRange(deletions);
    }

    private void VerifyTree(string directory, bool database, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(Verify(directory, database)).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var verified = Verify(entry, database);
            if (Directory.Exists(verified))
            {
                VerifyTree(verified, database, ct);
            }
        }
    }

    private void DeleteTreeVerified(string directory, bool database, CancellationToken ct)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(Verify(directory, database)).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var verified = Verify(entry, database);
            if (Directory.Exists(verified))
            {
                DeleteTreeVerified(verified, database, ct);
            }
            else
            {
                File.Delete(verified);
            }
        }

        Directory.Delete(Verify(directory, database));
    }

    private string Verify(string path, bool database)
    {
        if (database)
        {
            paths.VerifyDatabasePath(path);
        }
        else
        {
            paths.VerifyMediaPath(path);
        }

        return Path.GetFullPath(path);
    }

    private sealed class JobSweepWork
    {
        internal List<Func<int>> Deletions { get; } = [];
    }

    private sealed class JobSweepClaim
    {
        private readonly Guid _jobId;
        private readonly string? _token;

        private JobSweepClaim(Guid jobId, string? token)
        {
            _jobId = jobId;
            _token = token;
        }

        internal static JobSweepClaim Absent(Guid jobId) => new(jobId, token: null);

        internal static JobSweepClaim Claimed(Guid jobId, string token) => new(jobId, token);

        internal async Task ReleaseAsync(
            NostosDbContext db,
            TimeProvider clock,
            ILogger logger)
        {
            if (_token is null)
            {
                return;
            }

            try
            {
                var now = clock.GetUtcNow().UtcDateTime;
                await db.MigrationJobRecords
                    .Where(job => job.Id == _jobId && job.MigrationLeaseToken == _token)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.MigrationLeaseToken, (string?)null)
                        .SetProperty(job => job.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(job => job.UpdatedAtUtc, now)
                        .SetProperty(job => job.Version, job => job.Version + 1), CancellationToken.None);
            }
            catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
            {
                logger.LogWarning(exception,
                    "Activation orphan sweep could not release its cleanup claim; it expires on its own.");
            }
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>Observable result of one orphan sweep pass.</summary>
internal sealed record SelfHostedOrphanSweepResult(int RemovedEntries, int LeftAmbiguous);

/// <summary>
/// Activation orphan sweep (issue #681, Slice 10). Removes only leftovers that
/// are provably unreferenced: the job directory has no unresolved activation
/// journal, its job is terminal or absent, the entry is older than the safety
/// age, and every path resolves under the configured activation/recovery roots
/// without following a symlink or reparse point out of the tree. Anything
/// ambiguous - a corrupt manifest, retained material without a manifest, an
/// unrecognized entry, a protected job - is left in place and logged. The
/// guarded expiry cleanup owns valid recovery copies and deletion markers; this
/// sweep never touches them.
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

    private string MediaActivationRoot =>
        Path.GetDirectoryName(Path.GetDirectoryName(paths.CandidateMedia(ProbeId)))!;

    private string MediaRecoveryRoot =>
        Path.GetDirectoryName(Path.GetDirectoryName(paths.PreviousMedia(ProbeId)))!;

    internal async Task<SelfHostedOrphanSweepResult> SweepAsync(CancellationToken ct)
    {
        var cutoffUtc = (clock.GetUtcNow() - options.Value.OrphanSafetyAge).UtcDateTime;
        var protectedJobs = await LoadProtectedJobsAsync(clock.GetUtcNow().UtcDateTime, ct);
        var ambiguous = 0;
        var removed = 0;
        removed += SweepDatabaseActivationRoot(cutoffUtc, protectedJobs, ref ambiguous, ct);
        removed += SweepMediaActivationRoot(cutoffUtc, protectedJobs, ref ambiguous, ct);
        removed += SweepDatabaseRecoveryRoot(cutoffUtc, protectedJobs, ref ambiguous, ct);
        removed += SweepMediaRecoveryRoot(cutoffUtc, protectedJobs, ref ambiguous, ct);
        return new SelfHostedOrphanSweepResult(removed, ambiguous);
    }

    /// <summary>
    /// A job protects its activation area while it is non-terminal or holds a
    /// live lease. Terminal jobs that were never activated (Failed, Cancelled,
    /// Expired) still own prepared staging but no activation leftovers, so only
    /// their generated candidate area is eligible.
    /// </summary>
    private async Task<HashSet<Guid>> LoadProtectedJobsAsync(DateTime nowUtc, CancellationToken ct)
    {
        var active = await db.MigrationJobRecords.AsNoTracking()
            .Where(job => job.State < (int)MigrationJobState.Completed
                || (job.MigrationLeaseToken != null
                    && job.LeaseExpiresAtUtc != null
                    && job.LeaseExpiresAtUtc > nowUtc))
            .Select(job => job.Id)
            .ToListAsync(ct);
        return active.ToHashSet();
    }

    private int SweepDatabaseActivationRoot(
        DateTime cutoffUtc,
        HashSet<Guid> protectedJobs,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = paths.JournalRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        paths.VerifyDatabasePath(root);
        var removed = 0;
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

                if (HasUnresolvedJournal(jobId) || protectedJobs.Contains(jobId))
                {
                    continue;
                }

                removed += RemoveActivationJobLeftovers(directory, jobId, cutoffUtc, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one activation directory.");
            }
        }

        return removed;
    }

    private int RemoveActivationJobLeftovers(string directory, Guid jobId, DateTime cutoffUtc, CancellationToken ct)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            paths.VerifyDatabasePath(file);
            var name = Path.GetFileName(file);
            if (!IsDisposableActivationLeftover(name))
            {
                continue; // resolved journals and rebuild markers are durable records
            }

            if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
            {
                continue;
            }

            if (name == "candidate.db" || name is "candidate.db-wal" or "candidate.db-shm")
            {
                SelfHostedSqliteFile.ClearPoolFor(Path.Combine(directory, "candidate.db"));
            }

            File.Delete(file);
            removed++;
        }

        foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            paths.VerifyDatabasePath(child);
            logger.LogWarning("Activation orphan sweep left an unrecognized directory inside an activation job area.");
        }

        if (!HasResolvedJournal(jobId) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            paths.VerifyDatabasePath(directory);
            Directory.Delete(directory);
        }

        return removed;
    }

    private int SweepMediaActivationRoot(
        DateTime cutoffUtc,
        HashSet<Guid> protectedJobs,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = MediaActivationRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        paths.VerifyMediaPath(root);
        var removed = 0;
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

                if (HasUnresolvedJournal(jobId) || protectedJobs.Contains(jobId))
                {
                    continue;
                }

                var candidate = paths.CandidateMedia(jobId);
                paths.VerifyMediaPath(candidate);
                if (Directory.Exists(candidate)
                    && TransferPathResolver.NewestWriteTimeUtc(candidate) < cutoffUtc)
                {
                    VerifyTree(candidate, database: false, ct);
                    DeleteTreeVerified(candidate, database: false, ct);
                    removed++;
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

                if (!HasResolvedJournal(jobId) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    paths.VerifyMediaPath(directory);
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one media activation directory.");
            }
        }

        return removed;
    }

    private int SweepDatabaseRecoveryRoot(
        DateTime cutoffUtc,
        HashSet<Guid> protectedJobs,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = paths.RecoveryRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        paths.VerifyDatabasePath(root);
        var removed = 0;
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

                if (protectedJobs.Contains(jobId) || HasUnresolvedJournal(jobId) || manifests.HasDeletionMarker(jobId))
                {
                    continue;
                }

                if (ReadManifestOrAmbiguous(jobId, ref ambiguous) is not null)
                {
                    continue; // the guarded expiry cleanup owns valid copies
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
                    manifests.DeleteUnusedPlan(jobId);
                    removed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one recovery directory.");
            }
        }

        return removed;
    }

    private int SweepMediaRecoveryRoot(
        DateTime cutoffUtc,
        HashSet<Guid> protectedJobs,
        ref int ambiguous,
        CancellationToken ct)
    {
        var root = MediaRecoveryRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        paths.VerifyMediaPath(root);
        var removed = 0;
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

                if (protectedJobs.Contains(jobId) || HasUnresolvedJournal(jobId) || manifests.HasDeletionMarker(jobId))
                {
                    continue;
                }

                if (ReadManifestOrAmbiguous(jobId, ref ambiguous) is not null)
                {
                    continue;
                }

                if (manifests.MaterialExists(jobId))
                {
                    logger.LogWarning("Activation orphan sweep left retained media without a valid manifest for operator inspection.");
                    ambiguous++;
                    continue;
                }

                if (TransferPathResolver.NewestWriteTimeUtc(directory) < cutoffUtc)
                {
                    foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        paths.VerifyMediaPath(file);
                        if (Path.GetFileName(file).EndsWith(".tmp", StringComparison.Ordinal))
                        {
                            File.Delete(file);
                            removed++;
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        paths.VerifyMediaPath(directory);
                        Directory.Delete(directory);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TransferPathException)
            {
                logger.LogWarning(exception, "Activation orphan sweep will retry one media recovery directory.");
            }
        }

        return removed;
    }

    private SelfHostedRecoveryManifest? ReadManifestOrAmbiguous(Guid jobId, ref int ambiguous)
    {
        try
        {
            return manifests.Read(jobId);
        }
        catch (MigrationActivationException)
        {
            logger.LogWarning("Activation orphan sweep left a corrupt recovery manifest for operator inspection.");
            ambiguous++;
            return null;
        }
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
}

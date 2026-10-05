namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Small crash-repeatable recovery unit. Validate is read-only and is invoked for
/// ALL steps before ANY Execute. Execute is synchronous under a proven exclusive
/// lease, uses only host-derived paths, and must tolerate its preceding rename
/// having succeeded without a following journal write. Later slices can register
/// job projection/verification steps; imported-state verification and cutover
/// admission belong to the activation coordinator, not startup filesystem repair.
/// </summary>
internal interface ISelfHostedActivationRecoveryStep
{
    int Order { get; }
    void Validate(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action);
    void Execute(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action);
}

/// <summary>
/// Restores one complete component without deleting data. Incoming live objects
/// are quarantined back to their now-vacant candidate location, then previous
/// objects are restored. The durable phase selects the direction; existence only
/// validates a protocol-reachable position or a partially repeated rollback.
/// </summary>
internal sealed class SelfHostedActivationComponentStep(SelfHostedActivationPaths paths, bool database, Action? afterRenameForTesting = null)
    : ISelfHostedActivationRecoveryStep
{
    public int Order => database ? 20 : 10;

    public void Validate(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action)
    {
        var (live, candidate, previous) = Locations(journal.JobId);
        Verify(live); Verify(candidate); Verify(previous);
        var state = State(live, candidate, previous);
        if (action == SelfHostedRecoveryAction.RollForwardCandidate)
        {
            // 5 = active candidate + retained previous; 1 = active candidate
            // after future retention cleanup; 6 = rename still to be finished.
            if (state is not (5 or 1 or 6)) throw InvalidLayout();
        }
        else if (action == SelfHostedRecoveryAction.RollBackOriginal)
        {
            if (state is not (3 or 6 or 5)) throw InvalidLayout();
            if (journal.Phase != SelfHostedActivationPhase.RollingBack)
            {
                var possible = journal.Phase switch
                {
                    SelfHostedActivationPhase.CutoverPrepared => database ? new[] { 3 } : [3, 6],
                    SelfHostedActivationPhase.PreviousMediaRetained => database ? new[] { 3, 6 } : [6],
                    SelfHostedActivationPhase.PreviousDatabaseRetained => database ? new[] { 6 } : [6, 5],
                    SelfHostedActivationPhase.CandidateMediaActivated => database ? new[] { 6, 5 } : [5],
                    SelfHostedActivationPhase.CandidateDatabaseActivated or SelfHostedActivationPhase.PostActivationVerified => new[] { 5 },
                    _ => Array.Empty<int>(),
                };
                if (!possible.Contains(state)) throw InvalidLayout();
            }
        }
        if (database && action is SelfHostedRecoveryAction.RollBackOriginal or SelfHostedRecoveryAction.RollForwardCandidate)
        {
            // Retained DBs must be clean. Never guess away a WAL that might carry
            // original rows. Live post-verification sidecars travel with candidate.
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                foreach (var root in new[] { live, candidate, previous }) Verify(root + suffix);
                if (File.Exists(previous + suffix) && new FileInfo(previous + suffix).Length > 0)
                    throw InvalidLayout();
                if (File.Exists(live + suffix) && File.Exists(candidate + suffix)) throw InvalidLayout();
                if (state == 6 && File.Exists(live + suffix)) throw InvalidLayout();
            }
        }
    }

    public void Execute(SelfHostedActivationJournal journal, SelfHostedRecoveryAction action)
    {
        var (live, candidate, previous) = Locations(journal.JobId);
        // A concurrent status read that raced a vacant live path can leave a
        // zero-length SQLite artifact. It is not a generation; it must never
        // occupy the rename destination or be mistaken for a live database.
        if (database) RemoveEmptyDatabaseArtifact(live);
        if (action == SelfHostedRecoveryAction.RollBackOriginal && Exists(previous))
        {
            // Quarantine SQLite sidecars before restoring the original DB, even
            // when a preceding crash already quarantined the database itself.
            if (database)
                foreach (var suffix in new[] { "-wal", "-shm" })
                    MoveIfPresent(live + suffix, candidate + suffix);
            if (Exists(live)) Move(live, candidate);
            Move(previous, live);
        }
        else if (action == SelfHostedRecoveryAction.RollForwardCandidate)
        {
            if (!Exists(live)) Move(candidate, live);
            if (database)
                foreach (var suffix in new[] { "-wal", "-shm" }) MoveIfPresent(candidate + suffix, live + suffix);
        }
    }

    private (string, string, string) Locations(Guid id) => database
        ? (paths.LiveDatabase, paths.CandidateDatabase(id), paths.PreviousDatabase(id))
        : (paths.LiveMedia, paths.CandidateMedia(id), paths.PreviousMedia(id));
    /// <summary>
    /// A database exists only when its file carries bytes: SQLite materializes
    /// a zero-length file when a reader opens a missing path, and that artifact
    /// is never a library generation.
    /// </summary>
    private bool Exists(string path) => database
        ? File.Exists(path) && new FileInfo(path).Length > 0
        : Directory.Exists(path);
    private int State(string live, string candidate, string previous)
    {
        // Reject a file where a directory belongs, and vice versa.
        foreach (var path in new[] { live, candidate, previous })
            if (database ? Directory.Exists(path) : File.Exists(path)) throw InvalidLayout();
        return (Exists(live) ? 1 : 0) | (Exists(candidate) ? 2 : 0) | (Exists(previous) ? 4 : 0);
    }
    private void Verify(string path)
    {
        if (database) paths.VerifyDatabasePath(path); else paths.VerifyMediaPath(path);
    }
    private void MoveIfPresent(string source, string target)
    {
        if (File.Exists(source)) Move(source, target);
    }
    private void Move(string source, string target)
    {
        Verify(source); Verify(target);
        if (database) RenameDatabase(source, target);
        else ActivationFileSystem.Rename(source, target);
        afterRenameForTesting?.Invoke();
    }

    /// <summary>
    /// Renames a database file, retrying when the destination is only a
    /// zero-length SQLite artifact left by a concurrent reader that raced a
    /// vacated live path. A non-empty destination is never overwritten and
    /// propagates the ordinary layout error.
    /// </summary>
    internal static void RenameDatabase(string source, string target)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                ActivationFileSystem.Rename(source, target);
                return;
            }
            catch (IOException) when (RemoveEmptyDatabaseArtifact(target))
            {
            }
        }

        throw SelfHostedActivationPaths.Failure(
            "A database rename could not be completed because the destination stayed occupied.");
    }
    /// <summary>
    /// Removes a zero-length database artifact (and its empty sidecars) at a
    /// vacated live path. Returns true when something was removed.
    /// </summary>
    internal static bool RemoveEmptyDatabaseArtifact(string databasePath)
    {
        var removed = false;
        try
        {
            if (File.Exists(databasePath) && new FileInfo(databasePath).Length == 0)
            {
                File.Delete(databasePath);
                removed = true;
            }

            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = databasePath + suffix;
                if (File.Exists(sidecar) && new FileInfo(sidecar).Length == 0)
                {
                    File.Delete(sidecar);
                }
            }
        }
        catch (IOException)
        {
            // The owner's rename or another reader may hold it; the caller
            // fails closed through the ordinary layout error.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return removed;
    }

    private static MigrationActivationException InvalidLayout() => SelfHostedActivationPaths.Failure(
        "The activation component layout is inconsistent. Stop the host and follow the activation recovery guide.");
}

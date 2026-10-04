using System.Text;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Singleton, single-writer journal store. CandidatePrepared may be created before
/// maintenance; all other writes require the current exclusive lease from this
/// host's coordinator. Same-phase retries must carry identical protocol facts.
/// Writes use CreateNew, Flush(true), then a native atomic sibling rename. Linux
/// directory fsync is mandatory; see ActivationFileSystem for Windows power-loss
/// limits. Temp files never supersede an existing final journal. An initial orphan
/// temp means no published activation intent and is safe because no rename is
/// permitted before CutoverPrepared has been published.
/// </summary>
internal sealed class SelfHostedActivationJournalStore(
    SelfHostedActivationPaths paths, LibraryMaintenanceCoordinator maintenance)
{
    private readonly object _writer = new();
    internal Action? BeforePublishForTesting { get; set; }

    internal SelfHostedActivationJournal? Read(Guid id)
    {
        return ReadDocument(id, paths.Journal(id));
    }

    internal SelfHostedActivationJournal? ReadResolved(Guid id)
    {
        var journal = ReadDocument(id, paths.ResolvedJournal(id));
        if (journal is not null && journal.Phase is not (SelfHostedActivationPhase.Committed or SelfHostedActivationPhase.RolledBack))
            throw Corrupt();
        return journal;
    }

    private SelfHostedActivationJournal? ReadDocument(Guid id, string path)
    {
        paths.VerifyDatabasePath(path);
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw Corrupt();
            return null;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        paths.VerifyDatabasePath(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        SelfHostedActivationJournal journal;
        try { journal = SelfHostedActivationDocument.Decode<SelfHostedActivationJournal>(reader.ReadToEnd()); }
        catch (DecoderFallbackException) { throw Corrupt(); }
        catch (MigrationActivationException) { throw Corrupt(); }
        if (journal.JobId != id || SelfHostedActivationState.RecoveryAction(journal) == SelfHostedRecoveryAction.FailClosed)
            throw Corrupt();
        return journal;
    }

    internal IReadOnlyList<SelfHostedActivationJournal> ReadAll()
    {
        var root = paths.JournalRoot;
        if (!Directory.Exists(root))
        {
            if (File.Exists(root)) throw Corrupt();
            return [];
        }
        paths.VerifyDatabasePath(root);
        var result = new List<SelfHostedActivationJournal>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            paths.VerifyDatabasePath(directory);
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty
                || Path.GetFileName(directory) != id.ToString("N"))
                throw Corrupt();
            var journal = Read(id);
            if (journal is not null) result.Add(journal);
        }
        return result;
    }

    internal void Write(SelfHostedActivationJournal next, IAsyncDisposable? lease = null)
    {
        if (SelfHostedActivationState.RecoveryAction(next) == SelfHostedRecoveryAction.FailClosed) throw Corrupt();
        void WriteCore()
        {
            lock (_writer)
            {
                paths.Prepare(next.JobId);
                var current = Read(next.JobId);
                if (current is null)
                {
                    if (next.Phase != SelfHostedActivationPhase.CandidatePrepared)
                        throw new InvalidOperationException("A journal must begin at CandidatePrepared.");
                    if (File.Exists(paths.ResolvedJournal(next.JobId)) || Directory.Exists(paths.ResolvedJournal(next.JobId)))
                        throw new InvalidOperationException("This activation identifier has already been resolved.");
                }
                else
                {
                    // A phase transition must never change the generation identity
                    // or replacement admission facts, even under a valid lease.
                    if (current.OperationId != next.OperationId || current.DestinationRevision != next.DestinationRevision
                        || current.RetainPreviousLibrary != next.RetainPreviousLibrary || current.JournalVersion != next.JournalVersion)
                        throw new InvalidOperationException("Activation identity cannot change during a transition.");
                    if (current.Phase == next.Phase)
                    {
                        if (SelfHostedActivationDocument.Encode(current) != SelfHostedActivationDocument.Encode(next))
                            throw new InvalidOperationException("A same-phase retry must be identical.");
                        return;
                    }
                    SelfHostedActivationState.ValidateTransition(current.Phase, next.Phase);
                }
                var temporary = paths.TemporaryJournal(next.JobId, Guid.NewGuid());
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    paths.VerifyDatabasePath(temporary);
                    stream.Write(Encoding.UTF8.GetBytes(SelfHostedActivationDocument.Encode(next)));
                    stream.Flush(flushToDisk: true);
                }
                BeforePublishForTesting?.Invoke();
                paths.VerifyDatabasePath(temporary);
                paths.VerifyDatabasePath(paths.Journal(next.JobId));
                ActivationFileSystem.Rename(temporary, paths.Journal(next.JobId), overwrite: true);
            }
        }
        if (next.Phase == SelfHostedActivationPhase.CandidatePrepared && lease is null) WriteCore();
        else maintenance.WithExclusiveLease(lease, WriteCore);
    }

    /// <summary>
    /// Clears a crash-terminated attempt so a new attempt for the same job may
    /// begin. Only phases that can never have renamed a live component
    /// (<c>CandidatePrepared</c>, <c>ExclusiveEntered</c>,
    /// <c>DatabaseCheckpointed</c>) or a completed rollback
    /// (<c>RolledBack</c>, live or resolved) are clearable; retained previous
    /// material still on disk fails closed, and a resolved <c>Committed</c>
    /// journal is never cleared (it is resumed, not replayed). Runs outside the
    /// maintenance lease because it touches only control files.
    /// </summary>
    internal void PrepareForRetry(Guid id)
    {
        lock (_writer)
        {
            paths.Verify(id);
            var current = Read(id);
            var resolved = ReadResolved(id);
            if (current is not null && resolved is not null) throw Corrupt();
            var journal = current ?? resolved;
            if (journal is null) return;
            if (journal.JobId != id || journal.OperationId == Guid.Empty) throw Corrupt();
            if (journal.Phase is not (SelfHostedActivationPhase.CandidatePrepared
                or SelfHostedActivationPhase.ExclusiveEntered
                or SelfHostedActivationPhase.DatabaseCheckpointed
                or SelfHostedActivationPhase.RolledBack))
            {
                throw new InvalidOperationException(
                    "A non-terminal activation journal must be reconciled before a retry.");
            }

            if (File.Exists(paths.PreviousDatabase(id)) || Directory.Exists(paths.PreviousMedia(id)))
            {
                throw Corrupt();
            }

            var path = current is not null ? paths.Journal(id) : paths.ResolvedJournal(id);
            paths.VerifyDatabasePath(path);
            File.Delete(path);
            ActivationFileSystem.FlushDirectory(Path.GetDirectoryName(path)!);
        }
    }

    internal SelfHostedActivationJournal Advance(Guid id, SelfHostedActivationPhase phase, IAsyncDisposable lease)
    {
        SelfHostedActivationJournal? result = null;
        maintenance.WithExclusiveLease(lease, () =>
        {
            lock (_writer)
            {
                var current = Read(id) ?? throw Corrupt();
                result = current.Phase == phase ? current : current with { Phase = phase, UpdatedAtUtc = DateTimeOffset.UtcNow };
                Write(result, lease);
            }
        });
        return result!;
    }

    // Retain the complete envelope for later job finalization/operator inspection,
    // but never replay a historical commit against a future live generation.
    internal void MarkResolved(Guid id, IAsyncDisposable lease) => maintenance.WithExclusiveLease(lease, () =>
    {
        lock (_writer)
        {
            var current = Read(id);
            if (current is null && ReadResolved(id) is not null) return;
            if (current is not { Phase: SelfHostedActivationPhase.Committed or SelfHostedActivationPhase.RolledBack })
                throw new InvalidOperationException("Only a terminal journal may be resolved.");
            paths.VerifyDatabasePath(paths.ResolvedJournal(id));
            ActivationFileSystem.Rename(paths.Journal(id), paths.ResolvedJournal(id));
        }
    });

    private static MigrationActivationException Corrupt() => new(MigrationActivationErrorCodes.RecoveryCorrupt,
        "The activation journal is unsupported or corrupt. Stop the host and follow the activation recovery guide.");
}

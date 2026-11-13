using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.SavePipeline;

/// <summary>
/// Coordinates the pre-save domain guards and destination-revision guard
/// (issue #679 Slice 11): a save containing any portable change advances the
/// singleton <see cref="LibraryState.StateVersion"/> atomically, before the
/// portable rows are touched, in the same transaction.
///
/// Ambient transactions share one savepoint between the advance and the
/// portable DML, so a failed save rolls the advance back with its own rows and
/// clears the per-transaction "already advanced" marker. Without an ambient
/// transaction the owned transaction already covers both.
/// </summary>
internal sealed class PortableSavePipeline
{
    private static readonly HashSet<string> ReadingPositionProperties = new(StringComparer.Ordinal)
    {
        nameof(ReadingProgress.LastLocation),
        nameof(ReadingProgress.ProgressPercent),
        nameof(ReadingProgress.LastReadAt),
        nameof(ReadingProgress.PlaybackRate),
    };

    /// <summary>The transaction that already advanced the revision, if any.</summary>
    private IDbContextTransaction? _libraryRevisionTransaction;

    /// <summary>The authoritative revision advanced in the current transaction.</summary>
    private string? _libraryRevisionValue;

    /// <summary>The transaction whose portable saves <see cref="_libraryChangeHasMoreThanPosition"/> describes.</summary>
    private Guid? _libraryChangeTransactionId;

    /// <summary>True once a portable save in that transaction changed more than the reading position.</summary>
    private bool _libraryChangeHasMoreThanPosition;

    /// <summary>The last committed transaction already announced, so it is announced once.</summary>
    private Guid? _libraryChangeAnnouncedTransactionId;

    public int SaveChanges(
        NostosDbContext db,
        bool acceptAllChangesOnSuccess,
        Func<bool, int> commitSave)
    {
        MissingWorkAssigner.Assign(db);
        NoteUpdateStamper.Stamp(db.ChangeTracker);
        PhysicalBookFileGuard.EnsurePhysicalBooksHaveNoFile(db.ChangeTracker);
        if (!HasPortableChanges(db.ChangeTracker))
            return commitSave(acceptAllChangesOnSuccess);

        if (db.Database.CurrentTransaction is not null)
        {
            return SavePortableChangesWithSavepoint(db, acceptAllChangesOnSuccess, commitSave);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.Execute(() =>
        {
            using var owned = db.Database.BeginTransaction();
            try
            {
                LibraryRevision.AdvanceAndGet(db);
                NoteLibraryChangeKind(db);
                var result = commitSave(acceptAllChangesOnSuccess);
                owned.Commit();
                return result;
            }
            catch
            {
                owned.Rollback();
                throw;
            }
        });
    }

    public async Task<int> SaveChangesAsync(
        NostosDbContext db,
        bool acceptAllChangesOnSuccess,
        Func<bool, CancellationToken, Task<int>> commitSave,
        CancellationToken cancellationToken)
    {
        await MissingWorkAssigner.AssignAsync(db, cancellationToken);
        NoteUpdateStamper.Stamp(db.ChangeTracker);
        PhysicalBookFileGuard.EnsurePhysicalBooksHaveNoFile(db.ChangeTracker);
        if (!HasPortableChanges(db.ChangeTracker))
            return await commitSave(acceptAllChangesOnSuccess, cancellationToken);

        if (db.Database.CurrentTransaction is not null)
        {
            return await SavePortableChangesWithSavepointAsync(
                db,
                acceptAllChangesOnSuccess,
                commitSave,
                cancellationToken);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var owned = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await LibraryRevision.AdvanceAndGetAsync(db, cancellationToken);
                NoteLibraryChangeKind(db);
                var result = await commitSave(acceptAllChangesOnSuccess, cancellationToken);
                await owned.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await owned.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    /// <summary>
    /// True when the current transaction already contains one revision advance,
    /// so the next portable save in it must not advance again. The marker is
    /// context-local and is cleared when a portable save rolls back to its
    /// savepoint.
    /// </summary>
    internal bool TryGetCachedLibraryRevision(NostosDbContext db, out string value)
    {
        if (_libraryRevisionValue is not null
            && db.Database.CurrentTransaction is { } current
            && ReferenceEquals(_libraryRevisionTransaction, current))
        {
            value = _libraryRevisionValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Records that the revision was advanced in <paramref name="transaction"/>.</summary>
    internal void MarkLibraryRevisionAdvanced(IDbContextTransaction? transaction, string value)
    {
        _libraryRevisionTransaction = transaction;
        _libraryRevisionValue = value;
    }

    /// <summary>Forgets a cached advance whose savepoint was rolled back.</summary>
    internal void ClearLibraryRevisionCache()
    {
        _libraryRevisionTransaction = null;
        _libraryRevisionValue = null;
    }

    /// <summary>
    /// The same record, for a write that bypasses the tracker and so has to say
    /// what it changed. Without it the feed reports a full library change.
    /// </summary>
    internal void NoteLibraryChangeKind(NostosDbContext db, bool positionOnly)
    {
        if (db.Database.CurrentTransaction?.TransactionId is not { } transactionId)
            return;

        if (_libraryChangeTransactionId != transactionId)
        {
            _libraryChangeTransactionId = transactionId;
            _libraryChangeHasMoreThanPosition = false;
        }

        if (!positionOnly)
            _libraryChangeHasMoreThanPosition = true;
    }

    /// <summary>
    /// For <see cref="LibraryChangeCommitInterceptor"/>: true, once, when the
    /// committed transaction <paramref name="transactionId"/> advanced the
    /// library revision. A transaction that advanced it without a tracked
    /// portable save (a bulk operation) is never reported as position-only.
    /// </summary>
    internal bool TryTakeCommittedLibraryChange(Guid transactionId, out LibraryChange change)
    {
        change = default;
        if (_libraryRevisionValue is null
            || _libraryRevisionTransaction?.TransactionId != transactionId
            || _libraryChangeAnnouncedTransactionId == transactionId)
        {
            return false;
        }

        change = new LibraryChange(
            ProgressOnly: _libraryChangeTransactionId == transactionId && !_libraryChangeHasMoreThanPosition,
            Revision: _libraryRevisionValue);
        _libraryChangeAnnouncedTransactionId = transactionId;
        _libraryChangeTransactionId = null;
        _libraryChangeHasMoreThanPosition = false;
        return true;
    }

    /// <summary>
    /// True when this save mutates portable user-owned state. The portable set
    /// comes from <see cref="PortableEntitySet"/>, the same source the
    /// completeness inventory asserts parity with. The decision never depends
    /// on <see cref="LibraryState"/>'s tracked state.
    /// </summary>
    internal static bool HasPortableChanges(ChangeTracker changeTracker) =>
        changeTracker.Entries().Any(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && PortableEntitySet.IsPortableType(entry.Metadata.ClrType));

    /// <summary>
    /// Records, for the change feed, whether the portable save about to run
    /// moves the reading position only. Called inside the transaction that
    /// advanced the revision.
    /// </summary>
    private void NoteLibraryChangeKind(NostosDbContext db) => NoteLibraryChangeKind(
        db,
        db.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && PortableEntitySet.IsPortableType(entry.Metadata.ClrType))
            .All(entry => entry.State == EntityState.Modified
                && entry.Metadata.ClrType == typeof(ReadingProgress)
                && entry.Properties.Where(p => p.IsModified).All(p => ReadingPositionProperties.Contains(p.Metadata.Name))));

    /// <summary>
    /// Ambient-transaction portable save: the revision advance and EF's
    /// portable DML run inside one savepoint created before the advance. A
    /// failure rolls both back to that savepoint and clears the cached
    /// "advanced" value, so a later successful save in the same transaction
    /// advances again and no advance can be committed for rolled-back work.
    /// Providers without savepoint support fail closed.
    /// </summary>
    private int SavePortableChangesWithSavepoint(
        NostosDbContext db,
        bool acceptAllChangesOnSuccess,
        Func<bool, int> commitSave)
    {
        var ambient = db.Database.CurrentTransaction!;
        var wasCached = TryGetCachedLibraryRevision(db, out _);
        var savepoint = NewRevisionSavepointName();
        CreateRevisionSavepoint(ambient, savepoint);
        try
        {
            LibraryRevision.AdvanceAndGet(db);
            NoteLibraryChangeKind(db);
            var result = commitSave(acceptAllChangesOnSuccess);
            ambient.ReleaseSavepoint(savepoint);
            return result;
        }
        catch
        {
            RollbackToRevisionSavepoint(ambient, savepoint);
            if (!wasCached)
            {
                ClearLibraryRevisionCache();
            }

            throw;
        }
    }

    private async Task<int> SavePortableChangesWithSavepointAsync(
        NostosDbContext db,
        bool acceptAllChangesOnSuccess,
        Func<bool, CancellationToken, Task<int>> commitSave,
        CancellationToken cancellationToken)
    {
        var ambient = db.Database.CurrentTransaction!;
        var wasCached = TryGetCachedLibraryRevision(db, out _);
        var savepoint = NewRevisionSavepointName();
        try
        {
            await ambient.CreateSavepointAsync(savepoint, cancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            throw LibraryRevisionException.SavepointUnsupported(exception);
        }

        try
        {
            await LibraryRevision.AdvanceAndGetAsync(db, cancellationToken);
            NoteLibraryChangeKind(db);
            var result = await commitSave(acceptAllChangesOnSuccess, cancellationToken);
            await ambient.ReleaseSavepointAsync(savepoint, cancellationToken);
            return result;
        }
        catch
        {
            try
            {
                await ambient.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                await ambient.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            }
            catch
            {
                // The provider may already have unwound the transaction; the
                // original failure is what matters.
            }

            if (!wasCached)
            {
                ClearLibraryRevisionCache();
            }

            throw;
        }
    }

    private static string NewRevisionSavepointName() =>
        "nostos_library_revision_" + Guid.NewGuid().ToString("N");

    private static void CreateRevisionSavepoint(IDbContextTransaction transaction, string savepoint)
    {
        try
        {
            transaction.CreateSavepoint(savepoint);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            throw LibraryRevisionException.SavepointUnsupported(exception);
        }
    }

    private static void RollbackToRevisionSavepoint(IDbContextTransaction transaction, string savepoint)
    {
        try
        {
            transaction.RollbackToSavepoint(savepoint);
            transaction.ReleaseSavepoint(savepoint);
        }
        catch
        {
            // The provider may already have unwound the transaction; the
            // original failure is what matters.
        }
    }
}

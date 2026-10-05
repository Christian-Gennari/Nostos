using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Nostos.Backend.Data;

/// <summary>
/// The one authoritative writer of the live library revision
/// (<see cref="Data.Models.LibraryState.StateVersion"/>), issue #679 Slice 11.
///
/// <para><b>Why.</b> Activation compares the opaque destination revision
/// captured at import creation with the revision at activation time. The
/// revision must therefore change on <em>every</em> create, update or delete of
/// portable user-owned state — including content-only edits that never passed
/// through the legacy explicit version bump.</para>
///
/// <para><b>How.</b> <see cref="NostosDbContext.SaveChanges"/> detects portable
/// change-tracker entries and calls <see cref="AdvanceAsync"/>; bulk operations
/// that bypass the tracker call it explicitly at their call site inside the same
/// transaction. The update is a single atomic SQL statement so concurrent saves
/// serialize on the row and always observe a strictly larger value. The helper
/// is idempotent per context transaction: at most one advance per transaction,
/// however many portable rows the save contains.</para>
/// </summary>
public static class LibraryRevision
{
    /// <summary>
    /// Atomically increments the singleton revision. CAST(... AS BIGINT) is
    /// accepted by both SQLite (numeric affinity) and PostgreSQL, and keeps the
    /// value numeric for <c>SelfHostedActivationDatabaseBuilder.AdvanceLiveRevision</c>,
    /// which fails closed on a non-numeric revision. The table is a singleton
    /// (enforced by CK_LibraryStates_SingletonSlot), so no row selector is needed.
    /// </summary>
    internal const string AdvanceSql =
        "UPDATE \"LibraryStates\" " +
        "SET \"StateVersion\" = CAST(CAST(\"StateVersion\" AS BIGINT) + 1 AS TEXT)";

    /// <summary>
    /// Advances the revision once for the current context transaction. Callers
    /// that bypass the change tracker (ExecuteUpdate/ExecuteDelete/raw SQL on a
    /// portable set) must call this immediately after the bulk operation and
    /// inside the same explicit transaction; the engine's per-transaction
    /// marker then makes the next SaveChanges in that transaction a no-op.
    /// </summary>
    public static async Task AdvanceAsync(NostosDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.IsLibraryRevisionAdvancedInCurrentTransaction)
        {
            return;
        }

        // The singleton row is seeded by DatabaseBootstrapService on a real host
        // and by EnsureStateAsync in the legacy command services; an
        // EnsureCreated test database may legitimately have no row yet, in
        // which case there is no live library state to revise and the update
        // affects zero rows.
        await db.Database.ExecuteSqlRawAsync(AdvanceSql, ct).ConfigureAwait(false);
        db.MarkLibraryRevisionAdvanced(db.Database.CurrentTransaction);
    }

    /// <summary>Synchronous twin of <see cref="AdvanceAsync"/> for sync SaveChanges.</summary>
    internal static void Advance(NostosDbContext db)
    {
        if (db.IsLibraryRevisionAdvancedInCurrentTransaction)
        {
            return;
        }

        db.Database.ExecuteSqlRaw(AdvanceSql);
        db.MarkLibraryRevisionAdvanced(db.Database.CurrentTransaction);
    }
}

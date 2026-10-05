using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data;

/// <summary>
/// Typed failure raised when the singleton library revision cannot be advanced
/// safely. The save or bulk operation fails closed rather than normalising a
/// corrupted value silently.
/// </summary>
public sealed class LibraryRevisionException : Exception
{
    public const string InvalidRevisionCode = "library_revision_invalid";
    public const string SavepointUnsupportedCode = "library_revision_savepoint_unsupported";

    public LibraryRevisionException(string code, string message)
        : base(message) => Code = code;

    public LibraryRevisionException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    public string Code { get; }

    internal static LibraryRevisionException InvalidValue(string? value) =>
        new(
            InvalidRevisionCode,
            $"The live library revision is missing, empty, or not a non-negative integer " +
            $"(stored value: '{value ?? "<null>"}'). The library cannot be mutated safely.");

    internal static LibraryRevisionException SavepointUnsupported(Exception inner) =>
        new(
            SavepointUnsupportedCode,
            "The active transaction does not support savepoints, so a portable save cannot be " +
            "made atomic with the revision advance. Commit or roll back the transaction and retry.",
            inner);
}

/// <summary>
/// The one authoritative writer of the live library revision
/// (<see cref="LibraryState.StateVersion"/>), issue #679 Slice 11.
///
/// <para><b>Why.</b> Activation compares the opaque destination revision
/// captured at import creation with the revision at activation time. The
/// revision must therefore change on <em>every</em> create, update or delete of
/// portable user-owned state — including content-only edits that never passed
/// through a legacy version bump.</para>
///
/// <para><b>How.</b> <see cref="NostosDbContext.SaveChanges"/> detects portable
/// change-tracker entries and calls <see cref="AdvanceAndGetAsync"/>; bulk
/// operations that bypass the tracker call it explicitly at their call site.
/// No other code writes <see cref="LibraryState.StateVersion"/>: every caller
/// that needs the new value receives it from this helper. The update is a
/// single atomic SQL statement, executed inside the caller's transaction
/// <em>before</em> the portable rows are touched, so concurrent portable
/// writers serialize on the singleton row in one lock order and always observe
/// a strictly larger value. The helper is idempotent once per context-owned
/// transaction: at most one advance per transaction, however many portable
/// rows or saves it contains.</para>
///
/// <para><b>Savepoint atomicity.</b> Inside an ambient transaction the save
/// pipeline wraps the advance and the portable DML in one savepoint created
/// before the advance. If the save fails, both are rolled back to that
/// savepoint and the cached advance is cleared, so a later successful save in
/// the same transaction advances again and a no-change command can never
/// commit an advance for rolled-back work. A provider without savepoint
/// support fails closed with <c>library_revision_savepoint_unsupported</c>;
/// SQLite and PostgreSQL both support savepoints. The idempotence marker is
/// context-local, so two contexts enlisted in one shared underlying
/// transaction each advance once.</para>
///
/// <para><b>Missing row.</b> A database created without the bootstrap seed gets
/// the row inserted at <c>"0"</c> and the same advance applies, so the first
/// portable mutation yields <c>"1"</c>.</para>
///
/// <para><b>Invalid value.</b> An empty, non-numeric, negative, or maxed-out
/// revision fails closed with <see cref="LibraryRevisionException"/> before any
/// mutation is written; it is never coerced to zero. This matches the
/// activation candidate builder, which also refuses a non-numeric revision.</para>
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
        "SET \"StateVersion\" = CAST(CAST(\"StateVersion\" AS BIGINT) + 1 AS TEXT), " +
        "\"UpdatedAt\" = {0}";

    /// <summary>
    /// Takes the per-library serialization point without changing state: a
    /// no-op update of the singleton row. Job-admission ceilings run inside
    /// this lock so concurrent creators cannot both pass the count.
    /// </summary>
    internal const string LockSingletonSql =
        "UPDATE \"LibraryStates\" SET \"SingletonSlot\" = \"SingletonSlot\" WHERE \"SingletonSlot\" = 1";

    private const string SelectSql =
        "SELECT \"StateVersion\" FROM \"LibraryStates\" WHERE \"SingletonSlot\" = 1";

    private const string InsertSql =
        "INSERT INTO \"LibraryStates\" (\"Id\", \"SingletonSlot\", \"StateVersion\", \"UpdatedAt\") " +
        "VALUES ({0}, 1, '0', {1})";

    /// <summary>
    /// Advances the revision once for the current context transaction and
    /// returns the new value. Callers that bypass the change tracker
    /// (ExecuteUpdate/ExecuteDelete/raw SQL on a portable set) must call this
    /// immediately before the bulk operation and inside the same explicit
    /// transaction; the engine's per-transaction marker then makes the next
    /// SaveChanges in that transaction a no-op.
    /// </summary>
    public static async Task<string> AdvanceAndGetAsync(NostosDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.TryGetCachedLibraryRevision(out var cached))
        {
            return cached;
        }

        if (db.Database.CurrentTransaction is not null)
        {
            return await AdvanceCoreAsync(db, ct).ConfigureAwait(false);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        string? result = null;
        await strategy.ExecuteAsync(async () =>
        {
            await using var owned = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                result = await AdvanceCoreAsync(db, ct).ConfigureAwait(false);
                await owned.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);

        return result!;
    }

    /// <summary>Synchronous twin of <see cref="AdvanceAndGetAsync"/>.</summary>
    internal static string AdvanceAndGet(NostosDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.TryGetCachedLibraryRevision(out var cached))
        {
            return cached;
        }

        if (db.Database.CurrentTransaction is not null)
        {
            return AdvanceCore(db);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        string? result = null;
        strategy.Execute(() =>
        {
            using var owned = db.Database.BeginTransaction();
            try
            {
                result = AdvanceCore(db);
                owned.Commit();
            }
            catch
            {
                owned.Rollback();
                throw;
            }
        });

        return result!;
    }

    /// <summary>
    /// Serializes a per-library admission decision on the singleton row without
    /// advancing the revision. Used by migration job creation so the
    /// outstanding-job ceiling is a hard bound under concurrent creators.
    /// </summary>
    public static async Task LockSingletonAsync(NostosDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await db.Database.ExecuteSqlRawAsync(LockSingletonSql, ct).ConfigureAwait(false) == 1)
        {
            return;
        }

        await EnsureSingletonRowAsync(db, ct).ConfigureAwait(false);
        if (await db.Database.ExecuteSqlRawAsync(LockSingletonSql, ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The library singleton row could not be locked.");
        }
    }

    private static async Task<string> AdvanceCoreAsync(NostosDbContext db, CancellationToken ct)
    {
        var current = await ReadVersionAsync(db, ct).ConfigureAwait(false);
        if (current is null)
        {
            await EnsureSingletonRowAsync(db, ct).ConfigureAwait(false);
            current = await ReadVersionAsync(db, ct).ConfigureAwait(false);
        }

        EnsureValid(current);
        var now = DateTime.UtcNow;
        if (await db.Database.ExecuteSqlRawAsync(AdvanceSql, [now], ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The library revision row could not be advanced.");
        }

        var next = await ReadVersionAsync(db, ct).ConfigureAwait(false);
        EnsureValid(next);
        ProtectTrackedState(db);
        db.MarkLibraryRevisionAdvanced(db.Database.CurrentTransaction, next!);
        return next!;
    }

    private static string AdvanceCore(NostosDbContext db)
    {
        var current = ReadVersion(db);
        if (current is null)
        {
            EnsureSingletonRow(db);
            current = ReadVersion(db);
        }

        EnsureValid(current);
        var now = DateTime.UtcNow;
        if (db.Database.ExecuteSqlRaw(AdvanceSql, [now]) != 1)
        {
            throw new InvalidOperationException("The library revision row could not be advanced.");
        }

        var next = ReadVersion(db);
        EnsureValid(next);
        ProtectTrackedState(db);
        db.MarkLibraryRevisionAdvanced(db.Database.CurrentTransaction, next!);
        return next!;
    }

    private static void EnsureValid(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0
            || parsed == long.MaxValue)
        {
            throw LibraryRevisionException.InvalidValue(value);
        }
    }

    /// <summary>
    /// Creates the singleton row at <c>"0"</c> when it is missing. A concurrent
    /// creator may win the insert; the unique index makes that a retry, not a
    /// failure.
    /// </summary>
    private static async Task EnsureSingletonRowAsync(NostosDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                InsertSql,
                [LibraryState.WellKnownId, DateTime.UtcNow],
                ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
            // The singleton already exists (or another writer inserted it
            // first); the caller re-reads and fails closed if it truly is
            // missing.
        }
    }

    private static void EnsureSingletonRow(NostosDbContext db)
    {
        try
        {
            db.Database.ExecuteSqlRaw(InsertSql, [LibraryState.WellKnownId, DateTime.UtcNow]);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException)
        {
        }
    }

    private static async Task<string?> ReadVersionAsync(NostosDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = SelectSql;
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static string? ReadVersion(NostosDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = SelectSql;
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Neutralises any tracked <see cref="LibraryState"/> entry so EF cannot
    /// write a stale in-memory value during the same save. The entity's object
    /// is deliberately not mutated: the atomic SQL write is authoritative, and
    /// a caller that needs the new value must use
    /// <see cref="AdvanceAndGetAsync"/>. Leaving the object untouched also
    /// keeps no-change/failure paths reporting the committed value.
    /// </summary>
    private static void ProtectTrackedState(NostosDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<LibraryState>())
        {
            entry.State = EntityState.Unchanged;
        }
    }
}

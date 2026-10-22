using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data;

/// <summary>
/// Database lifecycle helper: makes Nostos start safely on a truly empty
/// SQLite database (brand-new file, or an existing file with zero tables)
/// without any test-only bootstrap, while preserving ordinary EF migration
/// semantics for every existing database.
///
/// This repository's migration history has no initial baseline migration —
/// the oldest retained migration alters the legacy Books table — so a fresh
/// database cannot be created with Migrate() alone. This service therefore
/// bootstraps a truly empty database to the complete current EF schema and
/// records an accurate migration-history baseline in one transaction; every
/// subsequent startup (and any existing database) goes through the ordinary
/// <see cref="DatabaseFacade.MigrateAsync"/> path and is never rebaselined.
/// Non-empty databases with partial or unknown schemas are left to the
/// ordinary migration path, which fails closed instead of stamping a
/// baseline onto a schema it cannot verify.
/// </summary>
public interface IDatabaseBootstrapService
{
    /// <summary>
    /// Ensures the database is ready for normal startup. A truly empty
    /// database is bootstrapped (complete schema + migration history in one
    /// transaction); anything else is migrated through the ordinary EF path.
    /// Throws (failing closed) when an existing database cannot be migrated.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDatabaseBootstrapService" />
public sealed class DatabaseBootstrapService(NostosDbContext db) : IDatabaseBootstrapService
{
    private const string HistoryTableName = "__EFMigrationsHistory";

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        var creator = db.Database.GetService<IRelationalDatabaseCreator>();

        if (creator.HasTables())
        {
            // Existing database: ordinary EF migration path only. Never
            // rebaseline, never stamp, never rewrite history. If the schema
            // is partial or unknown, MigrateAsync fails closed below.
            await db.Database.MigrateAsync(cancellationToken);
            await Services.Library.LibraryIdentityBackfill.BackfillAsync(db, cancellationToken);
            // Topics whose key the migration could not fold in full, or that
            // differ from another only in case, are merged into one.
            await new Repositories.TopicRepository(db).MergeCaseVariantsAsync(cancellationToken);
            // The notes each writing cites (issue #1162) are derived from its
            // content: AddWritingCitations creates the table empty and this
            // pass fills it, then keeps it right. Writes only differences.
            await Services.Writings.WritingCitationIndex.RebuildAllAsync(db, cancellationToken);
            await EnsureLibraryStateRowAsync(cancellationToken);
            return;
        }

        // Truly empty database (no user tables at all). Build the complete
        // current schema and an accurate migration-history baseline in one
        // transaction, so a crash mid-bootstrap leaves the database empty
        // again and the next startup retries cleanly.

        var migrationIds = db.Database.GetMigrations().ToList();
        if (migrationIds.Count == 0)
        {
            throw new InvalidOperationException(
                "Cannot bootstrap an empty database: no EF migrations were resolved from the backend assembly.");
        }

        var historyRepository = db.GetService<IHistoryRepository>();
        var productVersion = ResolveProductVersion();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // 1) Complete current schema, generated from the EF model exactly as
        //    EnsureCreated would create it.
        await db.Database.ExecuteSqlRawAsync(
            db.Database.GenerateCreateScript(),
            cancellationToken);

        // 2) The EF migration-history table, using EF's own creation script
        //    so it matches what Migrate() would create byte for byte.
        await db.Database.ExecuteSqlRawAsync(
            historyRepository.GetCreateScript(),
            cancellationToken);

        // 3) Every known migration marked applied, exactly once, in EF's own
        //    insert format.
        foreach (var migrationId in migrationIds)
        {
            await db.Database.ExecuteSqlRawAsync(
                historyRepository.GetInsertScript(new HistoryRow(migrationId, productVersion)),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // Seed the library singleton row (read paths must never mutate).
        await EnsureLibraryStateRowAsync(cancellationToken);
    }

    private async Task EnsureLibraryStateRowAsync(CancellationToken ct)
    {
        if (await db.LibraryStates.AnyAsync(ct))
            return;

        db.LibraryStates.Add(new LibraryState
        {
            Id = LibraryState.WellKnownId,
            SingletonSlot = LibraryState.SingletonSentinel,
        });
        await db.SaveChangesAsync(ct);
    }

    // Mirror what EF Core's own Migrator stamps into ProductVersion
    // (AssemblyInformationalVersion, hash suffix stripped), so the baseline
    // is indistinguishable from an incrementally migrated database.
    private static string ResolveProductVersion()
    {
        var informational = typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }

        return typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
    }
}

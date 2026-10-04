using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Builds the activation candidate database beside the live one. The candidate is
/// created through the same documented bootstrap path as a fresh installation at
/// the current schema, then receives the hash-verified prepared portable payload
/// through the shared relational restore and finally the live host operational
/// state through one consistent read transaction. The live database file is only
/// ever opened read-only and never moved by this component.
/// </summary>
internal interface ISelfHostedActivationDatabaseBuilder
{
    /// <summary>
    /// Builds (or rebuilds from scratch after a crash) the candidate database for
    /// the job. Idempotent: an existing candidate is discarded and rebuilt. On
    /// failure the partial candidate is removed and the error is rethrown.
    /// </summary>
    Task BuildAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the built candidate for read-only verification. The caller owns the
    /// returned context. Slice 4's portable verifier reads the candidate through
    /// this method.
    /// </summary>
    NostosDbContext OpenCandidate(Guid jobId);
}

internal sealed class SelfHostedActivationDatabaseBuilder : ISelfHostedActivationDatabaseBuilder
{
    private const string PortableDataFileName = "portable-data.json.tmp";

    private readonly SelfHostedActivationPaths _paths;
    private readonly IPortableImportStaging _staging;
    private readonly TimeProvider _clock;

    internal SelfHostedActivationDatabaseBuilder(
        SelfHostedActivationPaths paths,
        IPortableImportStaging staging,
        TimeProvider? clock = null)
    {
        _paths = paths;
        _staging = staging;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Test seam: invoked with the step name after each durable build step.</summary>
    internal Action<string>? AfterBuildStepForTesting { get; set; }

    public async Task BuildAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var candidate = _paths.CandidateDatabase(jobId);
        var dataPath = Path.Combine(Path.GetDirectoryName(candidate)!, PortableDataFileName);
        _paths.Prepare(jobId);
        RemoveCandidateArtifacts(candidate, dataPath);
        try
        {
            var data = await ReadVerifiedDataAsync(prepared, dataPath, cancellationToken);
            AfterBuildStepForTesting?.Invoke("data");

            await ApplyPortableAsync(candidate, data, prepared, cancellationToken);
            File.Delete(dataPath); // the verified payload is now materialized; keep the candidate area clean
            AfterBuildStepForTesting?.Invoke("portable");

            CopyHostState(candidate, cancellationToken);
            AfterBuildStepForTesting?.Invoke("host-state");

            VerifySelfContained(candidate);
            AfterBuildStepForTesting?.Invoke("verify");
        }
        catch
        {
            TryRemoveCandidateArtifacts(candidate, dataPath);
            throw;
        }
    }

    public NostosDbContext OpenCandidate(Guid jobId) =>
        CreateContext(_paths.CandidateDatabase(jobId), readOnly: true);

    private async Task<PortableLibraryData> ReadVerifiedDataAsync(
        IPreparedPortableImport prepared,
        string dataPath,
        CancellationToken cancellationToken)
    {
        var metadata = prepared.Metadata;
        await using (var source = await _staging.OpenDataReadAsync(metadata.StagingId, cancellationToken))
        {
            var (length, sha256) = await SelfHostedSqliteFile.CopyAndHashAsync(
                source,
                dataPath,
                metadata.DataBytes,
                cancellationToken);
            if (length != metadata.DataBytes
                || !string.Equals(sha256, metadata.DataSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationActivationException(
                    MigrationActivationErrorCodes.Failed,
                    "The prepared import's relational payload failed hash verification.");
            }
        }

        await using var staged = new FileStream(
            dataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await PortableLibraryDatabaseMaterializer.ReadRelationalDataAsync(staged, cancellationToken);
    }

    private static async Task ApplyPortableAsync(
        string candidate,
        PortableLibraryData data,
        IPreparedPortableImport prepared,
        CancellationToken cancellationToken)
    {
        await using (var db = CreateContext(candidate))
        {
            // Documented bootstrap path: a fresh candidate gets the complete
            // current schema plus an accurate migration-history baseline, exactly
            // like a normally bootstrapped database. Do not rebaseline a clone.
            await new DatabaseBootstrapService(db).EnsureReadyAsync(cancellationToken);
            db.ChangeTracker.Clear();
            await PortableLibraryDatabaseMaterializer.ApplyRelationalDataAsync(
                db,
                data,
                prepared.Media,
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        SelfHostedSqliteFile.ClearPool(candidate);
    }

    /// <summary>
    /// Reads every carry-over table inside ONE deferred read transaction, so the
    /// snapshot cannot mix rows from before and after a concurrent commit. In WAL
    /// mode readers do not block writers; the main live file is never written.
    /// </summary>
    private void CopyHostState(string candidate, CancellationToken cancellationToken)
    {
        using var model = CreateContext(candidate);
        var decisions = SelfHostedHostStateCarryOver.CarryDecisions;
        var ordered = OrderForInsertion(model, decisions);
        var schemas = ordered.ToDictionary(
            decision => decision.EntityType,
            decision => SchemaFor(model, decision.EntityType));

        var snapshot = new Dictionary<Type, List<object?[]>>();
        object?[]? liveLibraryState = null;
        using (var live = SelfHostedSqliteFile.Open(_paths.LiveDatabase, readOnly: true))
        using (var transaction = live.BeginTransaction(deferred: true))
        {
            foreach (var decision in ordered)
            {
                var (table, columns) = schemas[decision.EntityType];
                var rows = ReadRows(live, transaction, table, columns);
                if (decision.EntityType == typeof(LibraryState))
                {
                    liveLibraryState = rows.Count == 1 ? rows[0] : null;
                }
                else
                {
                    snapshot[decision.EntityType] = rows;
                }
            }

            transaction.Commit();
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var target = SelfHostedSqliteFile.Open(candidate, readOnly: false);
        Execute(target, "PRAGMA foreign_keys = ON;");
        var survivingNoteIds = SelfHostedSqliteFile
            .QueryStrings(target, "SELECT \"Id\" FROM \"Notes\";")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var decision in ordered)
        {
            var (table, columns) = schemas[decision.EntityType];
            if (decision.EntityType == typeof(LibraryState))
            {
                ApplyLibraryState(target, schemas, liveLibraryState);
            }
            else if (decision.EntityType == typeof(NoteImportBatchNote))
            {
                // Undo links to notes replaced by the imported library cannot be
                // restored without a foreign-key violation; links whose note does
                // survive (same portable ID) are preserved.
                var noteIdIndex = IndexOf(columns, "NoteId");
                var rows = snapshot[decision.EntityType]
                    .Where(row => row[noteIdIndex] is string noteId && survivingNoteIds.Contains(noteId))
                    .ToArray();
                InsertRows(target, table, columns, rows);
            }
            else
            {
                InsertRows(target, table, columns, snapshot[decision.EntityType]);
            }
        }
    }

    /// <summary>
    /// Advances the singleton library revision so the candidate represents the
    /// newly activated portable state; the row itself is host infrastructure and
    /// is never imported from a portable archive.
    /// </summary>
    private void ApplyLibraryState(
        SqliteConnection candidate,
        IReadOnlyDictionary<Type, (string Table, IReadOnlyList<string> Columns)> schemas,
        object?[]? liveRow)
    {
        if (liveRow is null)
        {
            return; // the fresh bootstrap already seeded the singleton row
        }

        var (stateTable, columns) = schemas[typeof(LibraryState)];
        var version = NextVersion(liveRow[IndexOf(columns, "StateVersion")] as string);
        var updatedAt = _clock.GetUtcNow().UtcDateTime;
        var slot = liveRow[IndexOf(columns, "SingletonSlot")];

        using (var update = candidate.CreateCommand())
        {
            update.CommandText =
                $"UPDATE {Quote(stateTable)} SET \"StateVersion\" = $version, \"UpdatedAt\" = $updated " +
                "WHERE \"SingletonSlot\" = $slot;";
            update.Parameters.AddWithValue("$version", version);
            update.Parameters.AddWithValue("$updated", updatedAt);
            update.Parameters.AddWithValue("$slot", slot ?? DBNull.Value);
            if (update.ExecuteNonQuery() > 0)
            {
                return;
            }
        }

        using var insert = candidate.CreateCommand();
        insert.CommandText =
            $"INSERT INTO {Quote(stateTable)} (\"Id\", \"SingletonSlot\", \"StateVersion\", \"UpdatedAt\") " +
            "VALUES ($id, $slot, $version, $updated);";
        insert.Parameters.AddWithValue("$id", liveRow[IndexOf(columns, "Id")] ?? DBNull.Value);
        insert.Parameters.AddWithValue("$slot", slot ?? DBNull.Value);
        insert.Parameters.AddWithValue("$version", version);
        insert.Parameters.AddWithValue("$updated", updatedAt);
        insert.ExecuteNonQuery();
    }

    private void VerifySelfContained(string candidate)
    {
        SelfHostedSqliteFile.ClearPool(candidate);
        using (var connection = SelfHostedSqliteFile.Open(candidate, readOnly: false))
        {
            var integrity = SelfHostedSqliteFile.QueryStrings(connection, "PRAGMA integrity_check;");
            if (integrity.Count != 1 || !string.Equals(integrity[0], "ok", StringComparison.Ordinal))
            {
                throw new MigrationActivationException(
                    MigrationActivationErrorCodes.Failed,
                    "The candidate database failed its integrity check.");
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_key_check;";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    throw new MigrationActivationException(
                        MigrationActivationErrorCodes.Failed,
                        "The candidate database has foreign-key violations.");
                }
            }

            var busy = SelfHostedSqliteFile.CheckpointTruncate(connection);
            if (busy != 0)
            {
                throw new MigrationActivationException(
                    MigrationActivationErrorCodes.Failed,
                    "The candidate database WAL could not be checkpointed.");
            }
        }

        SelfHostedSqliteFile.ClearPool(candidate);
        if (SelfHostedSqliteFile.RemainingSidecarProblem(candidate) is { } problem)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed, problem);
        }
    }

    private void RemoveCandidateArtifacts(string candidate, string dataPath)
    {
        SelfHostedSqliteFile.ClearPool(candidate);
        File.Delete(candidate);
        File.Delete(candidate + "-wal");
        File.Delete(candidate + "-shm");
        File.Delete(dataPath);
    }

    private void TryRemoveCandidateArtifacts(string candidate, string dataPath)
    {
        try
        {
            RemoveCandidateArtifacts(candidate, dataPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next build always starts by removing the prefix.
        }
    }

    private static NostosDbContext CreateContext(string path, bool readOnly = false) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                SelfHostedSqliteFile.ConnectionString(path, readOnly, pooling: true),
                sqlite => sqlite.MigrationsAssembly(typeof(PersistenceRegistration).Assembly.FullName))
            .Options);

    private static IReadOnlyList<HostStateCarryOverDecision> OrderForInsertion(
        NostosDbContext model,
        IReadOnlyList<HostStateCarryOverDecision> decisions)
    {
        var carryTypes = decisions.Select(decision => decision.EntityType).ToHashSet();
        var result = new List<HostStateCarryOverDecision>();
        var remaining = decisions.ToList();
        while (remaining.Count > 0)
        {
            var progressed = false;
            for (var index = 0; index < remaining.Count; index++)
            {
                var decision = remaining[index];
                var entity = model.Model.FindEntityType(decision.EntityType)
                    ?? throw new InvalidOperationException(
                        $"Carry-over entity {decision.EntityName} is not in the EF model.");
                var principals = entity.GetForeignKeys()
                    .Select(foreignKey => foreignKey.PrincipalEntityType.ClrType)
                    .Where(type => type != decision.EntityType && carryTypes.Contains(type))
                    .ToArray();
                if (principals.All(principal => result.Any(placed => placed.EntityType == principal)))
                {
                    result.Add(decision);
                    remaining.RemoveAt(index--);
                    progressed = true;
                }
            }

            if (!progressed)
            {
                throw new InvalidOperationException("The carry-over dependency graph contains a cycle.");
            }
        }

        return result;
    }

    private static (string Table, IReadOnlyList<string> Columns) SchemaFor(
        NostosDbContext model,
        Type entityType)
    {
        var entity = model.Model.FindEntityType(entityType)
            ?? throw new InvalidOperationException($"Entity {entityType.Name} is not in the EF model.");
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"Entity {entityType.Name} has no table mapping.");
        var columns = entity.GetProperties()
            .Select(property => property.GetColumnName())
            .OfType<string>()
            .ToArray();
        return (table, columns);
    }

    private static List<object?[]> ReadRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<string> columns)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)};";
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[columns.Count];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static void InsertRows(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> columns,
        IEnumerable<object?[]> rows)
    {
        var list = rows as IList<object?[]> ?? rows.ToList();
        if (list.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, index) => "$p" + index))});";
        var parameters = new SqliteParameter[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            parameters[index] = command.Parameters.AddWithValue("$p" + index, DBNull.Value);
        }

        foreach (var row in list)
        {
            for (var index = 0; index < columns.Count; index++)
            {
                parameters[index].Value = row[index] ?? DBNull.Value;
            }

            command.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int IndexOf(IReadOnlyList<string> columns, string column)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (string.Equals(columns[index], column, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Column {column} is not mapped by the EF model.");
    }

    private static string Quote(string identifier) => $"\"{identifier}\"";

    private static string NextVersion(string? version) =>
        (long.TryParse(version, out var parsed) ? parsed + 1 : 1).ToString();
}

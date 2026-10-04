using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Builds the activation candidate database beside the live one in two phases.
/// <see cref="BuildPortableCandidateAsync"/> is the long phase and runs while the
/// live library keeps serving: a fresh current-schema bootstrap plus the
/// hash-verified prepared portable payload, and no host operational state.
/// <see cref="FinalizeCandidateAsync"/> is the short phase and requires the
/// current exclusive maintenance lease: it replaces the candidate's host
/// operational state from ONE authoritative live snapshot and durably marks the
/// candidate finalized. The live database file is only ever opened read-only and
/// never moved by this component.
/// </summary>
internal interface ISelfHostedActivationDatabaseBuilder
{
    /// <summary>
    /// Builds (or rebuilds from scratch after a crash) the portable part of the
    /// candidate database for the job. Idempotent and crash-safe: an existing
    /// candidate, its sidecars, its temporary payload and any finalization marker
    /// are discarded first. The result is deliberately NOT cutover-ready.
    /// </summary>
    Task BuildPortableCandidateAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the live host operational state into the candidate under the
    /// caller's current exclusive maintenance lease and marks the candidate
    /// finalized. Must run after every pre-cutover live mutation and before
    /// <c>QuiesceLive</c>. Repeatable: re-running replaces the previous
    /// host-state rows from a fresh live snapshot. On any failure the candidate
    /// is left reported as unfinalized.
    /// </summary>
    Task FinalizeCandidateAsync(
        Guid jobId,
        IAsyncDisposable exclusiveLease,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True only when the durable finalization marker exists, validates for this
    /// job, and the candidate database is present. A corrupt or inconsistent
    /// marker fails closed with <c>migration_activation_failed</c>; the cutover
    /// slice must refuse any candidate that is not finalized.
    /// </summary>
    bool IsFinalized(Guid jobId);

    /// <summary>
    /// Opens the built candidate for read-only verification with pooling
    /// disabled, so disposing the context leaves no pooled handle on a file that
    /// will later be renamed. The caller owns the returned context. Slice 4's
    /// portable verifier reads the candidate through this method.
    /// </summary>
    NostosDbContext OpenCandidate(Guid jobId);
}

/// <summary>Durable marker that the candidate's host state came from the activation boundary.</summary>
internal sealed record SelfHostedCandidateFinalization(
    Guid JobId,
    string Revision,
    DateTimeOffset FinalizedAtUtc,
    int MarkerVersion = 1);

internal sealed class SelfHostedActivationDatabaseBuilder : ISelfHostedActivationDatabaseBuilder
{
    private const string PortableDataFileName = "portable-data.json.tmp";

    private readonly SelfHostedActivationPaths _paths;
    private readonly IPortableImportStaging _staging;
    private readonly LibraryMaintenanceCoordinator _maintenance;
    private readonly TimeProvider _clock;

    internal SelfHostedActivationDatabaseBuilder(
        SelfHostedActivationPaths paths,
        IPortableImportStaging staging,
        LibraryMaintenanceCoordinator maintenance,
        TimeProvider? clock = null)
    {
        _paths = paths;
        _staging = staging;
        _maintenance = maintenance;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Test seam: invoked with the step name after each portable build step.</summary>
    internal Action<string>? AfterBuildStepForTesting { get; set; }

    /// <summary>Test seam: invoked with the step name immediately BEFORE each finalization step.</summary>
    internal Action<string>? BeforeFinalizeStepForTesting { get; set; }

    public async Task BuildPortableCandidateAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var candidate = _paths.CandidateDatabase(jobId);
        var dataPath = Path.Combine(Path.GetDirectoryName(candidate)!, PortableDataFileName);
        _paths.Prepare(jobId);
        RemoveCandidateArtifacts(jobId, candidate, dataPath);
        try
        {
            await CopyVerifiedDataAsync(prepared, dataPath, cancellationToken);
            AfterBuildStepForTesting?.Invoke("data");

            await ApplyPortableAsync(candidate, dataPath, prepared, cancellationToken);
            File.Delete(dataPath); // the verified payload is now materialized; keep the candidate area clean
            AfterBuildStepForTesting?.Invoke("portable");
        }
        catch
        {
            TryRemoveCandidateArtifacts(jobId, candidate, dataPath);
            throw;
        }
    }

    public Task FinalizeCandidateAsync(
        Guid jobId,
        IAsyncDisposable exclusiveLease,
        CancellationToken cancellationToken = default)
    {
        _maintenance.WithExclusiveLease(exclusiveLease, () => FinalizeCore(jobId, cancellationToken));
        return Task.CompletedTask;
    }

    public bool IsFinalized(Guid jobId)
    {
        var markerPath = _paths.CandidateFinalizationMarker(jobId);
        _paths.VerifyDatabasePath(markerPath);
        if (!File.Exists(markerPath))
        {
            if (Directory.Exists(markerPath))
            {
                throw CorruptMarker();
            }

            return false;
        }

        SelfHostedCandidateFinalization marker;
        try
        {
            marker = JsonSerializer.Deserialize<SelfHostedCandidateFinalization>(File.ReadAllBytes(markerPath))
                ?? throw CorruptMarker();
        }
        catch (JsonException)
        {
            throw CorruptMarker();
        }

        if (marker.MarkerVersion != 1 || marker.JobId != jobId)
        {
            throw CorruptMarker();
        }

        if (!File.Exists(_paths.CandidateDatabase(jobId)))
        {
            throw new MigrationActivationException(
                MigrationActivationErrorCodes.Failed,
                "The finalized activation candidate database is missing.");
        }

        return true;
    }

    public NostosDbContext OpenCandidate(Guid jobId) =>
        CreateContext(_paths.CandidateDatabase(jobId), readOnly: true, pooling: false);

    private void FinalizeCore(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = _paths.CandidateDatabase(jobId);
        var markerPath = _paths.CandidateFinalizationMarker(jobId);
        _paths.Prepare(jobId);
        _paths.VerifyDatabasePath(markerPath);
        if (!File.Exists(candidate))
        {
            throw new MigrationActivationException(
                MigrationActivationErrorCodes.Failed,
                "The activation candidate database has not been built.");
        }

        // From here on any failure must leave the candidate reported as
        // unfinalized, so the durable marker is removed before new host state is
        // written; it is only rewritten after every check succeeds.
        File.Delete(markerPath);

        using var model = CreateContext(candidate, readOnly: false, pooling: false);
        var decisions = SelfHostedHostStateCarryOver.CarryDecisions;
        var ordered = OrderForInsertion(model, decisions);
        var schemas = ordered.ToDictionary(
            decision => decision.EntityType,
            decision => SchemaFor(model, decision.EntityType));

        var snapshot = ReadLiveHostState(ordered, schemas, out var liveLibraryState);
        cancellationToken.ThrowIfCancellationRequested();

        var advancedRevision = AdvanceLiveRevision(
            schemas[typeof(LibraryState)].Columns,
            liveLibraryState);

        BeforeFinalizeStepForTesting?.Invoke("write");
        WriteCandidateHostState(candidate, ordered, schemas, snapshot, liveLibraryState, advancedRevision);

        cancellationToken.ThrowIfCancellationRequested();
        BeforeFinalizeStepForTesting?.Invoke("verify");
        VerifySelfContained(candidate);

        BeforeFinalizeStepForTesting?.Invoke("marker");
        WriteFinalizationMarker(jobId, advancedRevision);
    }

    private async Task CopyVerifiedDataAsync(
        IPreparedPortableImport prepared,
        string dataPath,
        CancellationToken cancellationToken)
    {
        var metadata = prepared.Metadata;
        await using var source = await _staging.OpenDataReadAsync(metadata.StagingId, cancellationToken);
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

    private static async Task ApplyPortableAsync(
        string candidate,
        string dataPath,
        IPreparedPortableImport prepared,
        CancellationToken cancellationToken)
    {
        await using (var db = CreateContext(candidate, readOnly: false, pooling: true))
        {
            // Documented bootstrap path: a fresh candidate gets the complete
            // current schema plus an accurate migration-history baseline, exactly
            // like a normally bootstrapped database. Do not rebaseline a clone.
            await new DatabaseBootstrapService(db).EnsureReadyAsync(cancellationToken);
            db.ChangeTracker.Clear();

            // The same relational restore the compatibility import endpoint uses,
            // applied to the exact bytes just hash-verified.
            await using var verified = new FileStream(
                dataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await PortableLibraryRelationalRestore.ApplyVerifiedPayloadAsync(
                db,
                verified,
                prepared.Media,
                cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        SelfHostedSqliteFile.ClearPoolFor(candidate);
    }

    /// <summary>
    /// Reads every carry-over table inside ONE deferred read transaction, so the
    /// snapshot cannot mix rows from before and after a concurrent commit, and
    /// the caller runs it under the exclusive maintenance lease so it is also the
    /// authoritative activation-boundary snapshot. In WAL mode readers do not
    /// block writers; the main live file is never written.
    /// </summary>
    private Dictionary<Type, List<object?[]>> ReadLiveHostState(
        IReadOnlyList<HostStateCarryOverDecision> ordered,
        IReadOnlyDictionary<Type, (string Table, IReadOnlyList<string> Columns)> schemas,
        out object?[] liveLibraryState)
    {
        var snapshot = new Dictionary<Type, List<object?[]>>();
        List<object?[]>? liveStateRows = null;
        using (var live = SelfHostedSqliteFile.Open(_paths.LiveDatabase, readOnly: true))
        using (var transaction = live.BeginTransaction(deferred: true))
        {
            foreach (var decision in ordered)
            {
                var (table, columns) = schemas[decision.EntityType];
                var rows = ReadRows(live, transaction, table, columns);
                if (decision.EntityType == typeof(LibraryState))
                {
                    liveStateRows = rows;
                }
                else
                {
                    snapshot[decision.EntityType] = rows;
                }
            }

            transaction.Commit();
        }

        if (liveStateRows is not { Count: 1 })
        {
            throw new MigrationActivationException(
                MigrationActivationErrorCodes.Failed,
                "The live library revision row is missing or duplicated.");
        }

        liveLibraryState = liveStateRows[0];
        return snapshot;
    }

    /// <summary>
    /// Advances the singleton library revision from the authoritative live value:
    /// one activation is one committed library-state mutation. Missing,
    /// non-numeric, negative or non-incrementable revisions fail closed.
    /// </summary>
    private static string AdvanceLiveRevision(IReadOnlyList<string> columns, object?[] liveRow)
    {
        var raw = liveRow[IndexOf(columns, "StateVersion")] as string;
        if (raw is null
            || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version == long.MaxValue)
        {
            throw new MigrationActivationException(
                MigrationActivationErrorCodes.Failed,
                "The live library revision is missing, invalid, or cannot be advanced.");
        }

        return (version + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Replaces the candidate's carried host-state rows from one snapshot inside
    /// ONE candidate transaction: children are deleted first, all rows are then
    /// reinserted in dependency order (with the advanced singleton revision), and
    /// the transaction commits atomically. Re-running can never duplicate rows.
    /// </summary>
    private void WriteCandidateHostState(
        string candidate,
        IReadOnlyList<HostStateCarryOverDecision> ordered,
        IReadOnlyDictionary<Type, (string Table, IReadOnlyList<string> Columns)> schemas,
        IReadOnlyDictionary<Type, List<object?[]>> snapshot,
        object?[] liveLibraryState,
        string advancedRevision)
    {
        using var connection = SelfHostedSqliteFile.Open(candidate, readOnly: false);
        Execute(connection, "PRAGMA foreign_keys = ON;");
        using var transaction = connection.BeginTransaction(deferred: false);

        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var (table, _) = schemas[ordered[index].EntityType];
            Execute(connection, transaction, $"DELETE FROM {Quote(table)};");
        }

        foreach (var decision in ordered)
        {
            var (table, columns) = schemas[decision.EntityType];
            if (decision.EntityType == typeof(LibraryState))
            {
                var row = (object?[])liveLibraryState.Clone();
                row[IndexOf(columns, "StateVersion")] = advancedRevision;
                row[IndexOf(columns, "UpdatedAt")] = _clock.GetUtcNow().UtcDateTime;
                InsertRows(connection, transaction, table, columns, [row]);
            }
            else
            {
                InsertRows(connection, transaction, table, columns, snapshot[decision.EntityType]);
            }
        }

        transaction.Commit();
    }

    private void WriteFinalizationMarker(Guid jobId, string revision)
    {
        var markerPath = _paths.CandidateFinalizationMarker(jobId);
        _paths.VerifyDatabasePath(markerPath);
        var temporary = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        _paths.VerifyDatabasePath(temporary);
        var marker = new SelfHostedCandidateFinalization(jobId, revision, _clock.GetUtcNow());
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(marker));
            stream.Flush(flushToDisk: true);
        }

        ActivationFileSystem.Rename(temporary, markerPath, overwrite: true);
    }

    private void VerifySelfContained(string candidate)
    {
        SelfHostedSqliteFile.ClearPools();
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

        SelfHostedSqliteFile.ClearPools();
        if (SelfHostedSqliteFile.RemainingSidecarProblem(candidate) is { } problem)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed, problem);
        }
    }

    private void RemoveCandidateArtifacts(Guid jobId, string candidate, string dataPath)
    {
        SelfHostedSqliteFile.ClearPoolFor(candidate);
        File.Delete(candidate);
        File.Delete(candidate + "-wal");
        File.Delete(candidate + "-shm");
        File.Delete(dataPath);
        var marker = _paths.CandidateFinalizationMarker(jobId);
        File.Delete(marker);
        foreach (var temporary in Directory.EnumerateFiles(
                     Path.GetDirectoryName(marker)!,
                     Path.GetFileName(marker) + ".*"))
        {
            File.Delete(temporary);
        }
    }

    private void TryRemoveCandidateArtifacts(Guid jobId, string candidate, string dataPath)
    {
        try
        {
            RemoveCandidateArtifacts(jobId, candidate, dataPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next build always starts by removing the prefix.
        }
    }

    private static NostosDbContext CreateContext(string path, bool readOnly, bool pooling) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                SelfHostedSqliteFile.ConnectionString(path, readOnly, pooling),
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
        SqliteTransaction transaction,
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
        command.Transaction = transaction;
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

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static MigrationActivationException CorruptMarker() => new(
        MigrationActivationErrorCodes.Failed,
        "The activation candidate finalization marker is corrupt or does not belong to this job.");
}

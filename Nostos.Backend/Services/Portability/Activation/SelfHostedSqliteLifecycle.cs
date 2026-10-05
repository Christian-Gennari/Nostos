using Microsoft.Data.Sqlite;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// SQLite file lifecycle for the cutover. Both operations require the current
/// exclusive maintenance lease: the lease is the only host-local proof that every
/// application reader/writer has drained. Each primitive clears the connection
/// pool, fails closed when the WAL cannot be truncated or a connection is still
/// open, and is safe to repeat.
/// </summary>
internal interface ISelfHostedSqliteLifecycle
{
    /// <summary>
    /// Makes the live main database file a complete standalone database: pool
    /// cleared, WAL checkpointed with TRUNCATE and verified gone. Throws before
    /// any live-path change when a connection pins the database.
    /// </summary>
    void QuiesceLive(IAsyncDisposable exclusiveLease);

    /// <summary>
    /// Clears every pool entry for the activated path and verifies the activated
    /// database with <c>integrity_check</c> and <c>foreign_key_check</c> before
    /// ordinary traffic reopens.
    /// </summary>
    void ReopenActivated(IAsyncDisposable exclusiveLease);
}

internal sealed class SelfHostedSqliteLifecycle(
    SelfHostedActivationPaths paths,
    LibraryMaintenanceCoordinator maintenance) : ISelfHostedSqliteLifecycle
{
    public void QuiesceLive(IAsyncDisposable exclusiveLease) =>
        maintenance.WithExclusiveLease(exclusiveLease, () => SelfHostedSqliteFile.QuiesceLive(paths.LiveDatabase));

    public void ReopenActivated(IAsyncDisposable exclusiveLease) =>
        maintenance.WithExclusiveLease(exclusiveLease, () => SelfHostedSqliteFile.ReopenActivated(paths.LiveDatabase));
}

/// <summary>
/// Raw SQLite operations shared by the cutover lifecycle (lease-guarded) and the
/// candidate builder (which only ever touches the candidate file while the live
/// database keeps serving).
/// </summary>
internal static class SelfHostedSqliteFile
{
    private const int CopyBufferBytes = 128 * 1024;

    /// <summary>
    /// Test seam: invoked with the database path immediately before a SQLite
    /// connection is opened. Lets the cutover tests prove no connection is made
    /// to the live path between quiescence and the swap.
    /// </summary>
    internal static Action<string>? ConnectionOpeningForTesting { get; set; }

    internal static string ConnectionString(string databasePath, bool readOnly, bool pooling)
    {
        // Same shape as the host persistence registration ("Data Source=<path>").
        // Pool release uses the global primitive, not exact-string lookup.
        var connectionString = $"Data Source={databasePath}";
        if (readOnly)
        {
            connectionString += ";Mode=ReadOnly";
        }

        if (!pooling)
        {
            connectionString += ";Pooling=False";
        }

        return connectionString;
    }

    /// <summary>
    /// Empties every Microsoft.Data.Sqlite pool. EF scopes never prove physical
    /// connections are gone, and pool lookup is exact-string (logically
    /// equivalent connection strings can form distinct pools), so the cutover
    /// boundary uses the global primitive. Checked-out connections are
    /// unaffected; the exclusive maintenance lease is what proves active owners
    /// have drained. Closing idle pooled WAL connections can checkpoint the
    /// database, so this is only used under the exclusive lease.
    /// </summary>
    internal static void ClearPools() => SqliteConnection.ClearAllPools();

    /// <summary>
    /// Clears only the pools for the connection strings this host opens for one
    /// database. Safe to call while the live library serves because it cannot
    /// close unrelated connections; the candidate builder owns every candidate
    /// connection string, so exact lookup is complete there.
    /// </summary>
    internal static void ClearPoolFor(string databasePath)
    {
        ClearPoolForConnectionString($"Data Source={databasePath}");
        ClearPoolForConnectionString($"Data Source={databasePath};Mode=ReadOnly");
    }

    private static void ClearPoolForConnectionString(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    internal static SqliteConnection Open(string databasePath, bool readOnly)
    {
        ConnectionOpeningForTesting?.Invoke(databasePath);
        var connection = new SqliteConnection(ConnectionString(databasePath, readOnly, pooling: false));
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Quiesces the live path under the caller's exclusive lease: clear every
    /// idle pooled handle, TRUNCATE-checkpoint the WAL, close the administrative
    /// connection, then require that no sidecar remains that could hold committed
    /// writes or an open handle. Fails closed with <c>migration_activation_busy</c>.
    /// </summary>
    internal static void QuiesceLive(string databasePath)
    {
        try
        {
            ClearPools();
            using (var connection = Open(databasePath, readOnly: false))
            {
                var busy = CheckpointTruncate(connection);
                if (busy != 0)
                {
                    throw Busy("The live SQLite WAL could not be fully checkpointed. The library is busy; retry activation later.");
                }
            }

            ClearPools();
            if (RemainingSidecarProblem(databasePath) is { } problem)
            {
                throw Busy(problem);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw Busy("The live SQLite database is busy. Retry activation later.");
        }
        catch (SqliteException)
        {
            throw new MigrationActivationException(
                MigrationActivationErrorCodes.Failed,
                "The live SQLite database could not be quiesced.");
        }
    }

    private static MigrationActivationException Busy(string message) =>
        new(MigrationActivationErrorCodes.Busy, message);

    /// <summary>
    /// Reopens the activated path: clear pooled handles that still refer to the
    /// pre-switch file identity, then verify the activated file with SQLite's own
    /// integrity and foreign-key checks. Fails closed with
    /// <c>migration_activation_failed</c>.
    /// </summary>
    internal static void ReopenActivated(string databasePath)
    {
        try
        {
            ClearPools();
            if (!File.Exists(databasePath))
            {
                throw new MigrationActivationException(
                    MigrationActivationErrorCodes.Failed,
                    "The activated database file is missing.");
            }

            using (var connection = Open(databasePath, readOnly: false))
            {
                var integrity = QueryStrings(connection, "PRAGMA integrity_check;");
                if (integrity.Count != 1 || !string.Equals(integrity[0], "ok", StringComparison.Ordinal))
                {
                    throw new MigrationActivationException(
                        MigrationActivationErrorCodes.Failed,
                        "The activated database failed its integrity check.");
                }

                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA foreign_key_check;";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    throw new MigrationActivationException(
                        MigrationActivationErrorCodes.Failed,
                        "The activated database has foreign-key violations.");
                }
            }

            ClearPools();
        }
        catch (SqliteException)
        {
            throw new MigrationActivationException(
                MigrationActivationErrorCodes.Failed,
                "The activated database could not be opened or verified.");
        }
    }

    /// <summary>Returns the WAL checkpoint busy flag (0 on success).</summary>
    internal static int CheckpointTruncate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return 0; // not in WAL mode: nothing to checkpoint
        }

        return Convert.ToInt32(reader.GetValue(0));
    }

    /// <summary>
    /// Deletes a zero-length WAL remnant (safe per the activation protocol) and
    /// returns a problem description when a non-empty WAL or a live SHM remains,
    /// meaning a connection is still open or committed frames are unsafe to move.
    /// </summary>
    internal static string? RemainingSidecarProblem(string databasePath)
    {
        if (File.Exists(databasePath + "-shm"))
        {
            return "A SQLite connection is still open on the database. The library is busy; retry activation later.";
        }

        var wal = databasePath + "-wal";
        if (!File.Exists(wal))
        {
            return null;
        }

        if (new FileInfo(wal).Length > 0)
        {
            return "The SQLite WAL still contains committed frames. The library is busy; retry activation later.";
        }

        try
        {
            File.Delete(wal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A handle can still pin the remnant on Windows; fail closed typed
            // instead of leaking a raw filesystem exception.
            return "The zero-length SQLite WAL remnant could not be removed. The library is busy; retry activation later.";
        }

        return null;
    }

    /// <summary>
    /// Copies a stream to a file while computing its SHA-256, enforcing the
    /// declared length and rejecting descriptor mismatches. Used only inside the
    /// candidate area.
    /// </summary>
    internal static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        long length = 0;
        var buffer = new byte[CopyBufferBytes];
        await using (var target = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                if (length > maximumBytes)
                {
                    throw new MigrationActivationException(
                        MigrationActivationErrorCodes.Failed,
                        "The prepared import's relational payload exceeds its declared size.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>Returns every row of a single-column string projection.</summary>
    internal static List<string> QueryStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return values;
    }
}

using Microsoft.Data.Sqlite;

namespace Nostos.Backend.Services;

/// <summary>
/// Removes host credentials from a backup's database copy (issue #1525).
///
/// <para>
/// SelfHosted backups are a <c>VACUUM INTO</c> copy of the whole SQLite file,
/// so a table that holds a credential would travel in the archive even though
/// the value is Data-Protection ciphertext. The Readwise token is explicitly
/// never in a backup; deleting the row there means a restore asks for the
/// token again instead of carrying it to another machine.
/// </para>
/// </summary>
internal static class BackupSecretScrubber
{
    internal const string ReadwiseConnectionsTable = "ReadwiseConnections";

    /// <summary>Deletes host-credential rows from <paramref name="databasePath"/> in place.</summary>
    public static async Task ScrubAsync(string databasePath, CancellationToken ct = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        // An older or restored database may predate the table; there is
        // nothing to scrub then.
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
            exists.Parameters.AddWithValue("$table", ReadwiseConnectionsTable);
            if (await exists.ExecuteScalarAsync(ct) is null)
                return;
        }

        // A VACUUM INTO copy inherits the live database's journal mode. Return
        // it to the default rollback journal first, so the deletion is in the
        // single file the archive carries rather than a sidecar -wal the
        // archive would leave behind.
        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            await journal.ExecuteNonQueryAsync(ct);
        }

        await using var delete = connection.CreateCommand();
        delete.CommandText = $"DELETE FROM {ReadwiseConnectionsTable}";
        await delete.ExecuteNonQueryAsync(ct);
    }
}

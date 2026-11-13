using Microsoft.Data.Sqlite;

namespace Nostos.Backend.Services;

/// <summary>
/// Removes host-local transient/outbound credentials from a backup's database
/// copy (issues #1525, #1606).
///
/// <para>
/// SelfHosted backups are a <c>VACUUM INTO</c> copy of the whole SQLite file,
/// intended for same-installation operational recovery.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ReadwiseConnections</c> is always scrubbed: the Readwise API token is
/// explicitly documented as never kept in a SelfHosted backup, so a restore asks
/// for the token again instead of carrying it elsewhere.
/// </description></item>
/// <item><description>
/// <c>CarrelPairings</c> is always scrubbed: pairing rows are short-lived
/// operational handoffs that hold <c>TokenEncrypted</c> Data-Protection
/// ciphertext while a device polls.
/// </description></item>
/// <item><description>
/// <c>AiProviderSettings</c> (Data-Protection ciphertext bound to the host key
/// ring in <c>DataProtection:KeysPath</c>) and hash-only credential tables
/// (<c>CarrelDevices</c>, <c>McpAccessTokens</c>, <c>McpOAuthGrants</c>,
/// <c>McpOAuthTokens</c>) are intentionally preserved so same-installation
/// recovery does not discard provider configuration or disconnect paired apps;
/// <c>AiProviderSettingsService</c> degrades safely if restored without the
/// original key ring, while cross-host migration uses
/// <c>IPortableArchiveService</c> which excludes all host operational state.
/// </description></item>
/// </list>
/// </summary>
internal static class BackupSecretScrubber
{
    internal const string ReadwiseConnectionsTable = "ReadwiseConnections";
    internal const string CarrelPairingsTable = "CarrelPairings";

    /// <summary>
    /// Normalizes the copy's journal mode to <c>DELETE</c> and deletes scrubbed
    /// credential rows from <paramref name="databasePath"/> in place.
    /// </summary>
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

        // A VACUUM INTO copy inherits the live database's journal mode. Return
        // it to the default rollback journal unconditionally (even when none of
        // the scrubbed tables exist yet), so the single file the archive carries
        // is self-contained with no -wal/-shm sidecars.
        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            await journal.ExecuteNonQueryAsync(ct);
        }

        await DeleteAllIfTableExistsAsync(connection, ReadwiseConnectionsTable, ct);
        await DeleteAllIfTableExistsAsync(connection, CarrelPairingsTable, ct);
    }

    private static async Task DeleteAllIfTableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
            exists.Parameters.AddWithValue("$table", tableName);
            if (await exists.ExecuteScalarAsync(ct) is null)
                return;
        }

        await using var delete = connection.CreateCommand();
        delete.CommandText = $"DELETE FROM \"{tableName}\"";
        await delete.ExecuteNonQueryAsync(ct);
    }
}

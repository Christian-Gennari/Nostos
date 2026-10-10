using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Additive SQLite storage for alignment transcription settings. The existing
/// provider-settings entity and migrations remain unchanged.
/// </summary>
internal static class AlignmentTranscriptionSettingsStore
{
    internal sealed record StoredSettings(
        bool? Enabled,
        string? BaseUrl,
        string? Model,
        string? ApiKeyEncrypted,
        bool UseVoiceTranscription,
        string ApiStyle);

    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS AlignmentTranscriptionSettings (
            Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
            Enabled INTEGER NULL,
            BaseUrl TEXT NULL,
            Model TEXT NULL,
            ApiKeyEncrypted TEXT NULL,
            UseVoiceTranscription INTEGER NOT NULL DEFAULT 1,
            ApiStyle TEXT NOT NULL DEFAULT 'openai-multipart'
        );
        """;

    public static async Task<StoredSettings?> LoadAsync(
        NostosDbContext db,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            await EnsureTableAsync(connection, ct);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Enabled, BaseUrl, Model, ApiKeyEncrypted,
                       UseVoiceTranscription, ApiStyle
                FROM AlignmentTranscriptionSettings
                WHERE Id = 1;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return new StoredSettings(
                reader.IsDBNull(0) ? null : reader.GetInt64(0) != 0,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetString(5));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task SaveAsync(
        NostosDbContext db,
        StoredSettings settings,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            await EnsureTableAsync(connection, ct);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AlignmentTranscriptionSettings
                    (Id, Enabled, BaseUrl, Model, ApiKeyEncrypted,
                     UseVoiceTranscription, ApiStyle)
                VALUES
                    (1, @enabled, @baseUrl, @model, @key,
                     @useVoice, @apiStyle)
                ON CONFLICT(Id) DO UPDATE SET
                    Enabled = excluded.Enabled,
                    BaseUrl = excluded.BaseUrl,
                    Model = excluded.Model,
                    ApiKeyEncrypted = excluded.ApiKeyEncrypted,
                    UseVoiceTranscription = excluded.UseVoiceTranscription,
                    ApiStyle = excluded.ApiStyle;
                """;

            AddParameter(command, "@enabled",
                settings.Enabled.HasValue
                    ? (settings.Enabled.Value ? 1 : 0)
                    : DBNull.Value);
            AddParameter(command, "@baseUrl", settings.BaseUrl);
            AddParameter(command, "@model", settings.Model);
            AddParameter(command, "@key", settings.ApiKeyEncrypted);
            AddParameter(command, "@useVoice", settings.UseVoiceTranscription ? 1 : 0);
            AddParameter(command, "@apiStyle", settings.ApiStyle);

            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureTableAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CreateTableSql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

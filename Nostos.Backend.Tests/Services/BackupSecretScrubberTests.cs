using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.Services;

/// <summary>
/// The backup copy is a whole-file snapshot; the Readwise credential must not
/// travel in it (issue #1525). SQLite is real; the scrub statement is the same
/// one the backup builder runs.
/// </summary>
public sealed class BackupSecretScrubberTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nostos-backup-scrub-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task It_removes_the_readwise_row_and_leaves_library_rows_alone()
    {
        var path = Path.Combine(_directory, "backup.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE ReadwiseConnections (Id INTEGER PRIMARY KEY, TokenEncrypted TEXT NOT NULL)");
            Execute(connection, "CREATE TABLE Notes (Id TEXT PRIMARY KEY, Content TEXT)");
            Execute(connection, "INSERT INTO ReadwiseConnections VALUES (1, 'ciphertext-that-must-not-travel')");
            Execute(connection, "INSERT INTO Notes VALUES ('note-1', 'A library note')");
        }

        await BackupSecretScrubber.ScrubAsync(path);

        using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
        verify.Open();
        Scalar(verify, "SELECT COUNT(*) FROM ReadwiseConnections").Should().Be(0);
        Scalar(verify, "SELECT COUNT(*) FROM Notes").Should().Be(1);
    }

    [Fact]
    public async Task A_database_without_the_table_is_untouched()
    {
        var path = Path.Combine(_directory, "older.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE Notes (Id TEXT PRIMARY KEY)");
        }

        var act = () => BackupSecretScrubber.ScrubAsync(path);

        await act.Should().NotThrowAsync();
        using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
        verify.Open();
        Scalar(verify, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ReadwiseConnections'").Should().Be(0);
    }

    [Fact]
    public async Task It_switches_journal_mode_to_DELETE_even_when_ReadwiseConnections_is_absent()
    {
        var path = Path.Combine(_directory, "wal-no-readwise.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL");
            Execute(connection, "CREATE TABLE Notes (Id TEXT PRIMARY KEY, Content TEXT)");
            Execute(connection, "INSERT INTO Notes VALUES ('note-1', 'Kept in WAL before scrub')");
        }

        await BackupSecretScrubber.ScrubAsync(path);

        using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
        verify.Open();
        ScalarString(verify, "PRAGMA journal_mode").Should().Be("delete");
        Scalar(verify, "SELECT COUNT(*) FROM Notes").Should().Be(1);
        File.Exists(path + "-wal").Should().BeFalse();
        File.Exists(path + "-shm").Should().BeFalse();
    }

    [Fact]
    public async Task It_removes_carrel_pairings_and_preserves_ai_provider_settings()
    {
        var path = Path.Combine(_directory, "credentials.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE ReadwiseConnections (Id INTEGER PRIMARY KEY, TokenEncrypted TEXT NOT NULL)");
            Execute(connection, "CREATE TABLE CarrelPairings (Id TEXT PRIMARY KEY, Code TEXT NOT NULL, TokenEncrypted TEXT)");
            Execute(connection, "CREATE TABLE AiProviderSettings (Id INTEGER PRIMARY KEY, LlmApiKeyEncrypted TEXT)");
            Execute(connection, "INSERT INTO ReadwiseConnections VALUES (1, 'readwise-ciphertext')");
            Execute(connection, "INSERT INTO CarrelPairings VALUES ('pair-1', 'ABC-123', 'carrel-pairing-ciphertext')");
            Execute(connection, "INSERT INTO AiProviderSettings VALUES (1, 'ai-provider-ciphertext')");
        }

        await BackupSecretScrubber.ScrubAsync(path);

        using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
        verify.Open();
        Scalar(verify, "SELECT COUNT(*) FROM ReadwiseConnections").Should().Be(0);
        Scalar(verify, "SELECT COUNT(*) FROM CarrelPairings").Should().Be(0);
        Scalar(verify, "SELECT COUNT(*) FROM AiProviderSettings").Should().Be(1);
        ScalarString(verify, "SELECT LlmApiKeyEncrypted FROM AiProviderSettings WHERE Id = 1")
            .Should().Be("ai-provider-ciphertext");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }
}

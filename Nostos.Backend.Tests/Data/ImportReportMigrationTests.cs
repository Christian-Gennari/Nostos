using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// #1522 adds a nullable ReportJson column to NoteImportBatches. The suite
// builds schema with EnsureCreated and never runs migrations (AGENTS §7), so
// this test runs the real migration on a real SQLite file: a batch written
// before the column existed survives the upgrade, with no invented report.
public sealed class ImportReportMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261110155410_AddBookImportBatches";

    private readonly List<string> _directories = new();

    [Fact]
    public async Task AddImportReport_adds_a_nullable_column_and_keeps_old_batches()
    {
        var path = Path.Combine(NewDirectory(), "imports.db");
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path}",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

        string migrationId;

        // A database whose schema and history stop at the previous migration.
        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddImportReport"));

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations())
            {
                await db.Database.ExecuteSqlRawAsync(
                    history.GetInsertScript(new HistoryRow(id, "10.0.0")));
            }
        }

        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            (await db.Database.GetPendingMigrationsAsync()).First().Should().Be(migrationId);
        }

        (await ColumnsAsync(path)).Should().NotContain("ReportJson");

        var batchId = Guid.NewGuid();
        await using (var db = new NostosDbContext(options))
        {
            // Raw SQL: the current model cannot write a table without the column.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO NoteImportBatches (Id, Source, FileName, CreatedAtUtc)
                VALUES ({0}, 'kindle', 'My Clippings.txt', {1})
                """,
                batchId,
                new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(migrationId);
            (await db.Database.GetPendingMigrationsAsync()).Should().NotContain(migrationId);
            var batch = await db.NoteImportBatches.SingleAsync();
            batch.Id.Should().Be(batchId);
            batch.Source.Should().Be("kindle");
            batch.ReportJson.Should().BeNull("a batch from before the report must not gain invented numbers");
        }

        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            (await ColumnsAsync(path)).Should().NotContain("ReportJson");
            (await db.NoteImportBatches.CountAsync()).Should().Be(1, "undoing the schema does not lose the batch");
        }
    }

    private static async Task<List<string>> ColumnsAsync(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(NoteImportBatches);";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private string NewDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("nostos-import-report-migration-").FullName;
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

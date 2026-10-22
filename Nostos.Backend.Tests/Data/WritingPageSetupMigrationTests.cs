using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// AddWritingPageSetup (issue #1164) must be purely additive. The rest of the
// suite builds its schema with EnsureCreated, which never runs a migration, so
// the migration is exercised here against a real SQLite file: every writing
// keeps its name, tree and words, the new column arrives empty, and Down
// removes only that column.
public sealed class WritingPageSetupMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261021062134_AddTopicNormalizedName";
    private const string Column = "PageSetup";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nostos-page-setup-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task Additive_migration_upgrades_and_downgrades_a_real_sqlite_database()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "library.db");
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

        string migrationId;

        // 1. A database at the current schema with a complete history, holding
        //    a folder with a document in it.
        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddWritingPageSetup"));
            db.Database.GetMigrations().Should().Contain(PreviousMigration, "the migration upgrades from this one");

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));

            var folder = new WritingModel { Id = Guid.NewGuid(), Name = "Thesis", Type = WritingType.Folder };
            db.Writings.AddRange(
                folder,
                new WritingModel { Id = Guid.NewGuid(), Name = "Chapter one", Type = WritingType.Document, Content = "# One\n\nمرحبا", ParentId = folder.Id },
                new WritingModel { Id = Guid.NewGuid(), Name = "Loose page", Type = WritingType.Document, Content = "Words." });
            await db.SaveChangesAsync();
        }

        // 2. Roll back to the schema users have today.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            (await db.Database.GetPendingMigrationsAsync()).Should().StartWith(migrationId);
        }

        (await ColumnsAsync(path)).Should().NotContain(Column);
        var before = await SnapshotAsync(path);
        before.Should().HaveCount(3);

        // 3. Up: every writing keeps every value; none has a page setup yet.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(migrationId);
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        (await ColumnsAsync(path)).Should().Contain(Column);
        (await SnapshotAsync(path)).Should().Equal(before, "every existing writing keeps every value");

        await using (var db = new NostosDbContext(options))
        {
            (await db.Writings.AsNoTracking().ToListAsync()).Should().OnlyContain(w => w.PageSetup == null);
            var folder = await db.Writings.SingleAsync(w => w.Name == "Thesis");
            folder.PageSetup = "{\"paper\":\"a4\"}";
            await db.SaveChangesAsync();
        }

        // 4. Down removes only the column.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await ColumnsAsync(path)).Should().NotContain(Column);
        (await SnapshotAsync(path)).Should().Equal(before);
    }

    private static async Task<List<string>> ColumnsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Writings\")";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>Every writing's id, name, type, parent and content, read without the model.</summary>
    private static async Task<List<string>> SnapshotAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\", \"Name\", \"Type\", \"ParentId\", \"Content\" FROM \"Writings\" ORDER BY \"Id\"";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetValue(0)}|{reader.GetString(1)}|{reader.GetValue(2)}|{reader.GetValue(3)}|{reader.GetValue(4)}");
        return rows;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}

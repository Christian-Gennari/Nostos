using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// AddTopicKinds creates TopicKinds with six built-in rows and adds nullable
// KindId / KindSource columns to Topics. Because the rest of the suite uses
// EnsureCreated, Up() and Down() are exercised here against a real SQLite file.
public sealed class TopicKindsMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261113123425_ConvertLegacyPhysicalBooksWithFiles";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nostos-topic-kinds-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task AddTopicKinds_migration_upgrades_and_downgrades_a_real_sqlite_database()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "library.db");
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

        string migrationId;

        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddTopicKinds"));
            db.Database.GetMigrations().Should().Contain(PreviousMigration);

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));

            var work = new WorkModel { Id = Guid.NewGuid(), Title = "Republic", NormalizedTitle = "REPUBLIC", NormalizedAuthor = string.Empty };
            var book = new PhysicalBookModel { Id = Guid.NewGuid(), WorkId = work.Id, Work = work, Title = "Republic" };
            var note = new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Content = "[[Justice]] [[Plato]]", CreatedAt = DateTime.UtcNow };
            var justice = new TopicModel { Id = Guid.NewGuid(), Topic = "Justice" };
            var plato = new TopicModel { Id = Guid.NewGuid(), Topic = "Plato" };
            var loose = new TopicModel { Id = Guid.NewGuid(), Topic = "Untouched" };
            db.Works.Add(work);
            db.Books.Add(book);
            db.Notes.Add(note);
            db.Topics.AddRange(justice, plato, loose);
            db.NoteTopics.AddRange(
                new NoteTopicModel { NoteId = note.Id, TopicId = justice.Id },
                new NoteTopicModel { NoteId = note.Id, TopicId = plato.Id });
            await db.SaveChangesAsync();
        }

        // Roll back to the pre-1767 schema.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            (await db.Database.GetPendingMigrationsAsync()).Should().Equal(migrationId);
        }

        (await TableExistsAsync(path, "TopicKinds")).Should().BeFalse();
        (await ColumnsAsync(path, "Topics")).Should().NotContain("KindId").And.NotContain("KindSource");
        var beforeTopics = await SnapshotTopicsAsync(path);
        beforeTopics.Should().HaveCount(3);
        var noteTopicCount = await CountAsync(path, "NoteTopics");

        // Up(): creates TopicKinds with 6 seeds, adds KindId/KindSource, leaves existing topics untyped.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(migrationId);
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        (await TableExistsAsync(path, "TopicKinds")).Should().BeTrue();
        (await ColumnsAsync(path, "Topics")).Should().Contain("KindId").And.Contain("KindSource");
        (await SnapshotTopicsAsync(path)).Should().Equal(beforeTopics);
        (await CountAsync(path, "NoteTopics")).Should().Be(noteTopicCount);

        await using (var db = new NostosDbContext(options))
        {
            var kinds = await db.TopicKinds.AsNoTracking().OrderBy(k => k.SortOrder).ToListAsync();
            TopicKindModel.MatchesBuiltInDefaults(kinds).Should().BeTrue();

            var topics = await db.Topics.ToListAsync();
            topics.Should().OnlyContain(t => t.KindId == null && t.KindSource == null);

            var plato = topics.Single(t => t.Topic == "Plato");
            plato.KindId = TopicKindModel.PersonId;
            plato.KindSource = TopicKindSources.Reader;
            await db.SaveChangesAsync();
        }

        // Down(): drops FK, TopicKinds table, index, and KindId/KindSource columns cleanly.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await TableExistsAsync(path, "TopicKinds")).Should().BeFalse();
        (await ColumnsAsync(path, "Topics")).Should().NotContain("KindId").And.NotContain("KindSource");
        (await SnapshotTopicsAsync(path)).Should().Equal(beforeTopics);
        (await CountAsync(path, "NoteTopics")).Should().Be(noteTopicCount);
    }

    private static async Task<bool> TableExistsAsync(string path, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task<List<string>> ColumnsAsync(string path, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<long> CountAsync(string path, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> SnapshotTopicsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\", \"Topic\" FROM \"Topics\" ORDER BY \"Id\"";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add($"{reader.GetValue(0)}|{reader.GetString(1)}");
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

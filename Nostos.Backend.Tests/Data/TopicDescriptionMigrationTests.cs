using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// AddTopicDescription must be purely additive. The rest of the suite builds its
// schema with EnsureCreated, which never runs a migration, so the migration is
// exercised here against a real SQLite file: every topic keeps its name and
// links, the new column arrives empty, and Down removes only that column.
public sealed class TopicDescriptionMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261016150045_AddNotePassages";
    private const string Column = "Description";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nostos-topic-words-migration-{Guid.NewGuid():N}");

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
        //    topics linked to a note, and one topic no note carries.
        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddTopicDescription"));
            db.Database.GetMigrations().Should().Contain(PreviousMigration, "the migration upgrades from this one");

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));

            var work = new WorkModel { Id = Guid.NewGuid(), Title = "Ethics", NormalizedTitle = "ETHICS", NormalizedAuthor = string.Empty };
            var book = new PhysicalBookModel { Id = Guid.NewGuid(), WorkId = work.Id, Work = work, Title = "Ethics" };
            var note = new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Content = "[[Virtue]] [[Wisdom]]", CreatedAt = DateTime.UtcNow };
            var virtue = new TopicModel { Id = Guid.NewGuid(), Topic = "Virtue" };
            var wisdom = new TopicModel { Id = Guid.NewGuid(), Topic = "Wisdom" };
            db.Works.Add(work);
            db.Books.Add(book);
            db.Notes.Add(note);
            db.Topics.AddRange(virtue, wisdom, new TopicModel { Id = Guid.NewGuid(), Topic = "Loose" });
            db.NoteTopics.AddRange(
                new NoteTopicModel { NoteId = note.Id, TopicId = virtue.Id },
                new NoteTopicModel { NoteId = note.Id, TopicId = wisdom.Id });
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
        var links = await CountAsync(path, "NoteTopics");

        // 3. Up: every topic keeps its name and links; nobody has words yet.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(migrationId);
            (await db.Database.GetPendingMigrationsAsync()).Should().NotContain(migrationId);
        }

        (await ColumnsAsync(path)).Should().Contain(Column);
        (await SnapshotAsync(path)).Should().Equal(before, "every existing topic keeps every value");
        (await CountAsync(path, "NoteTopics")).Should().Be(links);

        await using (var db = new NostosDbContext(options))
        {
            // A later migration's column the current model reads (LaterBookColumns).
            await LaterBookColumns.RestoreTopicNormalizedNameAsync(db);
            (await db.Topics.AsNoTracking().ToListAsync()).Should().OnlyContain(t => t.Description == null);
            var virtue = await db.Topics.SingleAsync(t => t.Topic == "Virtue");
            virtue.Description = "Living well.";
            await db.SaveChangesAsync();
        }

        // 4. Down removes only the column.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await ColumnsAsync(path)).Should().NotContain(Column);
        (await SnapshotAsync(path)).Should().Equal(before);
        (await CountAsync(path, "NoteTopics")).Should().Be(links);
    }

    private static async Task<List<string>> ColumnsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Topics\")";
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

    /// <summary>Every topic's id and name, read without the model.</summary>
    private static async Task<List<string>> SnapshotAsync(string path)
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

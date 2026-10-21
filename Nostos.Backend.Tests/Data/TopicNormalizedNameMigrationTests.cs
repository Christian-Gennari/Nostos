using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// AddTopicNormalizedName makes a topic one per name in any case (issue #1116).
// A library from before it can hold [[gulligt]] and [[Gulligt]] as two topics,
// so the upgrade must give every topic a key without breaking the new unique
// index, and startup (DatabaseBootstrapService) then merges the variants.
// SQLite's upper() folds ASCII only: Äpple/äpple get distinct keys from the
// migration and are joined by the same merge.
public sealed class TopicNormalizedNameMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261020072417_AddCoverVersion";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nostos-topic-key-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task Upgrade_keys_every_topic_and_startup_merges_the_case_variants()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "library.db");
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

        string migrationId;
        var noteIds = new Guid[4];

        // 1. A current database with a full history and one book with notes,
        //    rolled back to the schema users have today.
        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddTopicNormalizedName"));
            db.Database.GetMigrations().Should().Contain(PreviousMigration, "the migration upgrades from this one");

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));

            var work = new WorkModel { Id = Guid.NewGuid(), Title = "Ethics", NormalizedTitle = "ETHICS", NormalizedAuthor = string.Empty };
            var book = new PhysicalBookModel { Id = Guid.NewGuid(), WorkId = work.Id, Work = work, Title = "Ethics" };
            db.Works.Add(work);
            db.Books.Add(book);
            var contents = new[]
            {
                "One [[Gulligt]] and [[Äpple]]",
                "Two [[Gulligt]]",
                "Three [[gulligt]] [[äpple]]",
                "Four [[Virtue]]",
            };
            for (var i = 0; i < contents.Length; i++)
            {
                var note = new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Content = contents[i], CreatedAt = DateTime.UtcNow };
                noteIds[i] = note.Id;
                db.Notes.Add(note);
            }
            await db.SaveChangesAsync();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        // 2. Topics as the old schema allowed them: unique only byte for byte.
        var gulligt = Guid.NewGuid();
        var gulligtLower = Guid.NewGuid();
        var gulligtUpper = Guid.NewGuid();
        var apple = Guid.NewGuid();
        var appleLower = Guid.NewGuid();
        var virtue = Guid.NewGuid();
        await ExecuteAsync(path,
            Topic(gulligt, "Gulligt", null),
            Topic(gulligtLower, "gulligt", "Sweet."),
            Topic(gulligtUpper, "GULLIGT", null),
            Topic(apple, "Äpple", null),
            Topic(appleLower, "äpple", null),
            Topic(virtue, "Virtue", null),
            Link(noteIds[0], gulligt), Link(noteIds[1], gulligt), Link(noteIds[2], gulligtLower),
            Link(noteIds[0], apple), Link(noteIds[2], appleLower),
            Link(noteIds[3], virtue));

        // 3. Up: every topic is kept and keyed; variants wait under pending keys.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(migrationId);
        }

        var keys = await KeysAsync(path);
        keys.Should().HaveCount(6);
        keys[gulligt].Should().Be("GULLIGT", "the variant on the most notes keeps the key");
        keys[gulligtLower].Should().StartWith("GULLIGT|");
        keys[gulligtUpper].Should().StartWith("GULLIGT|");
        keys[virtue].Should().Be("VIRTUE");
        keys[apple].Should().Be("ÄPPLE");
        keys[appleLower].Should().Be("äPPLE", "SQLite folds ASCII only; the merge pass finishes the fold");

        // 4. Startup merges every variant into one topic per name.
        await using (var db = new NostosDbContext(options))
        {
            await new DatabaseBootstrapService(db).EnsureReadyAsync();
        }

        await using (var db = new NostosDbContext(options))
        {
            var topics = await db.Topics.AsNoTracking().Include(t => t.NoteTopics).ToListAsync();
            topics.Select(t => (t.Id, t.Topic, t.NormalizedName, t.NoteTopics.Count)).Should().BeEquivalentTo(new[]
            {
                (gulligt, "Gulligt", "GULLIGT", 3),
                (apple, "Äpple", "ÄPPLE", 2),
                (virtue, "Virtue", "VIRTUE", 1),
            });
            topics.Single(t => t.Id == gulligt).Description.Should().Be("Sweet.");
            (await db.Notes.SingleAsync(n => n.Id == noteIds[2])).Content.Should().Be("Three [[Gulligt]] [[Äpple]]");
        }

        // 5. Down restores the old index; the merged library fits it.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }
        (await KeysAsync(path, withKey: false)).Should().HaveCount(3);
    }

    private static string Topic(Guid id, string name, string? description) =>
        $"INSERT INTO \"Topics\" (\"Id\", \"Topic\", \"Description\") VALUES ('{id.ToString().ToUpperInvariant()}', '{name}', {(description is null ? "NULL" : $"'{description}'")})";

    private static string Link(Guid noteId, Guid topicId) =>
        $"INSERT INTO \"NoteTopics\" (\"NoteId\", \"TopicId\") VALUES ('{noteId.ToString().ToUpperInvariant()}', '{topicId.ToString().ToUpperInvariant()}')";

    private static async Task ExecuteAsync(string path, params string[] statements)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Every topic's key by id, read without the model.</summary>
    private static async Task<Dictionary<Guid, string>> KeysAsync(string path, bool withKey = true)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = withKey
            ? "SELECT \"Id\", \"NormalizedName\" FROM \"Topics\""
            : "SELECT \"Id\", \"Topic\" FROM \"Topics\"";
        var keys = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            keys[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
        return keys;
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

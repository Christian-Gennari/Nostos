using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class TopicRepositoryMergeCaseVariantsTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public TopicRepositoryMergeCaseVariantsTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MergeCaseVariantsAsync_batches_multiple_groups_and_variants_in_single_save()
    {
        var dbPath = _fixture.CreateDatabasePath();
        var bookId = Guid.NewGuid();
        var gulligtId = Guid.NewGuid();
        var gulligtLowerId = Guid.NewGuid();
        var gulligtUpperId = Guid.NewGuid();
        var alphaId = Guid.NewGuid();
        var alphaLowerId = Guid.NewGuid();
        var alphaUpperId = Guid.NewGuid();
        var soloUmlautId = Guid.NewGuid();
        var sharedNoteId = Guid.NewGuid();
        var alphaNoteId = Guid.NewGuid();

        await using (var seedDb = _fixture.CreateContext(dbPath))
        {
            var book = new PhysicalBookModel { Id = bookId, Title = "Case Merge Book" };
            seedDb.Books.Add(book);

            var n1 = new NoteModel { Id = Guid.NewGuid(), BookId = bookId, Content = "One [[Gulligt]]", CreatedAt = DateTime.UtcNow };
            var n2 = new NoteModel { Id = Guid.NewGuid(), BookId = bookId, Content = "Two [[Gulligt]] [[Alpha]]", CreatedAt = DateTime.UtcNow };
            var n3 = new NoteModel { Id = sharedNoteId, BookId = bookId, Content = "Three [[gulligt]] and [[GULLIGT]]", CreatedAt = DateTime.UtcNow };
            var n4 = new NoteModel { Id = alphaNoteId, BookId = bookId, Content = "Four [[Alpha]] and [[alpha]] and [[ALPHA]]", CreatedAt = DateTime.UtcNow };
            var n5 = new NoteModel { Id = Guid.NewGuid(), BookId = bookId, Content = "Five [[äpple]]", CreatedAt = DateTime.UtcNow };
            seedDb.Notes.AddRange(n1, n2, n3, n4, n5);

            var gulligt = new TopicModel { Id = gulligtId, Topic = "Gulligt", NormalizedName = "GULLIGT" };
            var gulligtLower = new TopicModel { Id = gulligtLowerId, Topic = "gulligt", NormalizedName = "GULLIGT|1", Description = "Sweet." };
            var gulligtUpper = new TopicModel { Id = gulligtUpperId, Topic = "GULLIGT", NormalizedName = "GULLIGT|2" };

            var alpha = new TopicModel { Id = alphaId, Topic = "Alpha", NormalizedName = "ALPHA", Description = "First." };
            var alphaLower = new TopicModel { Id = alphaLowerId, Topic = "alpha", NormalizedName = "ALPHA|1" };
            var alphaUpper = new TopicModel { Id = alphaUpperId, Topic = "ALPHA", NormalizedName = "ALPHA|2", Description = "Caps." };

            // Single row with SQLite ASCII-only upper() key: no duplicate to merge, just key normalization.
            var soloUmlaut = new TopicModel { Id = soloUmlautId, Topic = "äpple", NormalizedName = "äPPLE" };

            seedDb.Topics.AddRange(gulligt, gulligtLower, gulligtUpper, alpha, alphaLower, alphaUpper, soloUmlaut);
            seedDb.NoteTopics.AddRange(
                new NoteTopicModel { NoteId = n1.Id, TopicId = gulligtId },
                new NoteTopicModel { NoteId = n2.Id, TopicId = gulligtId },
                new NoteTopicModel { NoteId = sharedNoteId, TopicId = gulligtLowerId },
                new NoteTopicModel { NoteId = sharedNoteId, TopicId = gulligtUpperId },
                new NoteTopicModel { NoteId = n2.Id, TopicId = alphaId },
                new NoteTopicModel { NoteId = alphaNoteId, TopicId = alphaId },
                new NoteTopicModel { NoteId = alphaNoteId, TopicId = alphaLowerId },
                new NoteTopicModel { NoteId = n5.Id, TopicId = soloUmlautId });

            await seedDb.SaveChangesAsync();
        }

        var commands = new CommandRecorder();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .AddInterceptors(commands)
            .Options;

        long revisionBefore;
        long revisionAfter;
        int merged;

        await using (var db = new NostosDbContext(options))
        {
            revisionBefore = await ReadRevisionAsync(db);
            commands.Clear();

            merged = await new TopicRepository(db).MergeCaseVariantsAsync();

            var batchCommands = commands.Sent.ToList();
            revisionAfter = await ReadRevisionAsync(db);

            merged.Should().Be(4, "2 variants of Gulligt and 2 variants of Alpha should be merged away");
            revisionAfter.Should().Be(revisionBefore + 1,
                "batched merge must commit in a single SaveChanges/transaction and advance LibraryState once");

            // Verify batched query + save command counts (no N+1 per-variant queries or saves).
            var libraryStateUpdates = batchCommands
                .Count(sql => sql.Contains("LibraryStates", StringComparison.Ordinal)
                    && (sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                        || sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)));
            libraryStateUpdates.Should().Be(1);

            var topicSelects = batchCommands
                .Count(sql => sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("\"Topics\"", StringComparison.Ordinal));
            topicSelects.Should().Be(2, "expected 1 stale-detection projection query and 1 batched WithLinkedNotes query");
        }

        await using (var verifyDb = _fixture.CreateContext(dbPath))
        {
            var topics = await verifyDb.Topics.AsNoTracking()
                .Include(t => t.NoteTopics)
                .OrderBy(t => t.Topic)
                .ToListAsync();

            topics.Select(t => (t.Id, t.Topic, t.NormalizedName, t.NoteTopics.Count))
                .Should().BeEquivalentTo(new[]
                {
                    (alphaId, "Alpha", "ALPHA", 2),
                    (gulligtId, "Gulligt", "GULLIGT", 3),
                    (soloUmlautId, "äpple", "ÄPPLE", 1),
                });

            topics.Single(t => t.Id == gulligtId).Description.Should().Be("Sweet.");
            topics.Single(t => t.Id == alphaId).Description.Should().Be("First.\n\nCaps.");

            var sharedNote = await verifyDb.Notes.AsNoTracking().SingleAsync(n => n.Id == sharedNoteId);
            sharedNote.Content.Should().Be("Three [[Gulligt]] and [[Gulligt]]");

            var alphaNote = await verifyDb.Notes.AsNoTracking().SingleAsync(n => n.Id == alphaNoteId);
            alphaNote.Content.Should().Be("Four [[Alpha]] and [[Alpha]] and [[Alpha]]");
        }
    }

    [Fact]
    public async Task MergeCaseVariantsAsync_handles_key_collision_atomically_when_deleted_variant_holds_survivor_key()
    {
        var dbPath = _fixture.CreateDatabasePath();
        var survivorId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        var noteWithDeletedTopicId = Guid.NewGuid();

        await using (var seedDb = _fixture.CreateContext(dbPath))
        {
            var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Umlaut Book" };
            seedDb.Books.Add(book);

            var n1 = new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Content = "One [[äpple]]", CreatedAt = DateTime.UtcNow };
            var n2 = new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Content = "Two [[äpple]]", CreatedAt = DateTime.UtcNow };
            var n3 = new NoteModel { Id = noteWithDeletedTopicId, BookId = book.Id, Content = "Three [[Äpple]]", CreatedAt = DateTime.UtcNow };
            seedDb.Notes.AddRange(n1, n2, n3);

            // "äpple" has more links so it wins, while "Äpple" currently holds the target "ÄPPLE" key.
            var survivor = new TopicModel { Id = survivorId, Topic = "äpple", NormalizedName = "äPPLE" };
            var deleted = new TopicModel { Id = deletedId, Topic = "Äpple", NormalizedName = "ÄPPLE", Description = "From Äpple." };
            seedDb.Topics.AddRange(survivor, deleted);
            seedDb.NoteTopics.AddRange(
                new NoteTopicModel { NoteId = n1.Id, TopicId = survivorId },
                new NoteTopicModel { NoteId = n2.Id, TopicId = survivorId },
                new NoteTopicModel { NoteId = noteWithDeletedTopicId, TopicId = deletedId });
            await seedDb.SaveChangesAsync();
        }

        long revisionBefore;
        long revisionAfter;
        int merged;

        await using (var db = _fixture.CreateContext(dbPath))
        {
            revisionBefore = await ReadRevisionAsync(db);
            merged = await new TopicRepository(db).MergeCaseVariantsAsync();
            revisionAfter = await ReadRevisionAsync(db);
        }

        merged.Should().Be(1);
        revisionAfter.Should().Be(revisionBefore + 1, "collision transaction must advance LibraryState once");

        await using (var verifyDb = _fixture.CreateContext(dbPath))
        {
            var topic = await verifyDb.Topics.AsNoTracking()
                .Include(t => t.NoteTopics)
                .SingleAsync();

            topic.Id.Should().Be(survivorId);
            topic.Topic.Should().Be("äpple");
            topic.NormalizedName.Should().Be("ÄPPLE");
            topic.Description.Should().Be("From Äpple.");
            topic.NoteTopics.Should().HaveCount(3);

            var rewrittenNote = await verifyDb.Notes.AsNoTracking()
                .SingleAsync(n => n.Id == noteWithDeletedTopicId);
            rewrittenNote.Content.Should().Be("Three [[äpple]]");
        }
    }

    [Fact]
    public async Task MergeCaseVariantsAsync_is_noop_when_all_keys_match()
    {
        var dbPath = _fixture.CreateDatabasePath();
        await using (var seedDb = _fixture.CreateContext(dbPath))
        {
            seedDb.Topics.Add(new TopicModel { Id = Guid.NewGuid(), Topic = "Ethics", NormalizedName = "ETHICS" });
            await seedDb.SaveChangesAsync();
        }

        var commands = new CommandRecorder();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .AddInterceptors(commands)
            .Options;

        await using var db = new NostosDbContext(options);
        var revisionBefore = await ReadRevisionAsync(db);
        commands.Clear();

        var merged = await new TopicRepository(db).MergeCaseVariantsAsync();

        merged.Should().Be(0);
        commands.Sent.Should().ContainSingle(sql => sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        (await ReadRevisionAsync(db)).Should().Be(revisionBefore);
    }

    private static async Task<long> ReadRevisionAsync(NostosDbContext db)
    {
        var row = await db.LibraryStates.AsNoTracking().SingleOrDefaultAsync();
        return row is null ? 0L : long.Parse(row.StateVersion);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_sent)
                    return _sent.ToList();
            }
        }

        public void Clear()
        {
            lock (_sent)
                _sent.Clear();
        }

        private void Record(DbCommand command)
        {
            lock (_sent)
                _sent.Add(command.CommandText);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

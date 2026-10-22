using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// AddWritingCitations (issue #1162) creates the derived index empty: SQL
// cannot read Markdown, so startup (DatabaseBootstrapService) fills it from
// every writing's content. The suite builds schema with EnsureCreated and
// never runs migrations (AGENTS §7), so this runs the real migration and the
// real startup pass on a SQLite file that stops at the previous migration.
public sealed class WritingCitationsMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261021062134_AddTopicNormalizedName";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nostos-writing-citations-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task Upgrade_creates_the_index_and_startup_backfills_it_from_content()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "library.db");
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

        var book = Guid.NewGuid();
        var noteA = Guid.NewGuid();
        var noteB = Guid.NewGuid();
        var cites = Guid.NewGuid();
        var bibOnly = Guid.NewGuid();
        var folder = Guid.NewGuid();
        string migrationId;

        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddWritingCitations"));
            db.Database.GetMigrations().Should().Contain(PreviousMigration, "the migration upgrades from this one");

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));

            var bookModel = new PhysicalBookModel { Id = book, Title = "Candide", Author = "Voltaire" };
            db.Books.Add(bookModel);
            db.Notes.AddRange(
                new NoteModel { Id = noteA, BookId = book, Book = bookModel, Content = "a", CreatedAt = DateTime.UtcNow },
                new NoteModel { Id = noteB, BookId = book, Book = bookModel, Content = "b", CreatedAt = DateTime.UtcNow });
            db.Writings.AddRange(
                new WritingModel
                {
                    Id = cites,
                    Name = "Cites",
                    Type = WritingType.Document,
                    Content = $"[(V)](nostos:cite/{book}?src={noteA}) [(V)](nostos:cite/{book}?p=2&src={noteA}) " +
                        $"[(V)](nostos:cite/{book}?as=quote&src={noteB}) [(gone)](nostos:cite/{book}?src={Guid.NewGuid()})",
                },
                new WritingModel
                {
                    Id = bibOnly,
                    Name = "Bibliography",
                    Type = WritingType.Document,
                    Content = $"[^1]: [V.](nostos:cite/{book}?as=bib&src={noteA})",
                },
                new WritingModel { Id = folder, Name = "Folder", Type = WritingType.Folder });
            await db.SaveChangesAsync();

            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await TablesAsync(options)).Should().NotContain("WritingCitations");

        // Startup on the existing library: migrate, then the backfill pass.
        await using (var db = new NostosDbContext(options))
        {
            await new DatabaseBootstrapService(db).EnsureReadyAsync();
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(migrationId);
        }

        await using (var db = new NostosDbContext(options))
        {
            var rows = await db.WritingCitations.AsNoTracking()
                .Select(c => new { c.WritingId, c.NoteId })
                .ToListAsync();
            rows.Should().BeEquivalentTo(new[]
            {
                new { WritingId = cites, NoteId = noteA },
                new { WritingId = cites, NoteId = noteB },
            });

            // Idempotent: a second boot finds nothing to change.
            (await Nostos.Backend.Services.Writings.WritingCitationIndex.RebuildAllAsync(db)).Should().Be(0);

            // A stale row (as if written by an older build) is repaired.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"WritingCitations\" (\"WritingId\", \"NoteId\") VALUES ({0}, {1})",
                bibOnly, noteA);
        }

        await using (var db = new NostosDbContext(options))
        {
            await new DatabaseBootstrapService(db).EnsureReadyAsync();
            (await db.WritingCitations.CountAsync(c => c.WritingId == bibOnly)).Should().Be(0);
            (await db.WritingCitations.CountAsync()).Should().Be(2);
        }

        // Down drops the derived table; nothing user-owned is lost.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }
        (await TablesAsync(options)).Should().NotContain("WritingCitations");
    }

    private static async Task<List<string>> TablesAsync(DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        return await db.Database
            .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();
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
            // Best effort.
        }
    }
}

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// #861 AddNoteHighlightColour carries a hand-written backfill in both
// directions, and the suite builds schema with EnsureCreated and never runs
// migrations (AGENTS §7). This test runs the real migration on a real SQLite
// file: a database at the previous migration is seeded with notes in every
// location shape, migrated up (PDF pens copied out of the location JSON,
// nothing else touched), recoloured, and migrated down again (pens written
// back into the PDF JSON before the column goes).
public sealed class NoteHighlightColourMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261011011628_AddAlignmentAnchors";

    private const string EpubCfi = "epubcfi(/6/4!/4/2,/1:0,/1:5)";
    private const string MalformedJson = "{\"pageNumber\":3,\"colour\":\"sage\"";

    private readonly List<string> _directories = new();

    [Fact]
    public async Task Migration_copies_pdf_pens_up_and_writes_them_back_down_on_a_real_sqlite_file()
    {
        var path = Path.Combine(NewDirectory(), "notes.db");
        var options = FileOptions(path);
        string migrationId;

        // 1. A database whose schema and history stop at the previous
        //    migration: EnsureCreated, stamp the full history, roll back one.
        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddNoteHighlightColour"));

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
            // Later migrations are pending too; this one must be the next.
            (await db.Database.GetPendingMigrationsAsync()).First().Should().Be(migrationId);
        }

        (await NoteColumnsAsync(path)).Should().NotContain("HighlightColour");

        // 2. Notes as they were stored before the column existed. Raw SQL: the
        //    current model cannot write a Notes table without the column.
        Guid bookId;
        await using (var db = new NostosDbContext(options))
        {
            // The current model writes the later cover column on every book.
            await LaterBookColumns.RestoreCoverVersionAsync(db);
            var work = new WorkModel
            {
                Id = Guid.NewGuid(),
                Title = "A Book",
                Author = "An Author",
                NormalizedTitle = "A BOOK",
                NormalizedAuthor = "AN AUTHOR",
                CreatedAt = DateTime.UtcNow,
            };
            var book = new EBookModel
            {
                Id = Guid.NewGuid(),
                WorkId = work.Id,
                Work = work,
                Title = "A Book",
                Author = "An Author",
                CreatedAt = DateTime.UtcNow,
            };
            db.Works.Add(work);
            db.Books.Add(book);
            await db.SaveChangesAsync();
            bookId = book.Id;
        }

        var seeded = new (string Tag, string? CfiRange)[]
        {
            ("pdf-amber", PdfLocation("\"amber\"")),
            ("pdf-sage", PdfLocation("\"sage\"")),
            ("pdf-blue", PdfLocation("\"blue\"")),
            ("pdf-rose", PdfLocation("\"rose\"")),
            ("pdf-no-colour", "{\"pageNumber\":3,\"rects\":[]}"),
            ("pdf-gray", PdfLocation("\"gray\"")),
            ("pdf-numeric", PdfLocation("7")),
            ("malformed", MalformedJson),
            ("json-array", "[\"sage\"]"),
            ("epub", EpubCfi),
            ("no-location", null),
        };
        foreach (var (tag, cfiRange) in seeded)
            await InsertLegacyNoteAsync(path, bookId, tag, cfiRange);

        // 3. Up: only a PDF location carrying one of the four pen names fills
        //    the column; every location is left exactly as it was.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(migrationId);
            (await db.Database.GetPendingMigrationsAsync()).Should().NotContain(migrationId);
            // The current model also reads the later passage columns.
            await LaterBookColumns.RestorePassageColumnsAsync(db);
        }

        await using (var db = new NostosDbContext(options))
        {
            var notes = await db.Notes.AsNoTracking().ToDictionaryAsync(n => n.Content);
            notes.Should().HaveCount(seeded.Length);
            notes.ToDictionary(pair => pair.Key, pair => pair.Value.HighlightColour)
                .Should().Equal(new Dictionary<string, string?>
                {
                    ["pdf-amber"] = "amber",
                    ["pdf-sage"] = "sage",
                    ["pdf-blue"] = "blue",
                    ["pdf-rose"] = "rose",
                    ["pdf-no-colour"] = null,
                    ["pdf-gray"] = null,
                    ["pdf-numeric"] = null,
                    ["malformed"] = null,
                    ["json-array"] = null,
                    ["epub"] = null,
                    ["no-location"] = null,
                });
            foreach (var (tag, cfiRange) in seeded)
                notes[tag].CfiRange.Should().Be(cfiRange, "the backfill reads a location and never rewrites it");
        }

        // 4. Recolours made while the column exists, in every shape.
        await using (var db = new NostosDbContext(options))
        {
            var notes = await db.Notes.ToDictionaryAsync(n => n.Content);
            notes["pdf-amber"].HighlightColour = "rose";
            notes["pdf-no-colour"].HighlightColour = "blue";
            notes["epub"].HighlightColour = "sage";
            notes["malformed"].HighlightColour = "sage";
            notes["json-array"].HighlightColour = "sage";
            notes["no-location"].HighlightColour = "sage";
            await db.SaveChangesAsync();
        }

        // 5. Down: a PDF location takes its pen back; anything that is not a
        //    JSON object is left alone rather than broken.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await NoteColumnsAsync(path)).Should().NotContain("HighlightColour");
        var after = await LocationsAsync(path);
        after.Should().HaveCount(seeded.Length);
        ColourOf(after["pdf-amber"]).Should().Be("rose", "the recolour survives the rollback");
        ColourOf(after["pdf-no-colour"]).Should().Be("blue");
        ColourOf(after["pdf-sage"]).Should().Be("sage");
        ColourOf(after["pdf-blue"]).Should().Be("blue");
        ColourOf(after["pdf-rose"]).Should().Be("rose");
        after["pdf-gray"].Should().Be(PdfLocation("\"gray\""));
        after["pdf-numeric"].Should().Be(PdfLocation("7"));
        after["epub"].Should().Be(EpubCfi);
        after["malformed"].Should().Be(MalformedJson);
        after["json-array"].Should().Be("[\"sage\"]");
        after["no-location"].Should().BeNull();
    }

    private static string PdfLocation(string colourJson) =>
        "{\"pageNumber\":3,\"rects\":[{\"left\":0.1,\"top\":0.2,\"width\":0.3,\"height\":0.02}],\"colour\":"
        + colourJson + "}";

    private static string? ColourOf(string? location)
    {
        using var document = System.Text.Json.JsonDocument.Parse(location!);
        return document.RootElement.GetProperty("colour").GetString();
    }

    private static async Task InsertLegacyNoteAsync(string path, Guid bookId, string tag, string? cfiRange)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Notes (Id, Content, CfiRange, SelectedText, CreatedAt, BookId,
                               CaptureSource, ProcessingMode, SourceAnchorKind, AnchorVerified)
            VALUES ($id, $content, $cfi, 'a passage', '2026-10-11 00:00:00', $book,
                    'text', 'verbatim', 'unknown', 0)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$content", tag);
        command.Parameters.AddWithValue("$cfi", (object?)cfiRange ?? DBNull.Value);
        command.Parameters.AddWithValue("$book", bookId.ToString().ToUpperInvariant());
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task<Dictionary<string, string?>> LocationsAsync(string path)
    {
        var result = new Dictionary<string, string?>();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Content, CfiRange FROM Notes";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }

    private static async Task<List<string>> NoteColumnsAsync(string path)
    {
        var columns = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('Notes')";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static DbContextOptions<NostosDbContext> FileOptions(string path) =>
        new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path}",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nostos-861-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }
}

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// Runs the real <c>ConvertLegacyPhysicalBooksWithFiles</c> migration (tests
/// otherwise use EnsureCreated, which never executes one) over a database
/// holding the shape older builds created: <c>BookType = 'physical'</c> AND
/// <c>FileDetails_HasFile = 1</c>.
///
/// The rows are inserted with raw SQL, deliberately: the save-time invariant
/// that refuses this shape (see <c>PhysicalBookHasNoFileTests</c>) makes it
/// impossible to create through the model, which is exactly why the fix has to
/// be a data migration.
/// </summary>
public sealed class ConvertLegacyPhysicalBooksWithFilesMigrationTests : IClassFixture<SqliteTestFixture>
{
    private const string TargetMigration = "ConvertLegacyPhysicalBooksWithFiles";
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestFixture _fixture;

    public ConvertLegacyPhysicalBooksWithFilesMigrationTests(SqliteTestFixture fixture) => _fixture = fixture;

    private sealed record Seed(
        Guid Id,
        string Title,
        string Type,
        string? FileName,
        bool HasFile,
        string? PhysicalIsbn = null,
        int? PhysicalPageCount = null,
        int? EbookPageCount = null,
        string? LastLocation = null,
        int Rating = 0,
        string? NormalizedIsbn = null);

    [Fact]
    public async Task A_physical_book_with_an_ebook_file_becomes_an_ebook_and_keeps_its_place_and_notes()
    {
        var pdf = new Seed(Guid.NewGuid(), "Scanned Ethics", "physical", "book.pdf", true,
            PhysicalIsbn: "9780140449495", PhysicalPageCount: 329,
            LastLocation: "{\"pageNumber\":74,\"yPercent\":0,\"rects\":[]}", Rating: 4,
            NormalizedIsbn: "9780140449495");
        var epub = new Seed(Guid.NewGuid(), "Pensées", "physical", "book.epub", true,
            PhysicalIsbn: "9780140446456", PhysicalPageCount: 333,
            LastLocation: "epubcfi(/6/24!/4/66/3:716)", NormalizedIsbn: "9780140446456");
        var path = await SeedAsync(pdf, epub);

        await MigrateAsync(path);

        await using var db = Open(path);
        var books = await db.Books.AsNoTracking().ToListAsync();

        var converted = books.Single(b => b.Id == pdf.Id);
        converted.Should().BeOfType<EBookModel>();
        var ebook = (EBookModel)converted;
        ebook.Isbn.Should().Be("9780140449495", "the print ISBN has an ebook column to live in");
        ebook.PageCount.Should().Be(329, "the page count moves to the ebook's own column");
        ebook.FileDetails.HasFile.Should().BeTrue();
        ebook.FileDetails.FileName.Should().Be("book.pdf");
        ebook.Progress.LastLocation.Should().Be(pdf.LastLocation, "the reader's place is untouched");
        ebook.Progress.Rating.Should().Be(4, "the edition's own rating is untouched");
        ebook.NormalizedIsbn.Should().Be("9780140449495");
        (await PhysicalColumnsAsync(path, pdf.Id)).Should().Be((null, null), "the physical columns are cleared");

        books.Single(b => b.Id == epub.Id).Should().BeOfType<EBookModel>();
    }

    [Fact]
    public async Task An_audio_file_becomes_an_audiobook_and_the_print_identity_is_cleared()
    {
        var audio = new Seed(Guid.NewGuid(), "Meditations, read aloud", "physical", "book.m4b", true,
            PhysicalIsbn: "9780143036272", PhysicalPageCount: 176,
            LastLocation: "1234.5", NormalizedIsbn: "9780143036272");
        var path = await SeedAsync(audio);

        await MigrateAsync(path);

        await using var db = Open(path);
        var book = await db.Books.AsNoTracking().SingleAsync(b => b.Id == audio.Id);
        book.Should().BeOfType<AudioBookModel>();
        book.NormalizedIsbn.Should().BeNull("an audiobook never carries ISBN identity");
        (await PhysicalColumnsAsync(path, audio.Id)).Should().Be((null, null));
    }

    [Fact]
    public async Task Ambiguous_rows_stay_exactly_as_they_were()
    {
        var paper = new Seed(Guid.NewGuid(), "Read as paper", "physical", "book.pdf", true,
            PhysicalIsbn: "9780198752431", PhysicalPageCount: 677, LastLocation: "{\"page\":143}",
            NormalizedIsbn: "9780198752431");
        var unknown = new Seed(Guid.NewGuid(), "Opaque file", "physical", "notes.bin", true,
            PhysicalIsbn: "9780198751796", PhysicalPageCount: 877, NormalizedIsbn: "9780198751796");
        var noName = new Seed(Guid.NewGuid(), "File without a name", "physical", null, true,
            PhysicalIsbn: "9780415312078", PhysicalPageCount: 361, NormalizedIsbn: "9780415312078");
        var path = await SeedAsync(paper, unknown, noName);

        await MigrateAsync(path);

        await using var db = Open(path);
        var books = await db.Books.AsNoTracking().ToListAsync();
        foreach (var seed in new[] { paper, unknown, noName })
        {
            var book = books.Single(b => b.Id == seed.Id);
            book.Should().BeOfType<PhysicalBookModel>($"{seed.Title} is a guess this migration does not make");
            var physical = (PhysicalBookModel)book;
            physical.Isbn.Should().Be(seed.PhysicalIsbn);
            physical.PageCount.Should().Be(seed.PhysicalPageCount);
            physical.FileDetails.HasFile.Should().BeTrue();
            physical.Progress.LastLocation.Should().Be(seed.LastLocation);
        }

        paper.LastLocation!.Should().StartWith("{\"page\"", "a paper page is what makes it ambiguous");
    }

    [Fact]
    public async Task Every_supported_extension_converts_exactly_as_the_shared_classifier_says()
    {
        var seeds = BookAssetFormats.BookExtensions
            .Select((extension, index) => new Seed(
                Guid.NewGuid(),
                $"Extension {extension}",
                "physical",
                "book" + (index % 2 == 0 ? extension.ToUpperInvariant() : extension),
                true,
                PhysicalIsbn: "97800000000" + index,
                PhysicalPageCount: 100 + index,
                LastLocation: "{\"pageNumber\":2}"))
            .ToList();
        var path = await SeedAsync([.. seeds]);

        await MigrateAsync(path);

        await using var db = Open(path);
        var byId = (await db.Books.AsNoTracking().ToListAsync()).ToDictionary(b => b.Id);
        var expectations = seeds.ToDictionary(
            seed => seed.Id,
            seed => LegacyPhysicalFileBook.Resolve(seed.FileName, seed.LastLocation));

        foreach (var seed in seeds)
        {
            var expected = expectations[seed.Id];
            expected.Should().NotBe(LegacyPhysicalFileBook.Resolution.LeavePhysical,
                $"{seed.FileName} is a book extension this product accepts");

            var book = byId[seed.Id];
            var actual = book switch
            {
                EBookModel => LegacyPhysicalFileBook.Resolution.Ebook,
                AudioBookModel => LegacyPhysicalFileBook.Resolution.Audiobook,
                _ => LegacyPhysicalFileBook.Resolution.LeavePhysical,
            };
            actual.Should().Be(expected, "the migration and the import classifier must agree on {0}", seed.FileName);
        }
    }

    [Fact]
    public async Task Rows_that_are_not_the_legacy_shape_are_never_touched()
    {
        var metadataOnly = new Seed(Guid.NewGuid(), "A real paper book", "physical", null, false,
            PhysicalIsbn: "9780140442274", PhysicalPageCount: 240, NormalizedIsbn: "9780140442274");
        var ebook = new Seed(Guid.NewGuid(), "Already an ebook", "ebook", "book.epub", true,
            LastLocation: "epubcfi(/6/2!/4/2:0)");
        var path = await SeedAsync(metadataOnly, ebook);

        await MigrateAsync(path);

        await using var db = Open(path);
        var books = await db.Books.AsNoTracking().ToListAsync();

        var paper = (PhysicalBookModel)books.Single(b => b.Id == metadataOnly.Id);
        paper.Isbn.Should().Be("9780140442274");
        paper.PageCount.Should().Be(240);
        paper.FileDetails.HasFile.Should().BeFalse();

        var untouched = (EBookModel)books.Single(b => b.Id == ebook.Id);
        untouched.PageCount.Should().BeNull();
        untouched.FileDetails.FileName.Should().Be("book.epub");
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing_more()
    {
        var seeds = new[]
        {
            new Seed(Guid.NewGuid(), "Pdf", "physical", "book.pdf", true,
                PhysicalIsbn: "9780140449495", PhysicalPageCount: 329, LastLocation: "{\"pageNumber\":7}",
                NormalizedIsbn: "9780140449495"),
            new Seed(Guid.NewGuid(), "Paper", "physical", "book.pdf", true,
                PhysicalIsbn: "9780198752431", PhysicalPageCount: 677, LastLocation: "{\"page\":143}",
                NormalizedIsbn: "9780198752431"),
            new Seed(Guid.NewGuid(), "Audio", "physical", "book.mp3", true,
                PhysicalIsbn: "9780143036272", PhysicalPageCount: 176, LastLocation: "60",
                NormalizedIsbn: "9780143036272"),
        };
        var path = await SeedAsync(seeds);

        await MigrateAsync(path);
        var once = await SnapshotAsync(path);

        await RerunAsync(path);
        (await SnapshotAsync(path)).Should().Equal(once, "the migration is safe to run twice");
    }

    // ------------------------------------------------------------------

    private NostosDbContext Open(string path) => new(new DbContextOptionsBuilder<NostosDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False", sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
        .Options);

    /// <summary>
    /// A database at the current schema, holding <paramref name="seeds"/> in the
    /// legacy shape, with every migration but this one already recorded.
    /// </summary>
    private async Task<string> SeedAsync(params Seed[] seeds)
    {
        var path = _fixture.CreateDatabasePath();
        Guid workId;
        await using (var db = Open(path))
        {
            await db.Database.EnsureCreatedAsync();

            var work = new WorkModel
            {
                Id = Guid.NewGuid(),
                Title = "One Work",
                NormalizedTitle = "ONE WORK",
                NormalizedAuthor = string.Empty,
                CreatedAt = Origin,
            };
            db.Works.Add(work);
            await db.SaveChangesAsync();
            workId = work.Id;

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations().Where(id => !id.EndsWith(TargetMigration, StringComparison.Ordinal)))
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));

            (await db.Database.GetPendingMigrationsAsync()).Should().Equal(
                db.Database.GetMigrations().Single(id => id.EndsWith(TargetMigration, StringComparison.Ordinal)));
        }

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            foreach (var seed in seeds)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO "Books" (
                        "Id", "BookType", "Title", "CreatedAt", "WorkId",
                        "FileDetails_HasFile", "FileDetails_FileName",
                        "PhysicalBookModel_Isbn", "PhysicalBookModel_PageCount", "PageCount",
                        "NormalizedIsbn", "Progress_LastLocation", "Progress_Rating",
                        "Progress_ProgressPercent", "Status")
                    VALUES ($id, $type, $title, $createdAt, $workId,
                        $hasFile, $fileName,
                        $physicalIsbn, $physicalPageCount, $pageCount,
                        $normalizedIsbn, $location, $rating,
                        0, 0);
                    """;
                command.Parameters.AddWithValue("$id", seed.Id.ToString("D").ToUpperInvariant());
                command.Parameters.AddWithValue("$type", seed.Type);
                command.Parameters.AddWithValue("$title", seed.Title);
                command.Parameters.AddWithValue("$createdAt", "2026-01-01 00:00:00");
                command.Parameters.AddWithValue("$workId", workId.ToString("D").ToUpperInvariant());
                command.Parameters.AddWithValue("$hasFile", seed.HasFile ? 1 : 0);
                command.Parameters.AddWithValue("$fileName", (object?)seed.FileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$physicalIsbn", (object?)seed.PhysicalIsbn ?? DBNull.Value);
                command.Parameters.AddWithValue("$physicalPageCount", (object?)seed.PhysicalPageCount ?? DBNull.Value);
                command.Parameters.AddWithValue("$pageCount", (object?)seed.EbookPageCount ?? DBNull.Value);
                command.Parameters.AddWithValue("$normalizedIsbn", (object?)seed.NormalizedIsbn ?? DBNull.Value);
                command.Parameters.AddWithValue("$location", (object?)seed.LastLocation ?? DBNull.Value);
                command.Parameters.AddWithValue("$rating", seed.Rating);
                await command.ExecuteNonQueryAsync();
            }
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private async Task MigrateAsync(string path)
    {
        await using var db = Open(path);
        await db.Database.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    /// <summary>Runs the migration's SQL a second time, as an upgrade retry would.</summary>
    private static async Task RerunAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var delete = connection.CreateCommand();
        delete.CommandText = $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '%{TargetMigration}'";
        await delete.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();

        await using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False", sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options);
        await db.Database.MigrateAsync();
    }

    /// <summary>Every stored column of every book, read without the model.</summary>
    private static async Task<List<string>> SnapshotAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        var columns = new List<string>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(\"Books\")";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(c => $"\"{c}\""))} FROM \"Books\" ORDER BY \"Id\"";
        var rows = new List<string>();
        await using var rowReader = await command.ExecuteReaderAsync();
        while (await rowReader.ReadAsync())
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, rowReader.FieldCount)
                    .Select(i => $"{columns[i]}={(rowReader.IsDBNull(i) ? "<null>" : rowReader.GetValue(i))}")));
        }

        return rows;
    }

    /// <summary>The physical-only columns of one row, read without the model.</summary>
    private static async Task<(string? Isbn, int? PageCount)> PhysicalColumnsAsync(string path, Guid id)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "PhysicalBookModel_Isbn", "PhysicalBookModel_PageCount" FROM "Books" WHERE "Id" = $id
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1));
    }
}

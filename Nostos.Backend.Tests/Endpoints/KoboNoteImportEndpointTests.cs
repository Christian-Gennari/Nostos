using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class KoboNoteImportEndpointTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nostos-kobo-test-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Kobo_import_reports_per_book_imports_notes_and_is_idempotent()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var owned = await CreateBookAsync(client, "Synthetic Kobo Book", "Ada Reader");
        // Same title, different author formatting: a resemblance, never a match.
        await CreateBookAsync(client, "Second Synthetic Book", "Lovelace, Ada");
        var database = CreateKoboDatabase();

        var first = await UploadAsync(client, database);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await first.Content.ReadFromJsonAsync<KoboImportReport>();

        report!.BookCount.Should().Be(3);
        report.MatchedBookCount.Should().Be(1);
        report.AnnotationCount.Should().Be(5);
        report.ImportedCount.Should().Be(2);
        report.DuplicateCount.Should().Be(0);
        report.SkippedCount.Should().Be(3);

        var matched = report.Books.Single(book => book.BookId == owned.Id);
        matched.Status.Should().Be("matched");
        matched.ImportedCount.Should().Be(2);

        report.Books.Where(book => book.Status == "unmatched")
            .Select(book => book.SourceTitle)
            .Should().BeEquivalentTo("Second Synthetic Book", "Not In The Library");

        var second = await UploadAsync(client, database);
        var secondReport = await second.Content.ReadFromJsonAsync<KoboImportReport>();
        secondReport!.ImportedCount.Should().Be(0);
        secondReport.DuplicateCount.Should().Be(2);

        await using var db = CreateContext(factory.DatabasePath);
        var notes = await db.Notes.AsNoTracking().ToListAsync();

        notes.Should().HaveCount(2);
        notes.Should().OnlyContain(note =>
            note.CaptureSource == "import" && note.SourceAnchorKind == "kobo_bookmark");
        notes.Should().Contain(note =>
            note.BookId == owned.Id
            && note.SelectedText == "A synthetic Kobo highlight."
            && note.Content == "My annotation."
            && note.SourceAnchorValue == "bm-1");
        // Hidden (deleted on device), dog-ear and stylus-markup rows are not imported.
        notes.Select(note => note.SourceAnchorValue).Should().NotContain(
            ["bm-hidden", "bm-hidden-int", "bm-dogear", "bm-markup", "bm-markup-old"]);
    }

    [Fact]
    public async Task Kobo_import_does_not_resurrect_a_note_deleted_in_nostos()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Synthetic Kobo Book", "Ada Reader");
        var database = CreateKoboDatabase();
        (await UploadAsync(client, database)).StatusCode.Should().Be(HttpStatusCode.OK);

        Guid noteId;
        await using (var db = CreateContext(factory.DatabasePath))
        {
            noteId = (await db.Notes.AsNoTracking().FirstAsync(note => note.SourceAnchorValue == "bm-1")).Id;
        }
        (await client.DeleteAsync($"/api/notes/{noteId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var again = await (await UploadAsync(client, database)).Content.ReadFromJsonAsync<KoboImportReport>();
        var matched = again!.Books.Single(entry => entry.BookId == book.Id);
        matched.ImportedCount.Should().Be(0);
        matched.DuplicateCount.Should().Be(2);

        await using var verify = CreateContext(factory.DatabasePath);
        (await verify.Notes.CountAsync(note => note.BookId == book.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Kobo_import_rejects_a_file_that_is_not_a_kobo_database()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();

        var notSqlite = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(notSqlite, "this is not a database, just some text");
        (await UploadAsync(client, notSqlite)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var otherDatabase = Path.Combine(_directory, "other.sqlite");
        Execute(otherDatabase, "CREATE TABLE unrelated (id INTEGER);");
        (await UploadAsync(client, otherDatabase)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var db = CreateContext(factory.DatabasePath);
        (await db.Notes.CountAsync()).Should().Be(0);
    }

    private string CreateKoboDatabase()
    {
        var path = Path.Combine(_directory, $"KoboReader-{Guid.NewGuid():N}.sqlite");
        Execute(path,
            """
            -- Column layout of Kobo firmware 4.38.23828 (the Bookmark table in
            -- full; content reduced to the columns a book row needs).
            CREATE TABLE content (
                ContentID TEXT NOT NULL, ContentType TEXT NOT NULL, MimeType TEXT NOT NULL,
                BookID TEXT, BookTitle TEXT, Title TEXT COLLATE NOCASE,
                Attribution TEXT COLLATE NOCASE, ___UserID TEXT NOT NULL, ISBN TEXT,
                PRIMARY KEY (ContentID));
            CREATE TABLE Bookmark (
                BookmarkID TEXT NOT NULL, VolumeID TEXT NOT NULL, ContentID TEXT NOT NULL,
                StartContainerPath TEXT NOT NULL, StartContainerChildIndex INTEGER NOT NULL,
                StartOffset INTEGER NOT NULL, EndContainerPath TEXT NOT NULL,
                EndContainerChildIndex INTEGER NOT NULL, EndOffset INTEGER NOT NULL,
                Text TEXT, Annotation TEXT, ExtraAnnotationData BLOB, DateCreated TEXT,
                ChapterProgress REAL NOT NULL DEFAULT 0, Hidden BOOL NOT NULL DEFAULT 0,
                Version TEXT, DateModified TEXT, Creator TEXT, UUID TEXT, UserID TEXT,
                SyncTime TEXT, Published BIT DEFAULT false, ContextString TEXT, Type TEXT,
                PRIMARY KEY (BookmarkID));

            INSERT INTO content VALUES
                ('vol-1', '6', 'application/x-kobo-epub+zip', NULL, NULL, 'Synthetic Kobo Book', 'Ada Reader', 'user', '3f6c1c1e-not-an-isbn'),
                ('vol-1!ch1', '9', 'application/x-kobo-epub+zip', NULL, NULL, 'Chapter One', NULL, 'user', NULL),
                ('vol-2', '6', 'application/x-kobo-epub+zip', NULL, NULL, 'Second Synthetic Book', 'Ada Lovelace', 'user', NULL),
                ('vol-3', '6', 'application/x-kobo-epub+zip', NULL, NULL, 'Not In The Library', 'Nobody', 'user', NULL);

            INSERT INTO Bookmark (BookmarkID, VolumeID, ContentID, StartContainerPath,
                StartContainerChildIndex, StartOffset, EndContainerPath, EndContainerChildIndex,
                EndOffset, Text, Annotation, DateCreated, ChapterProgress, Hidden, Type) VALUES
                ('bm-1', 'vol-1', 'vol-1!ch1', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, 'A synthetic Kobo highlight.', 'My annotation.', '2026-09-28T12:00:00.000', 0.1, 'false', 'note'),
                ('bm-2', 'vol-1', 'vol-1!ch1', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, '  A second Kobo highlight.  ', '', '2026-09-28T12:00:00.000', 0.1, 'false', 'highlight'),
                ('bm-hidden', 'vol-1', 'vol-1!ch1', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, 'Deleted on the device.', '', '2026-09-28T12:00:00.000', 0.1, 'true', 'highlight'),
                ('bm-hidden-int', 'vol-1', 'vol-1!ch1', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, 'Deleted on the device.', '', '2026-09-28T12:00:00.000', 0.1, 1, 'highlight'),
                ('bm-dogear', 'vol-1', 'vol-1!ch1', 'span#kobo\.1\.1', -99, 0, 'span#kobo\.1\.1', -99, 0, 'Page text under the dog-ear.', NULL, '2026-09-28T12:00:00.000', 0.1, 'false', 'dogear'),
                ('bm-markup', 'vol-1', 'vol-1!ch1', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, NULL, NULL, '2026-09-28T12:00:00.000', 0.1, 'false', 'markup'),
                ('bm-markup-old', 'vol-1', 'vol-1!ch1', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, '###MARKUP###', NULL, '2026-09-28T12:00:00.000', 0.1, 0, NULL),
                ('bm-3', 'vol-2', 'vol-2', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, 'Matched by title alone.', NULL, '2026-09-28T12:00:00.000', 0.1, 0, 'highlight'),
                ('bm-4', 'vol-3', 'vol-3', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, 'Unmatched one.', NULL, '2026-09-28T12:00:00.000', 0.1, 0, 'highlight'),
                ('bm-5', 'vol-3', 'vol-3', 'span#kobo\.1\.1', -99, 3, 'span#kobo\.1\.3', -99, 40, 'Unmatched two.', NULL, '2026-09-28T12:00:00.000', 0.1, 0, 'highlight');
            """);
        return path;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path)
    {
        using var body = new MultipartFormDataContent();
        await using var stream = File.OpenRead(path);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        body.Add(file, "file", "KoboReader.sqlite");
        return await client.PostAsync("/api/notes/import/kobo", body);
    }

    private static async Task<BookDto> CreateBookAsync(HttpClient client, string title, string author)
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title,
            author,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private static NostosDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new NostosDbContext(options);
    }
}

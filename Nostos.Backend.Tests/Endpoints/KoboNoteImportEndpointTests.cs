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
        // Author formatting on the device differs from the library's.
        var byTitle = await CreateBookAsync(client, "Second Synthetic Book", "Lovelace, Ada");
        var database = CreateKoboDatabase();

        var first = await UploadAsync(client, database);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await first.Content.ReadFromJsonAsync<KoboImportReport>();

        report!.BookCount.Should().Be(3);
        report.MatchedBookCount.Should().Be(2);
        report.AnnotationCount.Should().Be(5);
        report.ImportedCount.Should().Be(3);
        report.DuplicateCount.Should().Be(0);
        report.SkippedCount.Should().Be(2);

        var matched = report.Books.Single(book => book.BookId == owned.Id);
        matched.Status.Should().Be("matched");
        matched.ImportedCount.Should().Be(2);
        report.Books.Single(book => book.BookId == byTitle.Id).ImportedCount.Should().Be(1);

        var unmatched = report.Books.Single(book => book.Status == "unmatched");
        unmatched.SourceTitle.Should().Be("Not In The Library");
        unmatched.AnnotationCount.Should().Be(2);
        unmatched.SkippedCount.Should().Be(2);

        var second = await UploadAsync(client, database);
        var secondReport = await second.Content.ReadFromJsonAsync<KoboImportReport>();
        secondReport!.ImportedCount.Should().Be(0);
        secondReport.DuplicateCount.Should().Be(3);

        await using var db = CreateContext(factory.DatabasePath);
        var notes = await db.Notes.AsNoTracking().ToListAsync();

        notes.Should().HaveCount(3);
        notes.Should().OnlyContain(note =>
            note.CaptureSource == "import" && note.SourceAnchorKind == "kobo_bookmark");
        notes.Should().Contain(note =>
            note.BookId == owned.Id
            && note.SelectedText == "A synthetic Kobo highlight."
            && note.Content == "My annotation."
            && note.SourceAnchorValue == "bm-1");
        // Hidden (deleted on device) and text-less dog-ear rows are not imported.
        notes.Should().NotContain(note => note.SourceAnchorValue == "bm-hidden" || note.SourceAnchorValue == "bm-dogear");
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
            CREATE TABLE content (
                ContentID TEXT NOT NULL, ContentType TEXT NOT NULL,
                Title TEXT, Attribution TEXT, ISBN TEXT);
            CREATE TABLE Bookmark (
                BookmarkID TEXT NOT NULL PRIMARY KEY, VolumeID TEXT NOT NULL, ContentID TEXT NOT NULL,
                Text TEXT, Annotation TEXT, ExtraAnnotationData BLOB, DateCreated TEXT,
                ChapterProgress REAL, Hidden BOOL, Type TEXT);

            INSERT INTO content VALUES
                ('vol-1', '6', 'Synthetic Kobo Book', 'Ada Reader', '3f6c1c1e-not-an-isbn'),
                ('vol-1!ch1', '9', 'Chapter One', NULL, NULL),
                ('vol-2', '6', 'Second Synthetic Book', 'Ada Lovelace', NULL),
                ('vol-3', '6', 'Not In The Library', 'Nobody', NULL);

            INSERT INTO Bookmark VALUES
                ('bm-1', 'vol-1', 'vol-1!ch1', 'A synthetic Kobo highlight.', 'My annotation.', NULL, '2026-09-28T12:00:00.000', 0.1, 'false', 'note'),
                ('bm-2', 'vol-1', 'vol-1!ch1', '  A second Kobo highlight.  ', NULL, NULL, '2026-09-28T12:05:00.000', 0.2, 'false', 'highlight'),
                ('bm-hidden', 'vol-1', 'vol-1!ch1', 'Deleted on the device.', NULL, NULL, '2026-09-28T12:06:00.000', 0.3, 'true', 'highlight'),
                ('bm-dogear', 'vol-1', 'vol-1!ch1', NULL, NULL, NULL, '2026-09-28T12:07:00.000', 0.4, 'false', 'dogear'),
                ('bm-3', 'vol-2', 'vol-2', 'Matched by title alone.', NULL, NULL, '2026-09-28T12:08:00.000', 0.5, 'false', 'highlight'),
                ('bm-4', 'vol-3', 'vol-3', 'Unmatched one.', NULL, NULL, '2026-09-28T12:09:00.000', 0.5, 'false', 'highlight'),
                ('bm-5', 'vol-3', 'vol-3', 'Unmatched two.', NULL, NULL, '2026-09-28T12:10:00.000', 0.6, 'false', 'highlight');
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

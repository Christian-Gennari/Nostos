using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class HighlightImportEndpointTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nostos-kobo-test-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Preview_writes_nothing_and_sorts_books_into_exact_suggested_and_none()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var owned = await CreateBookAsync(client, "Synthetic Kobo Book", "Ada Reader");
        // Same title, same surname, different first name: a resemblance.
        var resembling = await CreateBookAsync(client, "Second Synthetic Book", "Lovelace, Grace");

        var preview = await PreviewAsync(client, CreateKoboDatabase());

        preview.Source.Should().Be("kobo");
        preview.Books.Should().HaveCount(3);

        var exact = preview.Books.Single(book => book.SourceKey == "vol-1");
        exact.Match.Should().Be("exact");
        exact.BookId.Should().Be(owned.Id);
        // Hidden, dog-ear and both markup rows are not annotations.
        exact.AnnotationCount.Should().Be(2);
        exact.NewCount.Should().Be(2);

        var suggested = preview.Books.Single(book => book.SourceKey == "vol-2");
        suggested.Match.Should().Be("suggested");
        suggested.BookId.Should().BeNull("a resemblance is never chosen on the owner's behalf");
        suggested.Candidates.Should().ContainSingle().Which.BookId.Should().Be(resembling.Id);

        var none = preview.Books.Single(book => book.SourceKey == "vol-3");
        none.Match.Should().Be("none");
        none.Candidates.Should().BeEmpty();

        await using var db = CreateContext(factory.DatabasePath);
        (await db.Notes.CountAsync()).Should().Be(0);
        (await db.NoteImportBookLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Only_an_identity_is_exact_and_every_resemblance_is_a_suggestion()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var mountain = await CreateBookAsync(client, "The Magic Mountain", "Thomas Mann");
        var devils = await CreateBookAsync(client, "Devils", "Fyodor Dostoevsky");
        var emma = await CreateBookAsync(client, "Emma", "Jane Austen");

        var preview = await PreviewAsync(client, CreateKoboDatabase(
            ("exact", "The Magic Mountain", "Mann, Thomas"),
            ("article", "Magic Mountain", "Thomas Mann"),
            ("subtitle", "The Possessed; or, The Devils", "Fyodor Dostoyevsky"),
            ("other-author", "Emma", "Charlotte Bronte"),
            ("unrelated", "A Completely Different Thing", "Nobody Known")));

        HighlightImportPreviewBook Book(string key) => preview.Books.Single(book => book.SourceKey == key);

        // Name order and punctuation are not a difference of author.
        Book("exact").Match.Should().Be("exact");
        Book("exact").BookId.Should().Be(mountain.Id);

        Book("article").Match.Should().Be("suggested");
        Book("article").Candidates[0].BookId.Should().Be(mountain.Id);

        Book("subtitle").Match.Should().Be("suggested");
        Book("subtitle").Candidates[0].BookId.Should().Be(devils.Id);

        // The case the review step exists for: same title, another author.
        Book("other-author").Match.Should().Be("suggested");
        Book("other-author").BookId.Should().BeNull();
        Book("other-author").Candidates.Single().Should().Match<HighlightImportCandidate>(candidate =>
            candidate.BookId == emma.Id && candidate.Reason == "Same title, different author");

        Book("unrelated").Match.Should().Be("none");
    }

    [Fact]
    public async Task Two_editions_of_one_book_are_offered_not_chosen()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var first = await CreateBookAsync(client, "Essays and Aphorisms", "Arthur Schopenhauer", "9780141921754");
        var second = await CreateBookAsync(client, "Essays and Aphorisms", "Arthur Schopenhauer", "9780140442274", "ebook");

        var preview = await PreviewAsync(client, CreateKoboDatabase(
            ("essays", "Essays and Aphorisms", "Arthur Schopenhauer")));

        var book = preview.Books.Single();
        book.Match.Should().Be("suggested");
        book.BookId.Should().BeNull();
        book.Candidates.Select(candidate => candidate.BookId).Should().BeEquivalentTo([first.Id, second.Id]);
    }

    [Fact]
    public async Task Commit_imports_only_decided_books_remembers_them_and_is_idempotent()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var owned = await CreateBookAsync(client, "Synthetic Kobo Book", "Ada Reader");
        var resembling = await CreateBookAsync(client, "Second Synthetic Book", "Lovelace, Grace");
        var database = CreateKoboDatabase();

        var result = await CommitAsync(client, database,
            new HighlightImportDecision("vol-1", owned.Id),
            new HighlightImportDecision("vol-2", resembling.Id));

        result.BatchId.Should().NotBeNull();
        result.Books.Single(book => book.SourceKey == "vol-1").ImportedCount.Should().Be(2);
        result.Books.Single(book => book.SourceKey == "vol-2").ImportedCount.Should().Be(1);
        result.Books.Single(book => book.SourceKey == "vol-3").Status.Should().Be("skipped");

        // The confirmed resemblance is now remembered: no second confirmation.
        var preview = await PreviewAsync(client, database);
        var remembered = preview.Books.Single(book => book.SourceKey == "vol-2");
        remembered.Match.Should().Be("remembered");
        remembered.BookId.Should().Be(resembling.Id);
        remembered.NewCount.Should().Be(0);

        var again = await CommitAsync(client, database,
            new HighlightImportDecision("vol-1", owned.Id),
            new HighlightImportDecision("vol-2", resembling.Id));
        again.BatchId.Should().BeNull("an import that adds nothing has nothing to undo");
        again.Books.Sum(book => book.ImportedCount).Should().Be(0);
        again.Books.Sum(book => book.DuplicateCount).Should().Be(3);

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
        notes.Select(note => note.SourceAnchorValue).Should().NotContain(
            ["bm-hidden", "bm-hidden-int", "bm-dogear", "bm-markup", "bm-markup-old"]);
    }

    [Fact]
    public async Task Commit_can_add_a_missing_book_to_the_library()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();

        var result = await CommitAsync(client, CreateKoboDatabase(),
            new HighlightImportDecision("vol-3", Create: true));

        var created = result.Books.Single(book => book.SourceKey == "vol-3");
        created.Created.Should().BeTrue();
        created.ImportedCount.Should().Be(2);

        await using var db = CreateContext(factory.DatabasePath);
        var book = await db.Books.AsNoTracking().SingleAsync();
        book.Title.Should().Be("Not In The Library");
        book.Author.Should().Be("Nobody");
        (await db.Notes.CountAsync(note => note.BookId == book.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Undo_removes_the_batch_notes_and_lets_them_be_imported_again()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var owned = await CreateBookAsync(client, "Synthetic Kobo Book", "Ada Reader");
        var other = await CreateBookAsync(client, "A Different Book", "Someone Else");
        var database = CreateKoboDatabase();

        var first = await CommitAsync(client, database, new HighlightImportDecision("vol-1", owned.Id));

        var batches = await client.GetFromJsonAsync<List<HighlightImportBatchSummary>>("/api/notes/imports/batches");
        batches.Should().ContainSingle().Which.NoteCount.Should().Be(2);

        (await client.DeleteAsync($"/api/notes/imports/batches/{first.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"/api/notes/imports/batches/{first.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using (var db = CreateContext(factory.DatabasePath))
        {
            (await db.Notes.CountAsync()).Should().Be(0);
            (await db.NoteCommandReceipts.CountAsync()).Should().Be(0);
        }

        // The mistake being undone: the notes belonged on another book.
        var second = await CommitAsync(client, database, new HighlightImportDecision("vol-1", other.Id));
        second.Books.Single(book => book.SourceKey == "vol-1").ImportedCount.Should().Be(2);

        await using var verify = CreateContext(factory.DatabasePath);
        (await verify.Notes.CountAsync(note => note.BookId == other.Id)).Should().Be(2);
    }

    [Fact]
    public async Task A_note_deleted_in_nostos_is_not_imported_again()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Synthetic Kobo Book", "Ada Reader");
        var database = CreateKoboDatabase();
        await CommitAsync(client, database, new HighlightImportDecision("vol-1", book.Id));

        Guid noteId;
        await using (var db = CreateContext(factory.DatabasePath))
        {
            noteId = (await db.Notes.AsNoTracking().FirstAsync(note => note.SourceAnchorValue == "bm-1")).Id;
        }
        (await client.DeleteAsync($"/api/notes/{noteId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var again = await CommitAsync(client, database, new HighlightImportDecision("vol-1", book.Id));
        var matched = again.Books.Single(entry => entry.BookId == book.Id);
        matched.ImportedCount.Should().Be(0);
        matched.DuplicateCount.Should().Be(2);

        await using var verify = CreateContext(factory.DatabasePath);
        (await verify.Notes.CountAsync(note => note.BookId == book.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_koreader_sidecar_goes_through_the_same_preview_and_commit()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Synthetic Reader Book", "Ada Reader");
        var sidecar = Path.Combine(AppContext.BaseDirectory, "Fixtures", "koreader", "metadata.lua");

        var preview = await PreviewAsync(client, sidecar);
        preview.Source.Should().Be("koreader");
        var only = preview.Books.Should().ContainSingle().Subject;
        only.Match.Should().Be("exact");
        only.BookId.Should().Be(book.Id);

        var result = await CommitAsync(client, sidecar, new HighlightImportDecision(only.SourceKey, book.Id));
        result.Books.Single().ImportedCount.Should().Be(2);

        // The original single-file endpoint sees those notes as its own.
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(await File.ReadAllBytesAsync(sidecar)), "file", "metadata.lua");
        var legacy = await (await client.PostAsync("/api/notes/import/koreader", body))
            .Content.ReadFromJsonAsync<KoreaderImportReport>();
        legacy!.ImportedCount.Should().Be(0);
        legacy.DuplicateCount.Should().Be(2);
    }

    [Fact]
    public async Task A_file_that_is_neither_source_is_rejected()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();

        var notSqlite = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(notSqlite, "this is not a database, just some text");
        (await UploadAsync(client, "preview", notSqlite)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var otherDatabase = Path.Combine(_directory, "other.sqlite");
        Execute(otherDatabase, "CREATE TABLE unrelated (id INTEGER);");
        (await UploadAsync(client, "preview", otherDatabase)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await UploadAsync(client, "commit", otherDatabase)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private const string Schema =
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

        """;

    /// <summary>One highlight per (volume id, title, author) on the device.</summary>
    private string CreateKoboDatabase(params (string Volume, string Title, string Author)[] books)
    {
        var path = Path.Combine(_directory, $"KoboReader-{Guid.NewGuid():N}.sqlite");
        Execute(path, Schema);
        foreach (var (volume, title, author) in books)
        {
            Execute(path,
                $"""
                INSERT INTO content VALUES ('{volume}', '6', 'application/x-kobo-epub+zip', NULL, NULL,
                    '{title.Replace("'", "''")}', '{author.Replace("'", "''")}', 'user', NULL);
                INSERT INTO Bookmark (BookmarkID, VolumeID, ContentID, StartContainerPath,
                    StartContainerChildIndex, StartOffset, EndContainerPath, EndContainerChildIndex,
                    EndOffset, Text, Annotation, DateCreated, Hidden, Type) VALUES
                    ('bm-{volume}', '{volume}', '{volume}', 'a', -99, 3, 'b', -99, 40,
                     'A highlight.', '', '2026-09-28T12:00:00.000', 0, 'highlight');
                """);
        }

        return path;
    }

    private string CreateKoboDatabase()
    {
        var path = Path.Combine(_directory, $"KoboReader-{Guid.NewGuid():N}.sqlite");
        Execute(path, Schema +
            """
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

    private static async Task<HighlightImportPreview> PreviewAsync(HttpClient client, string path)
    {
        var response = await UploadAsync(client, "preview", path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HighlightImportPreview>())!;
    }

    private static async Task<HighlightImportResult> CommitAsync(
        HttpClient client,
        string path,
        params HighlightImportDecision[] decisions)
    {
        var response = await UploadAsync(client, "commit", path, decisions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HighlightImportResult>())!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string step,
        string path,
        HighlightImportDecision[]? decisions = null)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
        if (decisions is not null)
            body.Add(new StringContent(JsonSerializer.Serialize(decisions, JsonSerializerOptions.Web)), "decisions");
        return await client.PostAsync($"/api/notes/imports/{step}", body);
    }

    private static async Task<BookDto> CreateBookAsync(
        HttpClient client, string title, string author, string? isbn = null, string type = "physical")
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type,
            title,
            author,
            isbn,
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

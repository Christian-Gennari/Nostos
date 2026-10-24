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
        // Each candidate says its format, so the owner can tell the editions apart.
        book.Candidates.Single(candidate => candidate.BookId == first.Id).Format.Should().Be("physical");
        book.Candidates.Single(candidate => candidate.BookId == second.Id).Format.Should().Be("ebook");
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
        notes.Should().OnlyContain(note => note.HighlightColour == "sage");
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

        (await client.DeleteAsync($"/api/notes/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.Books.CountAsync()).Should().Be(1);
        (await db.Notes.CountAsync()).Should().Be(0);
        (await db.NoteImportBookLinks.SingleAsync()).BookId.Should().Be(book.Id);
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
    public async Task Files_imported_together_share_one_batch()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var kobo = await CreateBookAsync(client, "Synthetic Kobo Book", "Ada Reader");
        var koreader = await CreateBookAsync(client, "Synthetic Reader Book", "Ada Reader");
        var sidecar = Path.Combine(AppContext.BaseDirectory, "Fixtures", "koreader", "metadata.lua");
        var sidecarKey = (await PreviewAsync(client, sidecar)).Books.Single().SourceKey;

        var first = await CommitAsync(client, CreateKoboDatabase(), new HighlightImportDecision("vol-1", kobo.Id));
        var response = await UploadAsync(client, "commit", sidecar,
            [new HighlightImportDecision(sidecarKey, koreader.Id)], first.BatchId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = (await response.Content.ReadFromJsonAsync<HighlightImportResult>())!;

        second.BatchId.Should().Be(first.BatchId);
        var batches = await client.GetFromJsonAsync<List<HighlightImportBatchSummary>>("/api/notes/imports/batches");
        batches.Should().ContainSingle().Which.Should().Match<HighlightImportBatchSummary>(batch =>
            batch.NoteCount == 4 && batch.BookCount == 2);

        var undo = await client.DeleteAsync($"/api/notes/imports/batches/{first.BatchId}");
        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        using var report = JsonDocument.Parse(await undo.Content.ReadAsStringAsync());
        report.RootElement.GetProperty("removed").GetInt32().Should().Be(4);
        await using var db = CreateContext(factory.DatabasePath);
        (await db.Notes.CountAsync()).Should().Be(0);
        (await db.Books.CountAsync()).Should().Be(2);
        (await db.NoteImportBookLinks.CountAsync()).Should().Be(2);
        (await db.NoteCommandReceipts.CountAsync()).Should().Be(0);
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
    public async Task Reimport_corrects_notes_an_earlier_import_stored_cut_short_instead_of_duplicating_them()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Synthetic Reader Book", "Ada Reader");
        var sidecar = Path.Combine(AppContext.BaseDirectory, "Fixtures", "koreader", "metadata.multiline.lua");

        // Exactly what the line-based reader stored for this sidecar: the first
        // source line of the two-paragraph highlight, quote and backslash
        // included, and the bookmark under its screen count.
        Guid cutId, bookmarkId;
        await using (var seed = CreateContext(factory.DatabasePath))
        {
            var cut = new Nostos.Backend.Data.Models.NoteModel
            {
                BookId = book.Id,
                Content = "She said \"wait\" here.",
                SelectedText = "\"The end of one paragraph.\\",
                CaptureSource = "import",
                SourceAnchorKind = "koreader_xpointer",
                SourceAnchorValue = "/body/DocFragment[3]/body/p[4]/text().0|/body/DocFragment[3]/body/p[5]/text().21",
            };
            var bookmark = new Nostos.Backend.Data.Models.NoteModel
            {
                BookId = book.Id,
                SelectedText = "in Chapter One",
                CaptureSource = "import",
                SourceAnchorKind = "koreader_page",
                SourceAnchorValue = "14",
            };
            seed.Notes.AddRange(cut, bookmark);
            await seed.SaveChangesAsync();
            (cutId, bookmarkId) = (cut.Id, bookmark.Id);
        }

        var preview = await PreviewAsync(client, sidecar);
        preview.Books.Single().NewCount.Should().Be(1, "only the later highlight was never imported");

        var result = await CommitAsync(
            client, sidecar, new HighlightImportDecision(preview.Books.Single().SourceKey, book.Id));
        var imported = result.Books.Single();
        imported.ImportedCount.Should().Be(1);
        imported.DuplicateCount.Should().Be(2);
        imported.Message.Should().Be("2 notes from an earlier import were corrected.");

        await using (var verify = CreateContext(factory.DatabasePath))
        {
            var notes = await verify.Notes.AsNoTracking().Where(note => note.BookId == book.Id).ToListAsync();
            notes.Should().HaveCount(3);
            notes.Single(note => note.Id == cutId).SelectedText
                .Should().Be("The end of one paragraph.\nThe start of the next.");
            var bookmark = notes.Single(note => note.Id == bookmarkId);
            bookmark.SourceAnchorKind.Should().Be("koreader_xpointer");
            bookmark.SourceAnchorValue.Should().Be("/body/DocFragment[3]/body/p[12]/text().0|");
        }

        var again = await CommitAsync(
            client, sidecar, new HighlightImportDecision(preview.Books.Single().SourceKey, book.Id));
        again.Books.Single().ImportedCount.Should().Be(0);
        again.Books.Single().DuplicateCount.Should().Be(3);
        again.Books.Single().Message.Should().BeNull();
    }

    [Fact]
    public async Task An_imported_highlight_takes_the_first_reader_location_and_nothing_else_does()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Synthetic Reader Book", "Ada Reader");
        var sidecar = Path.Combine(AppContext.BaseDirectory, "Fixtures", "koreader", "metadata.lua");
        var preview = await PreviewAsync(client, sidecar);
        await CommitAsync(client, sidecar, new HighlightImportDecision(preview.Books.Single().SourceKey, book.Id));

        var notes = await client.GetFromJsonAsync<List<NoteDto>>($"/api/books/{book.Id}/notes");
        var imported = notes!.First();
        const string cfi = "epubcfi(/6/8!/4/8,/1:0,/1:33)";

        var located = await client.PutAsJsonAsync($"/api/notes/{imported.Id}/location", new NoteLocationDto(cfi));
        located.StatusCode.Should().Be(HttpStatusCode.OK);
        (await located.Content.ReadFromJsonAsync<NoteDto>())!.CfiRange.Should().Be(cfi);

        var second = await client.PutAsJsonAsync(
            $"/api/notes/{imported.Id}/location", new NoteLocationDto("epubcfi(/6/2!/4/2,/1:0,/1:5)"));
        (await second.Content.ReadFromJsonAsync<NoteDto>())!.CfiRange.Should().Be(cfi, "the first location is kept");

        (await client.PutAsJsonAsync($"/api/notes/{imported.Id}/location", new NoteLocationDto("page 12")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync($"/api/notes/{Guid.NewGuid()}/location", new NoteLocationDto(cfi)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var typed = await client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new { content = "A typed note." });
        var typedNote = (await typed.Content.ReadFromJsonAsync<NoteDto>())!;
        (await client.PutAsJsonAsync($"/api/notes/{typedNote.Id}/location", new NoteLocationDto(cfi)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_pdf_sidecar_named_only_by_its_file_is_offered_the_book_and_never_filed_unasked()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Synthetic Reader PDF", "Ada Reader");
        var sidecar = Path.Combine(AppContext.BaseDirectory, "Fixtures", "koreader", "metadata.pdf-untitled.lua");

        var preview = await PreviewAsync(client, sidecar);
        var only = preview.Books.Should().ContainSingle().Subject;
        only.AnnotationCount.Should().Be(2);
        only.Match.Should().Be("suggested", "a file name is a resemblance, not an identity");
        only.BookId.Should().BeNull();
        only.Candidates.Should().ContainSingle().Which.BookId.Should().Be(book.Id);

        var result = await CommitAsync(client, sidecar, new HighlightImportDecision(only.SourceKey, book.Id));
        result.Books.Single().ImportedCount.Should().Be(2);

        // The owner's answer is remembered by KOReader's file checksum.
        (await PreviewAsync(client, sidecar)).Books.Single().Match.Should().Be("remembered");
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

    [Fact]
    public async Task Kindle_clippings_go_through_the_same_preview_commit_and_undo()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var austen = await CreateBookAsync(client, "Pride and Prejudice", "Jane Austen", type: "ebook");
        await CreateBookAsync(client, "Walden; or, Life in the Woods", "Henry David Thoreau");
        var clippings = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kindle", "My Clippings.txt");

        var preview = await PreviewAsync(client, clippings);

        preview.Source.Should().Be("kindle");
        var exact = preview.Books.Single(book => book.Title == "Pride and Prejudice");
        exact.Match.Should().Be("exact");
        exact.BookId.Should().Be(austen.Id);
        exact.AnnotationCount.Should().Be(1);
        var walden = preview.Books.Single(book => book.Title == "Walden");
        walden.Match.Should().Be("suggested");
        walden.BookId.Should().BeNull();
        preview.Books.Single(book => book.Title == "Meditations (Penguin Classics)").Match.Should().Be("none");
        preview.Skipped.Should().HaveCount(3)
            .And.Contain(entry => entry.Reason == KindleClippingsParser.BookmarkReason && entry.Title == "Pride and Prejudice");

        var meditations = preview.Books.Single(book => book.Title == "Meditations (Penguin Classics)");
        var result = await CommitAsync(client, clippings,
            new HighlightImportDecision(exact.SourceKey, austen.Id),
            new HighlightImportDecision(meditations.SourceKey, Create: true));

        result.Source.Should().Be("kindle");
        result.Books.Single(book => book.SourceKey == exact.SourceKey).ImportedCount.Should().Be(1);
        var created = result.Books.Single(book => book.SourceKey == meditations.SourceKey);
        created.Created.Should().BeTrue();
        result.Books.Single(book => book.SourceKey == walden.SourceKey).Status.Should().Be("skipped");

        await using (var db = CreateContext(factory.DatabasePath))
        {
            var note = await db.Notes.AsNoTracking().SingleAsync(candidate => candidate.BookId == austen.Id);
            note.SelectedText.Should().StartWith("It is a truth universally acknowledged, that");
            note.Content.Should().Be("The famous opening.");
            note.SourceAnchorKind.Should().Be("kindle_location");
            note.SourceAnchorValue.Should().Be("5-7");
            // A Kindle location is not a page anyone can cite.
            note.AnchorVerified.Should().BeFalse();

            var paper = await db.Books.AsNoTracking().SingleAsync(book => book.Id == created.BookId);
            paper.Title.Should().Be("Meditations (Penguin Classics)");
            paper.Author.Should().Be("Marcus Aurelius");
            var pageNote = await db.Notes.AsNoTracking().SingleAsync(candidate => candidate.BookId == paper.Id);
            (pageNote.SourceAnchorKind, pageNote.SourceAnchorValue, pageNote.AnchorVerified).Should().Be(("kindle_page", "12", false));
        }

        // The same file again adds nothing, and the books are remembered.
        var again = await PreviewAsync(client, clippings);
        again.Books.Single(book => book.SourceKey == exact.SourceKey).NewCount.Should().Be(0);
        again.Books.Single(book => book.SourceKey == meditations.SourceKey).Match.Should().Be("remembered");
        var repeat = await CommitAsync(client, clippings, new HighlightImportDecision(exact.SourceKey, austen.Id));
        repeat.BatchId.Should().BeNull();
        repeat.Books.Single(book => book.SourceKey == exact.SourceKey).DuplicateCount.Should().Be(1);

        (await client.DeleteAsync($"/api/notes/imports/batches/{result.BatchId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var db = CreateContext(factory.DatabasePath))
        {
            (await db.Notes.CountAsync()).Should().Be(0);
            (await db.Books.CountAsync()).Should().Be(3, "undo removes notes, never books the owner chose to add");
        }
    }

    [Fact]
    public async Task A_starter_draft_offers_the_passages_an_import_just_brought_in()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var austen = await CreateBookAsync(client, "Pride and Prejudice", "Jane Austen", type: "ebook");
        var clippings = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kindle", "My Clippings.txt");
        var preview = await PreviewAsync(client, clippings);
        var exact = preview.Books.Single(book => book.Title == "Pride and Prejudice");
        var meditations = preview.Books.Single(book => book.Title == "Meditations (Penguin Classics)");

        // Nothing imported yet: nothing to start from.
        (await StarterAsync(client, null)).Should().BeEmpty();

        var result = await CommitAsync(client, clippings,
            new HighlightImportDecision(exact.SourceKey, austen.Id),
            new HighlightImportDecision(meditations.SourceKey, Create: true));

        var picked = await StarterAsync(client, result.BatchId!.Value.ToString());

        // One passage per book, the one the reader wrote a note on first.
        picked.Select(hit => hit.BookTitle).Should().Equal("Pride and Prejudice", "Meditations (Penguin Classics)");
        picked[0].SelectedText.Should().StartWith("It is a truth universally acknowledged, that");
        picked[0].Content.Should().Be("The famous opening.");
        picked[0].BookAuthor.Should().Be("Jane Austen");

        // Without a batch, the library's imported highlights answer the same.
        (await StarterAsync(client, null)).Select(hit => hit.Id).Should().Equal(picked.Select(hit => hit.Id));
        // An import that no longer exists offers nothing.
        (await StarterAsync(client, Guid.NewGuid().ToString())).Should().BeEmpty();

        (await client.DeleteAsync($"/api/notes/imports/batches/{result.BatchId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StarterAsync(client, null)).Should().BeEmpty();
    }

    private static async Task<List<NoteSearchHitDto>> StarterAsync(HttpClient client, string? batchIds)
    {
        var url = batchIds is null ? "/api/notes/imports/starter" : $"/api/notes/imports/starter?batchIds={batchIds}";
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<NoteSearchHitDto>>())!;
    }

    [Fact]
    public async Task A_readwise_export_imports_books_and_reports_what_it_left_out()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();
        var austen = await CreateBookAsync(client, "Pride and Prejudice", "Jane Austen");
        var export = Path.Combine(AppContext.BaseDirectory, "Fixtures", "readwise", "readwise-export.csv");

        var preview = await PreviewAsync(client, export);

        preview.Source.Should().Be("readwise");
        preview.Books.Should().HaveCount(3);
        var book = preview.Books.Single(candidate => candidate.SourceKey == "readwise:asin:B00SYNTH01");
        book.Match.Should().Be("exact");
        book.NewCount.Should().Be(3);
        preview.Books.Single(candidate => candidate.Title == "How to Read Slowly").Notice
            .Should().Be(ReadwiseCsvParser.ArticleNotice);
        preview.Skipped!.Select(entry => entry.Reason).Should().BeEquivalentTo(
            ReadwiseCsvParser.TweetReason, ReadwiseCsvParser.PodcastReason, ReadwiseCsvParser.EmptyReason);

        var result = await CommitAsync(client, export, new HighlightImportDecision(book.SourceKey, austen.Id));
        result.Books.Single(candidate => candidate.SourceKey == book.SourceKey).ImportedCount.Should().Be(3);

        var again = await CommitAsync(client, export, new HighlightImportDecision(book.SourceKey, austen.Id));
        again.Books.Single(candidate => candidate.SourceKey == book.SourceKey).DuplicateCount.Should().Be(3);

        await using var db = CreateContext(factory.DatabasePath);
        var notes = await db.Notes.AsNoTracking().Where(note => note.BookId == austen.Id).ToListAsync();
        notes.Should().HaveCount(3);
        notes.Should().AllSatisfy(note =>
        {
            note.SourceAnchorKind.Should().Be("readwise_location");
            note.AnchorVerified.Should().BeFalse();
        });
        notes.Should().Contain(note => note.SelectedText == null && note.Content == "A note with no highlight.");
        (await db.NoteImportBatches.SingleAsync()).Source.Should().Be("readwise");
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
        HighlightImportDecision[]? decisions = null,
        Guid? batchId = null)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
        if (decisions is not null)
            body.Add(new StringContent(JsonSerializer.Serialize(decisions, JsonSerializerOptions.Web)), "decisions");
        if (batchId is not null)
            body.Add(new StringContent(batchId.Value.ToString()), "batchId");
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

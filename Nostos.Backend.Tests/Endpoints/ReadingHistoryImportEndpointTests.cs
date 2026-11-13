using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class ReadingHistoryImportEndpointTests : IClassFixture<LibraryEndpointFactory>, IAsyncLifetime
{
    private readonly LibraryEndpointFactory _factory;
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nostos-reading-history-test-").FullName;

    public ReadingHistoryImportEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetStateAsync();

    public Task DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    private static string Goodreads => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "goodreads", "goodreads_library_export.csv");

    private static string Storygraph => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "storygraph", "storygraph_export.csv");

    [Fact]
    public async Task Preview_matches_by_isbn13_then_isbn10_then_title_and_author_and_writes_nothing()
    {
        using var client = _factory.CreateClient();
        var cartographer = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");
        var kitchen = await CreateBookAsync(client, "Small Gods of the Kitchen", "Bea Lindqvist", "0000000051");
        var orchard = await CreateBookAsync(client, "The Glass Orchard", "Elias Thorn");

        var preview = await PreviewAsync(client, Goodreads);

        preview.Source.Should().Be("goodreads");
        preview.Books.Should().HaveCount(12);
        preview.Skipped.Should().ContainSingle().Which.Count.Should().Be(1);

        var byIsbn13 = preview.Books.Single(book => book.SourceKey == "goodreads:id:101");
        byIsbn13.Match.Should().Be("exact");
        byIsbn13.BookId.Should().Be(cartographer.Id);
        byIsbn13.Values.Rating.Should().Be(5);
        byIsbn13.Values.Review.Should().Contain("drawn from memory");
        byIsbn13.Values.PrivateNotes.Should().Be("Kept for the map on page 12.");
        byIsbn13.Values.FinishedAt.Should().Be(new DateTime(2021, 3, 14, 0, 0, 0, DateTimeKind.Utc));
        byIsbn13.Values.Shelves.Should().Equal("fantasy", "favourites");
        byIsbn13.HasChanges.Should().BeTrue();

        // ISBN13 is empty; the ISBN-10 column still identifies the book.
        var byIsbn10 = preview.Books.Single(book => book.SourceKey == "goodreads:id:108");
        byIsbn10.Match.Should().Be("exact");
        byIsbn10.BookId.Should().Be(kitchen.Id);

        var byName = preview.Books.Single(book => book.SourceKey == "goodreads:id:105");
        byName.Match.Should().Be("exact");
        byName.BookId.Should().Be(orchard.Id);

        var toRead = preview.Books.Single(book => book.SourceKey == "goodreads:id:103");
        toRead.Match.Should().Be("none");
        toRead.Values.ToRead.Should().BeTrue();

        var ebook = preview.Books.Single(book => book.SourceKey == "goodreads:id:107");
        ebook.Match.Should().Be("none", "a title with no ISBN that is not in the library has no match");

        await using var db = CreateContext(_factory.DatabasePath);
        (await db.Books.CountAsync()).Should().Be(3);
        (await db.BookImportBatches.CountAsync()).Should().Be(0);
        (await db.ReadingListEntries.CountAsync()).Should().Be(0);
        (await db.Collections.CountAsync()).Should().Be(0);
        (await db.Notes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Commit_sets_work_values_finish_date_shelves_and_private_note()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");

        var result = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id, MapShelves: true));

        result.BatchId.Should().NotBeNull();
        var row = result.Books.Single(entry => entry.SourceKey == "goodreads:id:101");
        row.Status.Should().Be("imported");
        row.BookId.Should().Be(book.Id);
        row.ChangeCount.Should().BeGreaterThanOrEqualTo(5, "rating, review, finish date, two shelves and a note");

        await using var db = CreateContext(_factory.DatabasePath);
        var saved = await db.Books
            .Include(candidate => candidate.Work)
            .Include(candidate => candidate.BookCollections)
            .SingleAsync(candidate => candidate.Id == book.Id);
        saved.Work!.Rating.Should().Be(5);
        saved.Work.PersonalReview.Should().Contain("drawn from memory");
        saved.Progress.FinishedAt.Should().Be(new DateTime(2021, 3, 14, 0, 0, 0, DateTimeKind.Utc));
        // The value belongs to the book, not this copy.
        saved.Progress.Rating.Should().Be(0);

        var collections = await db.Collections.AsNoTracking().OrderBy(collection => collection.Name).ToListAsync();
        collections.Select(collection => collection.Name).Should().Equal("fantasy", "favourites");
        saved.BookCollections.Select(membership => membership.CollectionId)
            .Should().BeEquivalentTo(collections.Select(collection => collection.Id));

        var note = await db.Notes.AsNoTracking().SingleAsync();
        (note.BookId, note.Content, note.CaptureSource, note.SourceAnchorKind)
            .Should().Be((book.Id, "Kept for the map on page 12.", "import", "reading_history_note"));
    }

    [Fact]
    public async Task An_older_export_never_overwrites_a_newer_rating_and_undo_restores_both()
    {
        using var client = _factory.CreateClient();
        // The file says 4 stars, read 2022-11-02; the library holds a newer 5.
        var book = await CreateBookAsync(
            client, "A Winter in the Salt Marshes", "Nils Berg",
            rating: 5, finishedAt: "2025-01-01T00:00:00Z");

        var result = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:102", book.Id));

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            var saved = await db.Books.Include(candidate => candidate.Work).SingleAsync(candidate => candidate.Id == book.Id);
            saved.Work!.Rating.Should().Be(5, "a newer value is never displaced by an older export");
            saved.Work.PersonalReview.Should().Be("Bleak and beautiful.");
            // The file's older rating is still kept, as this edition's own value.
            saved.Progress.Rating.Should().Be(4);
            saved.Progress.FinishedAt.Should().Be(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        (await client.DeleteAsync($"/api/books/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            var saved = await db.Books.Include(candidate => candidate.Work).SingleAsync(candidate => candidate.Id == book.Id);
            saved.Work!.Rating.Should().Be(5);
            saved.Work.PersonalReview.Should().BeNull();
            saved.Progress.Rating.Should().Be(0);
            saved.Progress.FinishedAt.Should().Be(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            (await db.BookImportBatches.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task A_newer_export_moves_the_library_rating_to_the_edition()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(
            client, "A Winter in the Salt Marshes", "Nils Berg",
            rating: 3, finishedAt: "2019-01-01T00:00:00Z");

        await CommitAsync(client, Goodreads, new ReadingHistoryImportDecision("goodreads:id:102", book.Id));

        await using var db = CreateContext(_factory.DatabasePath);
        var saved = await db.Books.Include(candidate => candidate.Work).SingleAsync(candidate => candidate.Id == book.Id);
        saved.Work!.Rating.Should().Be(4, "the file was read after the library's last read");
        saved.Progress.Rating.Should().Be(3, "the value the work used to hold is kept on the edition");
        saved.Progress.FinishedAt.Should().Be(new DateTime(2022, 11, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Reimporting_the_same_file_adds_nothing_and_can_be_undone_and_imported_again()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");

        var first = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id, MapShelves: true));

        var again = await PreviewAsync(client, Goodreads);
        var previewed = again.Books.Single(entry => entry.SourceKey == "goodreads:id:101");
        previewed.Match.Should().Be("exact");
        previewed.HasChanges.Should().BeFalse("every value the row holds is already in Nostos");

        var repeat = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id, MapShelves: true));
        repeat.BatchId.Should().BeNull("an import that changes nothing has nothing to undo");
        repeat.Books.Single(entry => entry.SourceKey == "goodreads:id:101").Status.Should().Be("unchanged");

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            (await db.BookImportBatches.CountAsync()).Should().Be(1);
            (await db.Notes.CountAsync()).Should().Be(1);
            (await db.Collections.CountAsync()).Should().Be(2);
            (await db.BookCollections.CountAsync()).Should().Be(2);
        }

        (await client.DeleteAsync($"/api/books/imports/batches/{first.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            (await db.BookImportBatches.CountAsync()).Should().Be(0);
            (await db.Notes.CountAsync()).Should().Be(0);
            (await db.Collections.CountAsync()).Should().Be(0, "the import created both collections");
        }

        // Undo removed the note receipt too, so the same file can bring it back.
        var restored = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id, MapShelves: true));
        restored.BatchId.Should().NotBeNull();
        restored.Books.Single(entry => entry.SourceKey == "goodreads:id:101").Status.Should().Be("imported");
    }

    [Fact]
    public async Task A_later_edit_is_not_lost_by_undo()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client, "A Winter in the Salt Marshes", "Nils Berg");

        var result = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:102", book.Id));
        (await client.PutAsJsonAsync($"/api/books/{book.Id}", new { rating = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.DeleteAsync($"/api/books/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateContext(_factory.DatabasePath);
        var saved = await db.Books.Include(candidate => candidate.Work).SingleAsync(candidate => candidate.Id == book.Id);
        saved.Work!.Rating.Should().Be(5, "undo only takes back what the import itself wrote");
    }

    [Fact]
    public async Task To_read_goes_to_the_reading_list_and_creates_no_book()
    {
        using var client = _factory.CreateClient();

        var result = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:103", Create: true));

        var row = result.Books.Single(entry => entry.SourceKey == "goodreads:id:103");
        row.Status.Should().Be("imported");
        row.BookId.Should().BeNull();
        row.Created.Should().BeFalse();

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            (await db.Books.CountAsync()).Should().Be(0);
            var entry = await db.ReadingListEntries.AsNoTracking().SingleAsync();
            (entry.Title, entry.Author).Should().Be(("The Lantern Keepers", "Mira Holt"));
            entry.Isbn.Should().Be("9780000000033");
        }

        var repeat = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:103", Create: true));
        repeat.BatchId.Should().BeNull();
        repeat.Books.Single(entry => entry.SourceKey == "goodreads:id:103").Message.Should().Be("Already on your reading list.");

        // Undo takes the entry back as well.
        (await client.DeleteAsync($"/api/books/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var db = CreateContext(_factory.DatabasePath))
            (await db.ReadingListEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Shelves_become_collections_only_when_the_owner_maps_them()
    {
        using var client = _factory.CreateClient();
        var cartographer = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");
        var silence = await CreateBookAsync(client, "Nine Kinds of Silence", "Ruth Okonkwo", "9780000000064");

        await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", cartographer.Id),
            new ReadingHistoryImportDecision("goodreads:id:106", silence.Id, MapShelves: true));

        await using var db = CreateContext(_factory.DatabasePath);
        var collections = await db.Collections.AsNoTracking().OrderBy(collection => collection.Name).ToListAsync();
        collections.Select(collection => collection.Name).Should().Equal("book club", "literary");
        var mapped = await db.Books.Include(candidate => candidate.BookCollections)
            .SingleAsync(candidate => candidate.Id == silence.Id);
        mapped.BookCollections.Should().HaveCount(2);
        var unmapped = await db.Books.Include(candidate => candidate.BookCollections)
            .SingleAsync(candidate => candidate.Id == cartographer.Id);
        unmapped.BookCollections.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_unmatched_book_can_be_added_and_undo_takes_it_back()
    {
        using var client = _factory.CreateClient();

        var result = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:110", Create: true));

        var row = result.Books.Single(entry => entry.SourceKey == "goodreads:id:110");
        row.Created.Should().BeTrue();
        row.BookId.Should().NotBeNull();

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            var saved = await db.Books.Include(candidate => candidate.Work).SingleAsync();
            saved.Title.Should().Be("Dune, or: \"The Spice\" Must Flow");
            saved.Work!.Rating.Should().Be(4);
            saved.Work.PersonalReview.Should().Contain("controls the spice");
        }

        (await client.DeleteAsync($"/api/books/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = CreateContext(_factory.DatabasePath))
            (await db.Books.CountAsync()).Should().Be(0, "the book was added by this import");
    }

    [Fact]
    public async Task A_storygraph_export_rounds_quarter_stars_and_uses_dates_read()
    {
        using var client = _factory.CreateClient();
        var kitchen = await CreateBookAsync(client, "Small Gods of the Kitchen", "Bea Lindqvist", "0000000051");
        var estuary = await CreateBookAsync(client, "The Long Estuary", "Sam Achterberg");
        var glass = await CreateBookAsync(client, "The Glass Orchard", "Elias Thorn");

        var preview = await PreviewAsync(client, Storygraph);
        preview.Source.Should().Be("storygraph");
        preview.Books.Should().HaveCount(12);
        var quarter = preview.Books.Single(book => book.SourceKey.StartsWith("storygraph:isbn:9780000000019"));
        quarter.Values.Rating.Should().Be(5);
        quarter.Values.RatingNotice.Should().Be("4.75 stars is stored as 5.");

        await CommitAsync(client, Storygraph,
            new ReadingHistoryImportDecision("storygraph:isbn:0000000051", kitchen.Id),
            new ReadingHistoryImportDecision("storygraph:meta:THE LONG ESTUARY|SAM ACHTERBERG", estuary.Id),
            new ReadingHistoryImportDecision("storygraph:isbn:9780000000057", glass.Id));

        await using var db = CreateContext(_factory.DatabasePath);
        var kitchenBook = await db.Books.Include(candidate => candidate.Work).SingleAsync(candidate => candidate.Id == kitchen.Id);
        kitchenBook.Work!.Rating.Should().Be(4, "4.25 rounds to 4");
        kitchenBook.Progress.FinishedAt.Should().Be(new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var estuaryBook = await db.Books.Include(candidate => candidate.Work).SingleAsync(candidate => candidate.Id == estuary.Id);
        estuaryBook.Work!.PersonalReview.Should().Be("Last Date Read is empty, so Dates Read answers.");
        estuaryBook.Progress.FinishedAt.Should().Be(new DateTime(2020, 2, 10, 0, 0, 0, DateTimeKind.Utc));

        var glassBook = await db.Books.Include(candidate => candidate.Work).SingleAsync(candidate => candidate.Id == glass.Id);
        glassBook.Work!.Rating.Should().Be(2, "2.25 rounds to 2");
        glassBook.Work.PersonalReview.Should().Be("Stopped halfway.");
        glassBook.Progress.FinishedAt.Should().BeNull("a did-not-finish row has no finish date");
    }

    [Fact]
    public async Task A_file_that_is_neither_source_is_rejected_and_batches_list_what_was_imported()
    {
        using var client = _factory.CreateClient();

        var other = Path.Combine(_directory, "library.csv");
        await File.WriteAllTextAsync(other, "Title,Author\nA Book,Someone\n");
        (await UploadAsync(client, "preview", other)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await UploadAsync(client, "commit", other)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var book = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");
        await CommitAsync(client, Goodreads, new ReadingHistoryImportDecision("goodreads:id:101", book.Id));

        var batches = await client.GetFromJsonAsync<List<ReadingHistoryImportBatchSummary>>("/api/books/imports/batches");
        var batch = batches.Should().ContainSingle().Subject;
        batch.Source.Should().Be("goodreads");
        batch.BookCount.Should().Be(1);
        batch.ChangeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task An_upload_name_this_server_cannot_store_is_replaced_by_a_safe_one()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");

        var result = await CommitAsync(
            client,
            Goodreads,
            ".goodreads_library_export.csv",
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id));

        result.BatchId.Should().NotBeNull();
        await using var db = CreateContext(_factory.DatabasePath);
        var batch = await db.BookImportBatches.AsNoTracking().SingleAsync();
        batch.Source.Should().Be("goodreads");
        batch.FileName.Should().Be(
            "export.csv",
            "a dot-prefixed upload name is neither written nor recorded");
    }

    [Fact]
    public async Task An_import_without_a_multipart_body_or_a_file_is_refused()
    {
        using var client = _factory.CreateClient();

        var json = await client.PostAsJsonAsync("/api/books/imports/preview", new { });
        json.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await json.Content.ReadAsStringAsync())
            .Should().Contain("Expected multipart/form-data with a Goodreads or StoryGraph CSV.");

        using var empty = new MultipartFormDataContent();
        empty.Add(new StringContent("[]"), "decisions");
        var missing = await client.PostAsync("/api/books/imports/preview", empty);
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync())
            .Should().Contain("Choose a Goodreads or StoryGraph export to import.");
    }

    private static async Task<ReadingHistoryImportPreview> PreviewAsync(HttpClient client, string path)
    {
        var response = await UploadAsync(client, "preview", path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ReadingHistoryImportPreview>())!;
    }

    private static async Task<ReadingHistoryImportResult> CommitAsync(
        HttpClient client,
        string path,
        params ReadingHistoryImportDecision[] decisions)
    {
        var response = await UploadAsync(client, "commit", path, decisions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ReadingHistoryImportResult>())!;
    }

    /// <summary>Commits with a chosen upload name, for the cases the name itself is suspect.</summary>
    private static async Task<ReadingHistoryImportResult> CommitAsync(
        HttpClient client,
        string path,
        string fileName,
        params ReadingHistoryImportDecision[] decisions)
    {
        var response = await UploadAsync(client, "commit", path, decisions, fileName);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ReadingHistoryImportResult>())!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string step,
        string path,
        ReadingHistoryImportDecision[]? decisions = null,
        string? fileName = null)
    {
        using var body = new MultipartFormDataContent();
        body.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(path)),
            "file",
            fileName ?? Path.GetFileName(path));
        if (decisions is not null)
            body.Add(new StringContent(JsonSerializer.Serialize(decisions, JsonSerializerOptions.Web)), "decisions");
        return await client.PostAsync($"/api/books/imports/{step}", body);
    }

    private static async Task<BookDto> CreateBookAsync(
        HttpClient client,
        string title,
        string? author = null,
        string? isbn = null,
        int? rating = null,
        string? finishedAt = null,
        string type = "physical")
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["title"] = title,
            ["author"] = author,
            ["isbn"] = isbn,
        };
        if (rating is not null)
            body["rating"] = rating;
        if (finishedAt is not null)
            body["finishedAt"] = finishedAt;

        var response = await client.PostAsJsonAsync("/api/books", body);
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

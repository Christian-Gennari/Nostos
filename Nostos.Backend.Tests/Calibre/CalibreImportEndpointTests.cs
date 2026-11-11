using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Calibre;

/// <summary>
/// The Calibre import through the SelfHosted host's real routes, against the
/// committed fixture library: a genuine Calibre schema and public-domain books
/// (see Fixtures/calibre/README.md). The fixture is never written to; every
/// test reads it and the imported files go into the test host's own storage.
/// </summary>
public sealed class CalibreImportEndpointTests
{
    private static string FixtureLibrary =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "calibre", "library");

    [Fact]
    public async Task A_self_hosted_server_offers_the_import_and_reads_the_library_without_writing_it()
    {
        var before = FixtureSnapshot.Take(FixtureLibrary);
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();

        var capabilities = await client.GetFromJsonAsync<DeploymentCapabilitiesResponse>(
            DeploymentCapabilitiesEndpoints.Route);
        capabilities!.SupportsCalibreImport.Should().BeTrue();

        var status = await client.GetFromJsonAsync<CalibreStatusResponse>(CalibreEndpoints.Route);
        status!.Configured.Should().BeTrue();
        status.Available.Should().BeTrue();

        var preview = await PreviewAsync(client);

        preview.Source.Should().Be("calibre");
        preview.Books.Should().HaveCount(6);
        preview.Summary.Skipped.Should().Be(2);

        var pride = Book(preview, 1);
        pride.Title.Should().Be("Pride and Prejudice");
        pride.Author.Should().Be("Jane Austen");
        pride.Isbn.Should().Be("9780141439518");
        pride.Publisher.Should().Be("T. Egerton");
        pride.PublishedDate.Should().Be("1813-01-28");
        pride.Rating.Should().Be(5, "Calibre's 10 is Nostos's 5 stars");
        pride.Tags.Should().Equal("classics", "romance");
        pride.Description.Should().Be(
            "Elizabeth Bennet and Mr Darcy, in the drawing room.\n\nSecond paragraph.",
            "Calibre's comments are HTML; Nostos stores words");
        pride.HasCover.Should().BeTrue();
        pride.ReadAt.Should().Be(new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        pride.Formats.Select(format => format.Format).Should().Equal("EPUB", "PDF");
        pride.Formats.Single(format => format.Format == "EPUB").SelectedByDefault.Should().BeTrue();
        pride.Formats.Single(format => format.Format == "PDF").SelectedByDefault.Should().BeFalse(
            "only the best format is offered by default");

        var alice = Book(preview, 2);
        alice.Series.Should().Be("Children's Classics");
        alice.VolumeNumber.Should().Be("1");
        alice.UnsupportedFormats.Should().ContainSingle().Which.Format.Should().Be("MOBI");
        alice.ReadAt.Should().BeNull();
        alice.ReadNotice.Should().Contain("no read date");

        var gilded = Book(preview, 4);
        gilded.Author.Should().Be("Mark Twain & Charles Dudley Warner");
        gilded.VolumeNumber.Should().Be("2.5");
        var audio = gilded.Formats.Single(format => format.Format == "MP3");
        audio.Kind.Should().Be("audiobook");

        var timeMachine = Book(preview, 3);
        timeMachine.HasCover.Should().BeFalse();
        timeMachine.Formats.Should().ContainSingle();

        var blue = Book(preview, 7);
        blue.Isbn.Should().BeNull();
        blue.Match.Should().Be("none");

        preview.Skipped.Should().Contain(entry => entry.Title == "The Art of War");
        preview.Skipped.Should().Contain(entry => entry.Title == "The Yellow Wallpaper"
            && entry.Reason.Contains("no longer in the Calibre library"));

        preview.CustomColumns.Should().Contain(column =>
            column.Label == "read" && column.Status == "used");
        preview.CustomColumns.Should().Contain(column =>
            column.Label == "date_read" && column.Status == "used");
        preview.CustomColumns.Should().Contain(column =>
            column.Label == "genre" && column.Status == "skipped");
        preview.CustomColumns.Should().Contain(column =>
            column.Label == "pages" && column.Status == "skipped");

        await using var db = CreateContext(factory.DatabasePath);
        (await db.Books.CountAsync()).Should().Be(0);
        (await db.BookImportBatches.CountAsync()).Should().Be(0);
        (await db.Collections.CountAsync()).Should().Be(0);
        before.VerifyUnchanged();
    }

    [Fact]
    public async Task Preview_matches_an_existing_book_by_isbn_first_then_title_and_author()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var pride = await CreateBookAsync(client, "Pride and Prejudice", "Jane Austen", "9780141439518");
        var timeMachine = await CreateBookAsync(client, "The Time Machine", "H. G. Wells");

        var preview = await PreviewAsync(client);

        Book(preview, 1).Match.Should().Be("exact");
        Book(preview, 1).BookId.Should().Be(pride.Id);
        Book(preview, 3).Match.Should().Be("exact");
        Book(preview, 3).BookId.Should().Be(timeMachine.Id);
    }

    [Fact]
    public async Task Import_brings_the_chosen_formats_their_metadata_and_tags_as_collections()
    {
        var before = FixtureSnapshot.Take(FixtureLibrary);
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var preview = await PreviewAsync(client);

        var result = await ImportAsync(client,
            Decision(preview, 1, "EPUB", collections: true),
            Decision(preview, 3, "EPUB", collections: true),
            Decision(preview, 4, "MP3", collections: true));

        result.BatchId.Should().NotBeNull();
        result.Books.Should().OnlyContain(book => book.Status == "imported");
        result.Books.Single(book => book.CalibreId == 4).Created.Should().BeTrue();

        await using var db = CreateContext(factory.DatabasePath);
        var books = await db.Books
            .Include(book => book.Work)
            .Include(book => book.BookCollections)
            .OrderBy(book => book.Title)
            .ToListAsync();
        books.Should().HaveCount(3);

        var pride = books.Single(book => book.Title == "Pride and Prejudice");
        pride.Should().BeOfType<EBookModel>();
        pride.Author.Should().Be("Jane Austen");
        pride.Metadata.Categories.Should().Be("classics, romance");
        pride.Work!.Rating.Should().Be(5);
        pride.Progress.FinishedAt.Should().Be(new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        pride.FileDetails.HasFile.Should().BeTrue();
        pride.FileDetails.FileName.Should().Be("book.epub");
        pride.FileDetails.CoverFileName.Should().NotBeNullOrEmpty("Calibre had cover.jpg");
        pride.Status.Should().Be(Nostos.Shared.Enums.BookStatus.Ready);
        Directory.Exists(Path.Combine(factory.BooksRootPath, pride.Id.ToString())).Should().BeTrue();

        var gilded = books.Single(book => book.Title == "The Gilded Age");
        gilded.Should().BeOfType<AudioBookModel>();
        gilded.Author.Should().Be("Mark Twain & Charles Dudley Warner");
        gilded.FileDetails.FileName.Should().Be("book.mp3");
        ((AudioBookModel)gilded).Duration.Should().NotBeNullOrEmpty("the MP3's length was read");
        gilded.Metadata.Series.Should().Be("The Gilded Age");
        gilded.Metadata.VolumeNumber.Should().Be("2.5");

        var collections = await db.Collections.AsNoTracking().OrderBy(collection => collection.Name).ToListAsync();
        collections.Select(collection => collection.Name).Should().Equal(
            "classics", "romance", "satire", "science fiction");

        (await db.BookImportBatches.CountAsync()).Should().Be(1);
        (await db.BookImportBatchBooks.CountAsync()).Should().Be(3);
        before.VerifyUnchanged();
    }

    [Fact]
    public async Task Tags_stay_metadata_when_the_owner_does_not_ask_for_collections()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var preview = await PreviewAsync(client);

        await ImportAsync(client, Decision(preview, 2, "EPUB", collections: false));

        await using var db = CreateContext(factory.DatabasePath);
        var alice = await db.Books.Include(book => book.BookCollections).SingleAsync();
        alice.Metadata.Categories.Should().Be("classics, fantasy");
        alice.BookCollections.Should().BeEmpty();
        (await db.Collections.CountAsync()).Should().Be(0, "nothing is created silently");
    }

    [Fact]
    public async Task A_matched_metadata_only_book_receives_the_file_and_undo_removes_only_the_file()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Pride and Prejudice", "Jane Austen", "9780141439518");
        var before = FixtureSnapshot.Take(FixtureLibrary);
        var preview = await PreviewAsync(client);

        Book(preview, 1).Match.Should().Be("exact");
        var result = await ImportAsync(client, Decision(preview, 1, "EPUB", collections: false));

        result.Books.Single().BookId.Should().Be(book.Id);
        result.Books.Single().Created.Should().BeFalse();

        await using (var db = CreateContext(factory.DatabasePath))
        {
            var saved = await db.Books.Include(candidate => candidate.Work).SingleAsync();
            saved.Id.Should().Be(book.Id);
            saved.FileDetails.HasFile.Should().BeTrue();
            saved.FileDetails.FileName.Should().Be("book.epub");
            saved.Work!.Rating.Should().Be(0, "an existing library value is not displaced");
        }

        var undo = await UndoAsync(client, result.BatchId!.Value);
        undo.Removed.Should().Be(1);
        undo.Kept.Should().Be(0);

        await using (var db = CreateContext(factory.DatabasePath))
        {
            var saved = await db.Books.SingleAsync();
            saved.Id.Should().Be(book.Id);
            saved.FileDetails.HasFile.Should().BeFalse();
            saved.FileDetails.FileName.Should().BeNull();
            saved.Metadata.Categories.Should().BeNull();
            File.Exists(Path.Combine(factory.BooksRootPath, book.Id.ToString(), "book.epub"))
                .Should().BeFalse("the file the import wrote is gone");
        }

        before.VerifyUnchanged();
    }

    [Fact]
    public async Task Importing_the_same_library_again_changes_nothing()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var preview = await PreviewAsync(client);

        var first = await ImportAsync(client, Decision(preview, 3, "EPUB", collections: false));
        var second = await ImportAsync(client, Decision(preview, 3, "EPUB", collections: false));

        second.BatchId.Should().BeNull("an import that writes nothing has nothing to undo");
        second.Books.Single().Status.Should().Be("unchanged");
        await using var db = CreateContext(factory.DatabasePath);
        (await db.Books.CountAsync()).Should().Be(1);
        (await db.BookImportBatches.CountAsync()).Should().Be(1);

        // The same preview now reports the format as already here.
        var again = await PreviewAsync(client);
        Book(again, 3).Formats.Single().AlreadyHere.Should().BeTrue();
    }

    [Fact]
    public async Task Both_ebook_formats_become_editions_of_one_work()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var preview = await PreviewAsync(client);
        var pride = Book(preview, 1);

        var result = await ImportAsync(client, new CalibreImportDecision(
            pride.SourceKey,
            pride.Formats.Select(format => format.Key).ToList(),
            CreateCollections: false));

        result.Books.Single().FileCount.Should().Be(2);

        await using var db = CreateContext(factory.DatabasePath);
        var books = await db.Books.Include(book => book.Work).ToListAsync();
        books.Should().HaveCount(2);
        books.Select(book => book.FileDetails.FileName).Should().BeEquivalentTo("book.epub", "book.pdf");
        books.Select(book => book.WorkId).Distinct().Should().ContainSingle("they are editions of one work");
        books.Count(book => book.NormalizedIsbn is not null).Should().Be(1, "an ISBN identifies one edition");
    }

    [Fact]
    public async Task Undo_keeps_a_book_the_owner_has_started_reading()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var preview = await PreviewAsync(client);
        var result = await ImportAsync(client, Decision(preview, 3, "EPUB", collections: false));
        var bookId = result.Books.Single().BookId!.Value;

        (await client.PutAsJsonAsync($"/api/books/{bookId}/progress",
            new { location = "chapter-2", percentage = 12 })).StatusCode.Should().Be(HttpStatusCode.OK);

        var undo = await UndoAsync(client, result.BatchId!.Value);
        undo.Removed.Should().Be(1);
        undo.Kept.Should().Be(1);

        await using var db = CreateContext(factory.DatabasePath);
        var saved = await db.Books.SingleAsync();
        saved.Id.Should().Be(bookId);
        saved.Progress.ProgressPercent.Should().Be(12);
        saved.FileDetails.HasFile.Should().BeTrue("the reader is using the file");
    }

    [Fact]
    public async Task Unreadable_and_unknown_choices_are_reported_not_silently_dropped()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/calibre/import", new
        {
            decisions = new[]
            {
                new { sourceKey = "calibre:6", formats = new[] { "calibre:6:EPUB" } },
                new { sourceKey = "calibre:999", formats = new[] { "calibre:999:EPUB" } },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var import = await WaitForImportAsync(client);

        import.Status.Should().Be("done");
        import.Results.Should().Contain(result =>
            result.SourceKey == "calibre:6" && result.Status == "skipped" && result.Message!.Contains("no longer"));
        import.Results.Should().Contain(result =>
            result.SourceKey == "calibre:999" && result.Status == "skipped");
    }

    [Fact]
    public async Task A_server_without_a_library_says_so_and_refuses_to_scan()
    {
        using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();

        var capabilities = await client.GetFromJsonAsync<DeploymentCapabilitiesResponse>(
            DeploymentCapabilitiesEndpoints.Route);
        capabilities!.SupportsCalibreImport.Should().BeTrue(
            "the host can offer the feature even before a folder is set up");

        var status = await client.GetFromJsonAsync<CalibreStatusResponse>(CalibreEndpoints.Route);
        status!.Configured.Should().BeFalse();
        status.Available.Should().BeFalse();

        var preview = await client.GetAsync("/api/calibre/preview");
        preview.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await preview.Content.ReadAsStringAsync()).Should().Contain("calibre_not_configured");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static CalibrePreviewBook Book(CalibreImportPreview preview, int calibreId) =>
        preview.Books.Single(book => book.CalibreId == calibreId);

    private static CalibreImportDecision Decision(
        CalibreImportPreview preview, int calibreId, string format, bool collections) =>
        new(
            Book(preview, calibreId).SourceKey,
            [Book(preview, calibreId).Formats.Single(choice => choice.Format == format).Key],
            CreateCollections: collections);

    private static async Task<CalibreImportPreview> PreviewAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/calibre/preview");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CalibreImportPreview>())!;
    }

    private static async Task<CalibreImportResult> ImportAsync(
        HttpClient client,
        params CalibreImportDecision[] decisions)
    {
        var response = await client.PostAsJsonAsync("/api/calibre/import", new { decisions });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var state = await WaitForImportAsync(client);
        state.Status.Should().Be("done");
        state.Results.Should().NotBeEmpty();

        return new CalibreImportResult(
            state.BatchId,
            "calibre",
            state.Results);
    }

    private static async Task<CalibreImportJobState> WaitForImportAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var state = await client.GetFromJsonAsync<CalibreImportJobState>("/api/calibre/import");
            if (state!.Status is not ("importing" or "idle"))
                return state;
            await Task.Delay(25);
        }

        throw new TimeoutException("The Calibre import did not finish.");
    }

    private static async Task<CalibreUndoResult> UndoAsync(HttpClient client, Guid batchId)
    {
        var response = await client.DeleteAsync($"/api/calibre/batches/{batchId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<CalibreUndoResult>())!;
    }

    private static async Task<BookDto> CreateBookAsync(
        HttpClient client,
        string title,
        string? author = null,
        string? isbn = null,
        string type = "ebook")
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type,
            title,
            author,
            isbn,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private static NostosDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new NostosDbContext(options);
    }

    private sealed class Factory(string libraryRoot) : LibraryEndpointFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Calibre:LibraryRoot", libraryRoot);
        }
    }

    /// <summary>A byte-for-byte and name-for-name check that the library was not touched.</summary>
    private sealed class FixtureSnapshot
    {
        private readonly string _root;
        private readonly Dictionary<string, string> _files;

        private FixtureSnapshot(string root, Dictionary<string, string> files)
        {
            _root = root;
            _files = files;
        }

        public static FixtureSnapshot Take(string root) =>
            new(root, Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    StringComparer.Ordinal));

        public void VerifyUnchanged() => Take(_root)._files.Should().Equal(_files);
    }
}

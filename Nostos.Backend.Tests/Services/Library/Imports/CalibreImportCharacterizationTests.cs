using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library.Imports;

/// <summary>
/// What a Calibre commit and its undo do to the library (issue #1625), pinned
/// before the import writes were routed through the library service: which books
/// are created and matched, which memberships appear, that the library version
/// advances for the writes (so open screens refresh) and not for a commit that
/// writes nothing, and that undo puts every one of them back.
/// </summary>
public sealed class CalibreImportCharacterizationTests
{
    private static string FixtureLibrary =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "calibre", "library");

    [Fact]
    public async Task Commit_creates_books_tags_as_collections_and_memberships_and_undo_takes_them_back()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var before = await LibraryVersion.ReadAsync(factory.DatabasePath);
        var preview = await PreviewAsync(client);

        (await LibraryVersion.ReadAsync(factory.DatabasePath))
            .Should().Be(before, "reading a library proposes, it never writes");

        // Neither book carries a read date, so both are purely what this import
        // brought and undo takes both back.
        var result = await ImportAsync(client,
            Decision(preview, 2, "EPUB", collections: true),
            Decision(preview, 3, "EPUB", collections: true));

        var committed = await LibraryVersion.ReadAsync(factory.DatabasePath);
        committed.Should().BeGreaterThan(before,
            "the rows a commit writes are portable state, so the library version advances");

        await using (var db = CreateContext(factory.DatabasePath))
        {
            var books = await db.Books
                .Include(book => book.Work)
                .Include(book => book.BookCollections)
                .OrderBy(book => book.Title)
                .ToListAsync();
            books.Should().HaveCount(2);
            result.Books.Should().OnlyContain(book => book.Status == "imported" && book.Created);

            var alice = books.Single(book => book.Title == "Alice's Adventures in Wonderland");
            alice.Metadata.Categories.Should().Be("classics, fantasy");
            alice.Progress.FinishedAt.Should().BeNull("Calibre says it was read, with no date");

            var collections = await db.Collections.AsNoTracking()
                .OrderBy(collection => collection.Name)
                .ToListAsync();
            collections.Select(collection => collection.Name).Should().Equal(
                "classics", "fantasy", "science fiction");
            alice.BookCollections.Select(membership => membership.CollectionId)
                .Should().BeEquivalentTo(
                    collections.Where(c => c.Name is "classics" or "fantasy").Select(c => c.Id));
            var timeMachine = books.Single(book => book.Title == "The Time Machine");
            timeMachine.BookCollections.Select(membership => membership.CollectionId)
                .Should().Equal(collections.Single(c => c.Name == "science fiction").Id);
        }

        var undo = await UndoAsync(client, result.BatchId!.Value);
        undo.Removed.Should().Be(2);
        undo.Kept.Should().Be(0);

        var undone = await LibraryVersion.ReadAsync(factory.DatabasePath);
        undone.Should().BeGreaterThan(committed,
            "taking the import back is portable state too, so screens refresh again");

        await using (var db = CreateContext(factory.DatabasePath))
        {
            (await db.Books.CountAsync()).Should().Be(0, "both books were added by this import");
            (await db.BookCollections.CountAsync()).Should().Be(0, "a deleted book takes its memberships with it");
            // The empty-collection prune runs before the created books are
            // deleted, so a collection whose only members were those books has
            // not looked empty yet and stays. Undo has always done this; the
            // refactor must not change it.
            (await db.Collections.CountAsync()).Should().Be(3);
            (await db.BookImportBatches.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Undo_keeps_a_created_book_that_already_looks_read()
    {
        // Calibre records the read date as the edition's finish date when the
        // book is created, so the book looks read from the moment it arrives.
        // Undo has never taken such a book back, and this pins that rule.
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var preview = await PreviewAsync(client);

        var result = await ImportAsync(client, Decision(preview, 1, "EPUB", collections: true));
        var undo = await UndoAsync(client, result.BatchId!.Value);

        undo.Removed.Should().Be(1);
        undo.Kept.Should().Be(1, "a book that already looks read stays");

        await using var db = CreateContext(factory.DatabasePath);
        var saved = await db.Books
            .Include(book => book.Work)
            .Include(book => book.BookCollections)
            .SingleAsync();
        saved.Title.Should().Be("Pride and Prejudice");
        saved.Work!.Rating.Should().Be(5);
        saved.Progress.FinishedAt.Should().Be(new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        saved.FileDetails.HasFile.Should().BeTrue("the import's file stays with the book it kept");
        saved.BookCollections.Should().HaveCount(2, "a kept book never had its tags taken back");
        (await db.Collections.CountAsync()).Should().Be(2, "both still hold the book undo kept");
    }

    [Fact]
    public async Task Undo_of_a_matched_book_removes_only_the_file_it_wrote_and_the_tags_it_added()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var book = await CreateBookAsync(client, "Pride and Prejudice", "Jane Austen", "9780141439518");
        var before = await LibraryVersion.ReadAsync(factory.DatabasePath);
        var preview = await PreviewAsync(client);

        var result = await ImportAsync(client, Decision(preview, 1, "EPUB", collections: true));
        var committed = await LibraryVersion.ReadAsync(factory.DatabasePath);
        committed.Should().BeGreaterThan(before);

        await using (var db = CreateContext(factory.DatabasePath))
        {
            var saved = await db.Books
                .Include(candidate => candidate.Work)
                .Include(candidate => candidate.BookCollections)
                .SingleAsync();
            saved.Id.Should().Be(book.Id);
            saved.FileDetails.HasFile.Should().BeTrue();
            saved.Work!.Rating.Should().Be(0, "an existing library value is not displaced");
            saved.BookCollections.Should().HaveCount(2, "classics and romance were not in it");
        }

        var undo = await UndoAsync(client, result.BatchId!.Value);
        undo.Removed.Should().Be(1);
        undo.Kept.Should().Be(0);
        (await LibraryVersion.ReadAsync(factory.DatabasePath)).Should().BeGreaterThan(committed);

        await using (var db = CreateContext(factory.DatabasePath))
        {
            var saved = await db.Books
                .Include(candidate => candidate.Work)
                .Include(candidate => candidate.BookCollections)
                .SingleAsync();
            saved.Id.Should().Be(book.Id, "the book was here before the import");
            saved.FileDetails.HasFile.Should().BeFalse("undo removes the file this import wrote");
            saved.BookCollections.Should().BeEmpty();
            saved.Work!.Rating.Should().Be(0);
            (await db.Collections.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Importing_the_same_library_again_writes_nothing_and_does_not_advance_the_version()
    {
        using var factory = new Factory(FixtureLibrary);
        var client = factory.CreateClient();
        var preview = await PreviewAsync(client);

        var first = await ImportAsync(client, Decision(preview, 3, "EPUB", collections: true));
        first.BatchId.Should().NotBeNull();
        var settled = await LibraryVersion.ReadAsync(factory.DatabasePath);

        var second = await ImportAsync(client, Decision(preview, 3, "EPUB", collections: true));

        second.BatchId.Should().BeNull("an import that writes nothing has nothing to undo");
        second.Books.Single().Status.Should().Be("unchanged");
        (await LibraryVersion.ReadAsync(factory.DatabasePath))
            .Should().Be(settled, "no write means no revision and no refresh");
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

        return new CalibreImportResult(state.BatchId, "calibre", state.Results);
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
        string? isbn = null)
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
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
}

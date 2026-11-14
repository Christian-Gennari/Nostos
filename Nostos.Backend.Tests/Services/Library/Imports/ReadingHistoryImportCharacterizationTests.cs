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

namespace Nostos.Backend.Tests.Services.Library.Imports;

/// <summary>
/// What a reading-history commit and its undo do to the library (issue #1625),
/// pinned before the import values and shelves were routed through the library
/// service: the work values and finish date it sets, the shelves it maps, the
/// note it captures, that the library version advances for them (so open screens
/// refresh) and not for a commit that writes nothing, and that undo restores the
/// previous values and removes the memberships, the note and the book it added.
/// </summary>
public sealed class ReadingHistoryImportCharacterizationTests : IClassFixture<LibraryEndpointFactory>, IAsyncLifetime
{
    private readonly LibraryEndpointFactory _factory;

    public ReadingHistoryImportCharacterizationTests(LibraryEndpointFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetStateAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Goodreads => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "goodreads", "goodreads_library_export.csv");

    [Fact]
    public async Task Commit_sets_values_shelves_and_a_note_and_advances_the_version_and_undo_restores_them()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");
        var before = await LibraryVersion.ReadAsync(_factory.DatabasePath);

        var result = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id, MapShelves: true));

        result.BatchId.Should().NotBeNull();
        var committed = await LibraryVersion.ReadAsync(_factory.DatabasePath);
        committed.Should().BeGreaterThan(before,
            "the values, memberships and note a commit writes are portable state");

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            var saved = await db.Books
                .Include(candidate => candidate.Work)
                .Include(candidate => candidate.BookCollections)
                .SingleAsync(candidate => candidate.Id == book.Id);
            saved.Work!.Rating.Should().Be(5);
            saved.Work.PersonalReview.Should().Contain("drawn from memory");
            saved.Progress.Rating.Should().Be(0, "the value belongs to the book, not this copy");
            saved.Progress.FinishedAt.Should().Be(new DateTime(2021, 3, 14, 0, 0, 0, DateTimeKind.Utc));

            var collections = await db.Collections.AsNoTracking()
                .OrderBy(collection => collection.Name)
                .ToListAsync();
            collections.Select(collection => collection.Name).Should().Equal("fantasy", "favourites");
            saved.BookCollections.Select(membership => membership.CollectionId)
                .Should().BeEquivalentTo(collections.Select(collection => collection.Id));
            (await db.Notes.CountAsync()).Should().Be(1);
        }

        (await client.DeleteAsync($"/api/books/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var undone = await LibraryVersion.ReadAsync(_factory.DatabasePath);
        undone.Should().BeGreaterThan(committed,
            "taking the import back is portable state too, so screens refresh again");

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            var saved = await db.Books
                .Include(candidate => candidate.Work)
                .Include(candidate => candidate.BookCollections)
                .SingleAsync(candidate => candidate.Id == book.Id);
            saved.Work!.Rating.Should().Be(0);
            saved.Work.PersonalReview.Should().BeNull();
            saved.Progress.FinishedAt.Should().BeNull();
            saved.BookCollections.Should().BeEmpty();
            (await db.Collections.CountAsync()).Should().Be(0, "the import created both collections");
            (await db.Notes.CountAsync()).Should().Be(0, "the private note came from the import");
            (await db.BookImportBatches.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Reimporting_the_same_file_writes_nothing_and_does_not_advance_the_version()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client, "The Cartographer's Daughter", "Ada Vance", "9780000000019");

        await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id, MapShelves: true));
        var settled = await LibraryVersion.ReadAsync(_factory.DatabasePath);

        var repeat = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:101", book.Id, MapShelves: true));

        repeat.BatchId.Should().BeNull("an import that changes nothing has nothing to undo");
        repeat.Books.Single(entry => entry.SourceKey == "goodreads:id:101").Status.Should().Be("unchanged");
        (await LibraryVersion.ReadAsync(_factory.DatabasePath))
            .Should().Be(settled, "no write means no revision and no refresh");
    }

    [Fact]
    public async Task A_book_the_import_created_keeps_its_values_until_undo_removes_it_and_advances_the_version()
    {
        using var client = _factory.CreateClient();
        var before = await LibraryVersion.ReadAsync(_factory.DatabasePath);

        var result = await CommitAsync(client, Goodreads,
            new ReadingHistoryImportDecision("goodreads:id:110", Create: true));

        var row = result.Books.Single(entry => entry.SourceKey == "goodreads:id:110");
        row.Created.Should().BeTrue();
        row.BookId.Should().NotBeNull();
        var committed = await LibraryVersion.ReadAsync(_factory.DatabasePath);
        committed.Should().BeGreaterThan(before);

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            var saved = await db.Books.Include(candidate => candidate.Work).SingleAsync();
            saved.Work!.Rating.Should().Be(4);
            saved.Work.PersonalReview.Should().Contain("controls the spice");
        }

        (await client.DeleteAsync($"/api/books/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LibraryVersion.ReadAsync(_factory.DatabasePath)).Should().BeGreaterThan(committed);

        await using (var db = CreateContext(_factory.DatabasePath))
        {
            (await db.Books.CountAsync()).Should().Be(0, "nothing else had come to the book this import added");
            (await db.Works.CountAsync()).Should().Be(0);
            (await db.BookImportBatches.CountAsync()).Should().Be(0);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<ReadingHistoryImportResult> CommitAsync(
        HttpClient client,
        string path,
        params ReadingHistoryImportDecision[] decisions)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
        body.Add(new StringContent(JsonSerializer.Serialize(decisions, JsonSerializerOptions.Web)), "decisions");

        var response = await client.PostAsync("/api/books/imports/commit", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReadingHistoryImportResult>())!;
    }

    private static async Task<BookDto> CreateBookAsync(
        HttpClient client,
        string title,
        string? author = null,
        string? isbn = null)
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
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
}

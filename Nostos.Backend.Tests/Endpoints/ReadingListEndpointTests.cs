using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// The reading list holds books the user wants and does not have. These tests
/// pin the two things that make it worth having: an entry needs nothing but a
/// title, and it never leaks into the library.
/// </summary>
public sealed class ReadingListEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public ReadingListEndpointTests(LibraryEndpointFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    /// <summary>The class shares one database, so every test starts from an empty list.</summary>
    private async Task ResetAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        db.ReadingListEntries.RemoveRange(await db.ReadingListEntries.ToListAsync());
        await db.SaveChangesAsync();
    }

    private async Task<ReadingListEntryDto> AddAsync(
        string title, string? author = null, string? why = null, string? recommendedBy = null,
        bool readNext = false, string? isbn = null)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/reading-list",
            new SaveReadingListEntryDto(title, author, isbn, null, why, recommendedBy, readNext));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ReadingListEntryDto>())!;
    }

    private async Task<List<ReadingListEntryDto>> ListAsync() =>
        (await Client.GetFromJsonAsync<List<ReadingListEntryDto>>("/api/reading-list"))!;

    private async Task<Guid> SeedBookAsync(string title, string? author = null, string? isbn = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var book = new PhysicalBookModel { Title = title, Author = author, Isbn = isbn, NormalizedIsbn = isbn };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task An_entry_needs_only_a_title_and_starts_in_someday()
    {
        await ResetAsync();

        var created = await AddAsync("  Middlemarch  ");

        created.Title.Should().Be("Middlemarch");
        created.Author.Should().BeNull();
        created.NextPosition.Should().BeNull();

        (await ListAsync()).Should().ContainSingle().Which.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task An_entry_is_not_a_library_book()
    {
        await ResetAsync();
        var before = await Client.GetFromJsonAsync<LibraryStatusCountsDto>("/api/books/status-counts");

        await AddAsync("The Rings of Saturn", "W. G. Sebald");

        var after = await Client.GetFromJsonAsync<LibraryStatusCountsDto>("/api/books/status-counts");
        after.Should().Be(before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_title_is_refused(string? title)
    {
        await ResetAsync();

        var response = await Client.PostAsJsonAsync(
            "/api/reading-list", new SaveReadingListEntryDto(title));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("invalid_reading_list_entry");
        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_isbn_is_normalized_and_an_invalid_one_refused()
    {
        await ResetAsync();

        var created = await AddAsync("Stoner", isbn: "978-1-59017-199-8");
        created.Isbn.Should().Be("9781590171998");

        var refused = await Client.PostAsJsonAsync(
            "/api/reading-list", new SaveReadingListEntryDto("Stoner", Isbn: "12345"));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(refused)).Should().Be("invalid_isbn");
    }

    [Fact]
    public async Task The_list_is_next_up_in_shelf_order_then_someday_newest_first()
    {
        await ResetAsync();
        var older = await AddAsync("Older someday");
        await Task.Delay(20);
        var newer = await AddAsync("Newer someday");
        var first = await AddAsync("First next", readNext: true);
        var second = await AddAsync("Second next", readNext: true);

        var list = await ListAsync();

        list.Select(e => e.Id).Should().Equal(first.Id, second.Id, newer.Id, older.Id);
        list.Select(e => e.NextPosition).Should().Equal(0, 1, null, null);
    }

    [Fact]
    public async Task Setting_next_replaces_the_whole_shelf()
    {
        await ResetAsync();
        var a = await AddAsync("A", readNext: true);
        var b = await AddAsync("B", readNext: true);
        var c = await AddAsync("C");

        // Promote C to the front, keep B, drop A back to Someday.
        var response = await Client.PutAsJsonAsync(
            "/api/reading-list/next", new SetReadingListNextDto([c.Id, b.Id]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = (await response.Content.ReadFromJsonAsync<List<ReadingListEntryDto>>())!;
        list.Select(e => e.Id).Should().Equal(c.Id, b.Id, a.Id);
        list.Select(e => e.NextPosition).Should().Equal(0, 1, null);
    }

    [Fact]
    public async Task Setting_next_refuses_unknown_and_repeated_books_and_changes_nothing()
    {
        await ResetAsync();
        var a = await AddAsync("A", readNext: true);

        var unknown = await Client.PutAsJsonAsync(
            "/api/reading-list/next", new SetReadingListNextDto([Guid.NewGuid()]));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var repeated = await Client.PutAsJsonAsync(
            "/api/reading-list/next", new SetReadingListNextDto([a.Id, a.Id]));
        repeated.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missing = await Client.PutAsJsonAsync(
            "/api/reading-list/next", new SetReadingListNextDto(null));
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ListAsync()).Single().NextPosition.Should().Be(0);
    }

    [Fact]
    public async Task Updating_replaces_the_fields_and_keeps_the_shelf_slot()
    {
        await ResetAsync();
        var created = await AddAsync("rings of saturn", readNext: true);

        var response = await Client.PutAsJsonAsync(
            $"/api/reading-list/{created.Id}",
            new SaveReadingListEntryDto(
                "The Rings of Saturn", "W. G. Sebald", null, "1995", "Walking and memory.", "Anna"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<ReadingListEntryDto>())!;
        updated.Title.Should().Be("The Rings of Saturn");
        updated.Author.Should().Be("W. G. Sebald");
        updated.PublishedDate.Should().Be("1995");
        updated.Why.Should().Be("Walking and memory.");
        updated.RecommendedBy.Should().Be("Anna");
        updated.NextPosition.Should().Be(0);
        updated.CreatedAt.Should().Be(created.CreatedAt);
    }

    [Fact]
    public async Task Deleting_removes_the_entry_and_a_second_delete_is_not_found()
    {
        await ResetAsync();
        var created = await AddAsync("Pedro Páramo");

        (await Client.DeleteAsync($"/api/reading-list/{created.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Client.DeleteAsync($"/api/reading-list/{created.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_entry_names_the_library_book_it_already_is()
    {
        await ResetAsync();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var bookId = await SeedBookAsync($"Owned Title {marker}", "Real Author");

        var sameTitle = await AddAsync($"owned title {marker}");
        var sameAuthor = await AddAsync($"Owned Title {marker}", "real author");
        var otherAuthor = await AddAsync($"Owned Title {marker}", "Someone Else");
        var unrelated = await AddAsync($"Unowned {marker}");

        sameTitle.OwnedBookId.Should().Be(bookId);
        sameAuthor.OwnedBookId.Should().Be(bookId);
        otherAuthor.OwnedBookId.Should().BeNull();
        unrelated.OwnedBookId.Should().BeNull();
    }

    [Fact]
    public async Task Getting_the_book_moves_the_reason_onto_it_and_removes_the_entry()
    {
        await ResetAsync();
        var bookId = await SeedBookAsync($"Acquired {Guid.NewGuid():N}");
        var entry = await AddAsync("Middlemarch", why: "Winter book.", recommendedBy: "Anna");

        var response = await Client.PostAsJsonAsync(
            $"/api/reading-list/{entry.Id}/acquired", new ReadingListAcquiredDto(bookId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync()).Should().BeEmpty();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var note = await db.Notes.SingleAsync(n => n.BookId == bookId);
        note.Content.Should().Be("Why I wanted to read this: Winter book.\n\nRecommended by: Anna");
    }

    [Fact]
    public async Task Getting_a_book_with_no_reason_leaves_no_note()
    {
        await ResetAsync();
        var bookId = await SeedBookAsync($"Acquired {Guid.NewGuid():N}");
        var entry = await AddAsync("Stoner");

        (await Client.PostAsJsonAsync(
            $"/api/reading-list/{entry.Id}/acquired", new ReadingListAcquiredDto(bookId)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        (await db.Notes.AnyAsync(n => n.BookId == bookId)).Should().BeFalse();
        (await db.ReadingListEntries.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Getting_an_unknown_book_keeps_the_entry()
    {
        await ResetAsync();
        var entry = await AddAsync("Stoner", why: "Slow and plain.");

        var response = await Client.PostAsJsonAsync(
            $"/api/reading-list/{entry.Id}/acquired", new ReadingListAcquiredDto(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("book_not_found");
        (await ListAsync()).Should().ContainSingle();
    }
}

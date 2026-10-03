using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

// Full-host HTTP coverage for the five note routes, now served by INoteService
// (issue #260 §1). Mirrors LibraryEndpointTests: the refactor moved note
// semantics into the service, so these prove the legacy REST contract — every
// status code and error body — survived byte-for-byte.
public sealed class NoteEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public NoteEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    // ------------------------------------------------------------------
    // GET /api/books/{bookId}/notes
    // ------------------------------------------------------------------

    [Fact]
    public async Task Get_notes_by_book_returns_notes_and_an_unknown_book_is_an_empty_list()
    {
        var book = await CreateBookAsync();
        var note = await CreateNoteAsync(book.Id, "a note body");

        var response = await Client.GetAsync($"/api/books/{book.Id}/notes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var notes = await response.Content.ReadFromJsonAsync<NoteDto[]>();
        notes.Should().ContainSingle().Which.Id.Should().Be(note.Id);

        // No 404 today — keep it that way.
        var missing = await Client.GetAsync($"/api/books/{Guid.NewGuid()}/notes");
        missing.StatusCode.Should().Be(HttpStatusCode.OK);
        (await missing.Content.ReadFromJsonAsync<NoteDto[]>()).Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // GET /api/notes/{id}
    // ------------------------------------------------------------------

    [Fact]
    public async Task Get_note_by_id_returns_canonical_note_evidence_and_missing_is_404()
    {
        var book = await CreateBookAsync();
        var note = await CreateNoteAsync(book.Id, "a thought about [[Attention]]");

        var response = await Client.GetAsync($"/api/notes/{note.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var hit = await response.Content.ReadFromJsonAsync<NoteSearchHitDto>();
        hit!.Id.Should().Be(note.Id);
        hit.BookId.Should().Be(book.Id);
        hit.BookTitle.Should().Be(book.Title);
        hit.Content.Should().Contain("[[Attention]]");
        hit.TopicNames.Should().Contain("Attention");

        var missing = await Client.GetAsync($"/api/notes/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------
    // GET /api/notes/search
    // ------------------------------------------------------------------

    [Fact]
    public async Task Search_notes_returns_hits_with_snippet_and_book_title()
    {
        var book = await CreateBookAsync();
        var note = await CreateNoteAsync(book.Id, "Sisyphus is condemned to roll the stone uphill");

        var response = await Client.GetAsync("/api/notes/search?query=Sisyphus&limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var hits = await response.Content.ReadFromJsonAsync<NoteSearchHitDto[]>();
        var hit = hits!.Single(h => h.Id == note.Id);
        hit.BookTitle.Should().Be(book.Title);
        hit.Snippet.Should().Contain("Sisyphus");
    }

    // ------------------------------------------------------------------
    // GET /api/notes/unlinked
    // ------------------------------------------------------------------

    [Fact]
    public async Task Browse_notes_pages_linked_and_unlinked_material_and_filters_without_topics()
    {
        var book = await CreateBookAsync();
        var linked = await CreateNoteAsync(book.Id, "a thought about [[Attention]]");
        var unlinked = await CreateNoteAsync(book.Id, "an unconnected thought");

        var all = await Client.GetFromJsonAsync<NoteSearchPageDto>(
            $"/api/notes?bookId={book.Id}&limit=1&offset=0");
        all!.TotalCount.Should().Be(2);
        all.Items.Should().ContainSingle();

        var second = await Client.GetFromJsonAsync<NoteSearchPageDto>(
            $"/api/notes?bookId={book.Id}&limit=1&offset=1");
        all.Items.Concat(second!.Items).Select(n => n.Id).Should().BeEquivalentTo([linked.Id, unlinked.Id]);

        var without = await Client.GetFromJsonAsync<NoteSearchPageDto>(
            $"/api/notes?bookId={book.Id}&withoutTopics=true&query=unconnected");
        without!.TotalCount.Should().Be(1);
        without.Items.Should().ContainSingle().Which.Id.Should().Be(unlinked.Id);
        without.Items.Single().TopicNames.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // GET /api/notes/unlinked
    // ------------------------------------------------------------------

    [Fact]
    public async Task Unlinked_notes_returns_a_page_with_the_total_and_clamped_bounds()
    {
        var book = await CreateBookAsync();
        var note = await CreateNoteAsync(book.Id, $"an unlinked note {Guid.NewGuid():N}");

        var page = await Client.GetFromJsonAsync<NoteSearchPageDto>("/api/notes/unlinked?limit=200&offset=0");

        page.Should().NotBeNull();
        page!.Limit.Should().Be(200);
        page.Offset.Should().Be(0);
        page.TotalCount.Should().BeGreaterThan(0);
        page.Items.Should().Contain(i => i.Id == note.Id);

        // limit clamps to 200 and a negative offset floors at 0.
        var clamped = await Client.GetFromJsonAsync<NoteSearchPageDto>("/api/notes/unlinked?limit=5000&offset=-3");
        clamped!.Limit.Should().Be(200);
        clamped.Offset.Should().Be(0);
    }

    // ------------------------------------------------------------------
    // POST /api/books/{bookId}/notes
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_note_returns_201_with_location_and_dto()
    {
        var book = await CreateBookAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/books/{book.Id}/notes", new { content = "captured note" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var note = await response.Content.ReadFromJsonAsync<NoteDto>();
        note!.BookId.Should().Be(book.Id);
        note.BookTitle.Should().Be(book.Title);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().EndWith($"/api/notes/{note.Id}");
    }

    [Fact]
    public async Task Create_note_for_unknown_book_returns_404_with_exact_body()
    {
        var bookId = Guid.NewGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/books/{bookId}/notes", new { content = "a note" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorAsync(response)).Should().Be($"Book {bookId} not found.");
    }

    [Fact]
    public async Task Create_note_without_content_or_selected_text_returns_400_with_exact_body()
    {
        var book = await CreateBookAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/books/{book.Id}/notes", new { content = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(response)).Should().Be("Note must have content or selected text.");
    }

    // ------------------------------------------------------------------
    // PUT /api/notes/{id}
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_note_returns_200_with_the_updated_note()
    {
        var book = await CreateBookAsync();
        var note = await CreateNoteAsync(book.Id, "before");

        var response = await Client.PutAsJsonAsync(
            $"/api/notes/{note.Id}", new { content = "after", selectedText = "the quote" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<NoteDto>();
        updated!.Id.Should().Be(note.Id);
        updated.Content.Should().Be("after");
        updated.SelectedText.Should().Be("the quote");
    }

    [Fact]
    public async Task Update_unknown_note_returns_404_with_no_custom_body()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/notes/{Guid.NewGuid()}", new { content = "x" });

        await AssertBareNotFoundAsync(response);
    }

    // ------------------------------------------------------------------
    // DELETE /api/notes/{id}
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_note_returns_204_then_the_second_delete_is_404()
    {
        var book = await CreateBookAsync();
        var note = await CreateNoteAsync(book.Id, "to delete");

        var deleted = await Client.DeleteAsync($"/api/notes/{note.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await deleted.Content.ReadAsStringAsync()).Should().BeEmpty();

        var again = await Client.DeleteAsync($"/api/notes/{note.Id}");
        await AssertBareNotFoundAsync(again);
    }

    [Fact]
    public async Task Delete_unknown_note_returns_404_with_no_custom_body()
    {
        var response = await Client.DeleteAsync($"/api/notes/{Guid.NewGuid()}");

        await AssertBareNotFoundAsync(response);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<BookDto> CreateBookAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Note Book {Guid.NewGuid():N}",
            author = "An Author",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private async Task<NoteDto> CreateNoteAsync(Guid bookId, string content)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/books/{bookId}/notes", new { content });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<NoteDto>())!;
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetString();
    }

    /// <summary>
    /// PUT/DELETE on an unknown note return a bare <c>Results.NotFound()</c> — no
    /// custom error body. The app registers <c>UseStatusCodePages()</c> globally,
    /// so the wire body is the stock ProblemDetails; a custom <c>error</c> field
    /// would mean the handler contract changed.
    /// </summary>
    private static async Task AssertBareNotFoundAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("\"error\"");
    }
}

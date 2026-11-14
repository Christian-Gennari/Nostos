using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library.Imports;

/// <summary>
/// What a highlight commit and its undo do to the library (issue #1625), pinned
/// before the created-book cleanup was shared with the other importers: the
/// notes and the book it adds, that the library version advances for them (so
/// open screens refresh), that undo removes a book only this import added, and
/// keeps one the owner has written on since.
/// </summary>
public sealed class HighlightImportCharacterizationTests
    : IClassFixture<LibraryEndpointFactory>, IAsyncLifetime
{
    private readonly LibraryEndpointFactory _factory;

    public HighlightImportCharacterizationTests(LibraryEndpointFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetStateAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Undo_removes_the_book_the_import_added_and_advances_the_version()
    {
        using var client = _factory.CreateClient();
        var clippings = ImportFixtures.KindleEnglish;
        var before = await LibraryVersion.ReadAsync(_factory.DatabasePath);
        var preview = await PreviewAsync(client, clippings);

        var meditations = preview.Books.Single(book => book.Title == "Meditations (Penguin Classics)");
        var result = await CommitAsync(client,
            clippings,
            new HighlightImportDecision(meditations.SourceKey, Create: true));

        var committed = await LibraryVersion.ReadAsync(_factory.DatabasePath);
        committed.Should().BeGreaterThan(before,
            "the book and notes a commit writes are portable state");

        var created = result.Books.Single(book => book.SourceKey == meditations.SourceKey);
        created.Created.Should().BeTrue();
        Guid bookId;
        await using (var db = CreateContext(_factory.DatabasePath))
        {
            var book = await db.Books.AsNoTracking().SingleAsync();
            bookId = book.Id;
            book.Title.Should().Be("Meditations (Penguin Classics)");
            (await db.Notes.CountAsync(note => note.BookId == bookId)).Should().Be(1);
        }

        (await client.DeleteAsync($"/api/notes/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LibraryVersion.ReadAsync(_factory.DatabasePath)).Should().BeGreaterThan(committed,
            "taking the import back is portable state too, so screens refresh again");

        await using (var verify = CreateContext(_factory.DatabasePath))
        {
            (await verify.Books.CountAsync()).Should().Be(0, "nothing else had come to the book this import added");
            (await verify.Works.CountAsync()).Should().Be(0);
            (await verify.Notes.CountAsync()).Should().Be(0);
            (await verify.NoteImportBatches.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Undo_keeps_a_book_the_import_added_when_the_owner_wrote_on_it_since()
    {
        using var client = _factory.CreateClient();
        var clippings = ImportFixtures.KindleEnglish;
        var preview = await PreviewAsync(client, clippings);
        var meditations = preview.Books.Single(book => book.Title == "Meditations (Penguin Classics)");
        var result = await CommitAsync(client,
            clippings,
            new HighlightImportDecision(meditations.SourceKey, Create: true));
        var bookId = result.Books.Single(book => book.SourceKey == meditations.SourceKey).BookId!.Value;

        (await client.PostAsJsonAsync($"/api/books/{bookId}/notes", new CreateNoteDto("My own thought")))
            .EnsureSuccessStatusCode();

        (await client.DeleteAsync($"/api/notes/imports/batches/{result.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var db = CreateContext(_factory.DatabasePath);
        (await db.Books.CountAsync()).Should().Be(1, "the owner has written on it since the import");
        (await db.Notes.CountAsync()).Should().Be(1);
        (await db.Notes.CountAsync(note => note.BookId == bookId)).Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<HighlightImportPreview> PreviewAsync(HttpClient client, string path)
    {
        var response = await UploadAsync(client, "preview", path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<HighlightImportPreview>())!;
    }

    private static async Task<HighlightImportResult> CommitAsync(
        HttpClient client,
        string path,
        params HighlightImportDecision[] decisions)
    {
        var response = await UploadAsync(client, "commit", path, decisions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
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

    private static NostosDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new NostosDbContext(options);
    }
}

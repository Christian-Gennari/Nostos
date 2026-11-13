using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class KoreaderNoteImportEndpointTests
    : IClassFixture<LibraryEndpointFactory>, IAsyncLifetime
{
    private readonly LibraryEndpointFactory _factory;

    public KoreaderNoteImportEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetStateAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Koreader_metadata_import_matches_book_imports_notes_and_is_idempotent()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client, "Synthetic Reader Book", "Ada Reader");
        var fixture = ImportFixtures.KoreaderEpub;

        var first = await UploadAsync(client, fixture);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstReport = await first.Content.ReadFromJsonAsync<KoreaderImportReport>();
        firstReport!.Status.Should().Be("matched");
        firstReport.BookId.Should().Be(book.Id);
        firstReport.AnnotationCount.Should().Be(2);
        firstReport.ImportedCount.Should().Be(2);
        firstReport.DuplicateCount.Should().Be(0);

        var second = await UploadAsync(client, fixture);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondReport = await second.Content.ReadFromJsonAsync<KoreaderImportReport>();
        secondReport!.ImportedCount.Should().Be(0);
        secondReport.DuplicateCount.Should().Be(2);

        await using var db = CreateContext(_factory.DatabasePath);
        var notes = await db.Notes
            .AsNoTracking()
            .Where(note => note.BookId == book.Id)
            .OrderBy(note => note.SourceAnchorValue)
            .ToListAsync();

        notes.Should().HaveCount(2);
        notes.Should().OnlyContain(note => note.CaptureSource == "import");
        // KOReader's own colour names are not the four pens: an import arrives in the default pen.
        notes.Should().OnlyContain(note => note.HighlightColour == "sage");
        notes.Should().OnlyContain(note => note.ProcessingMode == "verbatim" && note.RawContent == null);
        notes.Should().Contain(note =>
            note.SelectedText == "A synthetic highlighted sentence."
            && note.Content == "Remember this connection."
            && note.SourceAnchorKind == "koreader_xpointer");
        notes.Should().Contain(note =>
            note.SelectedText == "A second synthetic highlight."
            && note.Content == string.Empty);
        // The sidecar's `datetime` is the note's date, not the time of import (#1367).
        notes.Should().OnlyContain(note => note.CreatedAt.Year == 2026 && note.CreatedAt.Month == 9);
    }

    [Fact]
    public async Task Koreader_metadata_import_reports_unmatched_without_creating_notes()
    {
        using var client = _factory.CreateClient();

        var response = await UploadAsync(client, ImportFixtures.KoreaderEpub);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await response.Content.ReadFromJsonAsync<KoreaderImportReport>();
        report!.Status.Should().Be("unmatched");
        report.ImportedCount.Should().Be(0);

        await using var db = CreateContext(_factory.DatabasePath);
        (await db.Notes.CountAsync()).Should().Be(0);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path)
    {
        using var body = new MultipartFormDataContent();
        await using var stream = File.OpenRead(path);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        body.Add(file, "file", "metadata.lua");
        return await client.PostAsync("/api/notes/import/koreader", body);
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

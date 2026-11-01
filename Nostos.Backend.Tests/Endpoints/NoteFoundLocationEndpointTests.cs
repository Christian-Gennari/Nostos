using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// #1338: a reader may save where it found a quote Ask Nostos saved. It may do
/// that for nothing else, and never in place of a location a note already has.
/// </summary>
public sealed class NoteFoundLocationEndpointTests
{
    private const string Range = "epubcfi(/6/8!/4/8,/1:0,/1:33)";
    private const string Quote = "The words of the book";
    private const string PageFiveRects = """{"pageNumber":5,"rects":[{"left":0.1,"top":0.2,"width":0.5,"height":0.02}]}""";
    private const string PageFiveBlank = """{"pageNumber":5,"yPercent":0,"rects":[]}""";

    private readonly LibraryEndpointFactory _factory = new();
    private readonly HttpClient _client;

    public NoteFoundLocationEndpointTests() => _client = _factory.CreateClient();

    private async Task<Guid> BookAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/books", new { type = "physical", title = $"A Book {Guid.NewGuid()}", author = "An Author" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!.Id;
    }

    private async Task<NoteDto> NoteAsync(object note)
    {
        var response = await _client.PostAsJsonAsync($"/api/books/{await BookAsync()}/notes", note);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<NoteDto>())!;
    }

    private Task<HttpResponseMessage> LocateAsync(Guid noteId, string? location) =>
        _client.PutAsJsonAsync($"/api/notes/{noteId}/location", new NoteLocationDto(location));

    private async Task<Held> ReadAsync(Guid noteId) =>
        (await _client.GetFromJsonAsync<Held>($"/api/notes/{noteId}"))!;

    private sealed record Held(string? CfiRange);

    [Fact]
    public async Task An_Ask_Nostos_quote_in_an_epub_gets_its_range_once()
    {
        var note = await NoteAsync(new { content = "", selectedText = Quote });

        var located = await LocateAsync(note.Id, Range);

        located.StatusCode.Should().Be(HttpStatusCode.OK);
        (await located.Content.ReadFromJsonAsync<NoteDto>())!.CfiRange.Should().Be(Range);

        var second = await LocateAsync(note.Id, "epubcfi(/6/2!/4/2,/1:0,/1:5)");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(note.Id)).CfiRange.Should().Be(Range, "the first location is kept");
    }

    [Fact]
    public async Task A_page_top_anchor_does_not_stop_an_epub_quote_being_located()
    {
        var note = await NoteAsync(new
        {
            content = "",
            selectedText = Quote,
            sourceAnchorKind = "epub_cfi",
            sourceAnchorValue = "epubcfi(/6/4!/4/2/1:0)",
            anchorVerified = true,
        });

        (await LocateAsync(note.Id, Range)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(note.Id)).CfiRange.Should().Be(Range);
    }

    [Fact]
    public async Task A_bare_position_is_not_a_location_for_a_quote()
    {
        var note = await NoteAsync(new { content = "", selectedText = Quote });

        (await LocateAsync(note.Id, "epubcfi(/6/8!/4/8/1:0)")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(note.Id)).CfiRange.Should().BeNull();
    }

    [Fact]
    public async Task A_pdf_quote_keeps_its_page_and_gains_rects()
    {
        var note = await NoteAsync(new
        {
            content = "",
            selectedText = Quote,
            cfiRange = PageFiveBlank,
            sourceAnchorKind = "pdf_page",
            sourceAnchorValue = "5",
            anchorVerified = true,
        });

        (await LocateAsync(note.Id, PageFiveRects)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(note.Id)).CfiRange.Should().Be(PageFiveRects);

        var other = """{"pageNumber":5,"rects":[{"left":0.3,"top":0.4,"width":0.1,"height":0.02}]}""";
        (await LocateAsync(note.Id, other)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(note.Id)).CfiRange.Should().Be(PageFiveRects, "rects once saved are never replaced");
    }

    [Fact]
    public async Task A_pdf_quote_saved_before_the_page_was_bridged_gains_rects_too()
    {
        var note = await NoteAsync(new
        {
            content = "",
            selectedText = Quote,
            sourceAnchorKind = "pdf_page",
            sourceAnchorValue = "5",
            anchorVerified = true,
        });

        (await LocateAsync(note.Id, PageFiveRects)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(note.Id)).CfiRange.Should().Be(PageFiveRects);
    }

    [Theory]
    [InlineData("""{"pageNumber":6,"rects":[{"left":0.1,"top":0.2,"width":0.5,"height":0.02}]}""")] // another page
    [InlineData("""{"pageNumber":5,"rects":[]}""")] // nothing to draw
    [InlineData("""{"pageNumber":5,"rects":[{"left":0.1,"top":0.2,"width":9,"height":0.02}]}""")] // off the page
    [InlineData("""{"pageNumber":5,"rects":[{"left":0.1,"top":0.2}]}""")] // not a rectangle
    [InlineData("""{"pageNumber":"5","rects":[{"left":0.1,"top":0.2,"width":0.5,"height":0.02}]}""")]
    [InlineData("""{"pageNumber":5""")]
    [InlineData("page 5")]
    [InlineData(Range)] // an EPUB location on a PDF note
    public async Task A_pdf_quote_refuses_anything_but_rects_on_its_own_page(string location)
    {
        var note = await NoteAsync(new
        {
            content = "",
            selectedText = Quote,
            cfiRange = PageFiveBlank,
            sourceAnchorKind = "pdf_page",
            sourceAnchorValue = "5",
            anchorVerified = true,
        });

        (await LocateAsync(note.Id, location)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(note.Id)).CfiRange.Should().Be(PageFiveBlank);
    }

    [Fact]
    public async Task A_pdf_quote_with_no_page_cannot_be_given_one()
    {
        var note = await NoteAsync(new { content = "", selectedText = Quote });

        (await LocateAsync(note.Id, PageFiveRects)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(note.Id)).CfiRange.Should().BeNull();
    }

    [Fact]
    public async Task A_highlight_made_in_a_reader_is_never_moved()
    {
        var epub = await NoteAsync(new { content = "", selectedText = Quote, cfiRange = Range });
        var pdf = await NoteAsync(new { content = "", selectedText = Quote, cfiRange = PageFiveRects });

        await LocateAsync(epub.Id, "epubcfi(/6/2!/4/2,/1:0,/1:5)");
        await LocateAsync(pdf.Id, PageFiveRects.Replace("0.5", "0.6"));

        (await ReadAsync(epub.Id)).CfiRange.Should().Be(Range);
        (await ReadAsync(pdf.Id)).CfiRange.Should().Be(PageFiveRects);
    }

    [Fact]
    public async Task A_typed_note_a_photo_and_a_passage_stay_out_of_reach()
    {
        var typed = await NoteAsync(new { content = "\"The words of the book\" she said." });
        var photo = await NoteAsync(new { content = "", selectedText = Quote, captureSource = "photo" });
        var passage = await NoteAsync(new
        {
            content = "",
            selectedText = Quote,
            kind = "passage",
            passageTitle = "A passage",
            sourceAnchorKind = "epub_cfi",
            sourceAnchorValue = "epubcfi(/6/2!/4/2/1:0)",
            passageEnd = "epubcfi(/6/2!/4/2/1:9)",
        });

        foreach (var note in new[] { typed, photo, passage })
        {
            (await LocateAsync(note.Id, Range)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadAsync(note.Id)).CfiRange.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_saved_location_moves_the_library_revision()
    {
        var note = await NoteAsync(new { content = "", selectedText = Quote });
        string Revision()
        {
            using var scope = _factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<NostosDbContext>().LibraryStates.AsNoTracking().Single().StateVersion;
        }

        var before = Revision();
        (await LocateAsync(note.Id, Range)).StatusCode.Should().Be(HttpStatusCode.OK);
        Revision().Should().NotBe(before, "another device must hear of the change");
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

// Passages (issue #1014): a note of kind "passage" with two ends, a title and
// its text. The lists carry only its opening; GET /passage returns the rest.
public sealed class PassageEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public PassageEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    private static readonly string LongText = string.Join(' ', Enumerable.Repeat(
        "Imagine, then, that it takes place in Spain, in Seville, in the most terrible time of the Inquisition.",
        60));

    [Fact]
    public async Task An_epub_passage_is_saved_listed_by_its_opening_and_read_in_full()
    {
        var book = await CreateBookAsync();
        var response = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "Ivan's poem",
            kind = "passage",
            passageTitle = "  The Grand Inquisitor  ",
            sourceAnchorKind = "epub_cfi",
            sourceAnchorValue = "epubcfi(/6/14!/4/2/1:0)",
            passageEnd = "epubcfi(/6/16!/4/88/1:120)",
            selectedText = LongText,
            passageHtml = "<p class=\"calibre\" onclick=\"x()\">Imagine, <em>then</em></p><script>alert(1)</script><span>kept</span><img src=\"a.png\">",
            cfiRange = "epubcfi(/6/14!/4/2,/1:0,/1:5)",
            highlightColour = "blue",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<NoteDto>())!;
        created.Kind.Should().Be("passage");
        created.PassageTitle.Should().Be("The Grand Inquisitor");
        created.SourceAnchorValue.Should().Be("epubcfi(/6/14!/4/2/1:0)");
        created.PassageEnd.Should().Be("epubcfi(/6/16!/4/88/1:120)");
        created.AnchorVerified.Should().BeTrue();
        created.CfiRange.Should().BeNull("no reader paints a passage as a highlight");
        created.PassageWordCount.Should().Be(LongText.Split(' ').Length);
        created.SelectedText!.Length.Should().BeLessThan(LongText.Length);
        created.SelectedText.Should().EndWith("…");

        var listed = await Client.GetFromJsonAsync<List<NoteDto>>($"/api/books/{book.Id}/notes");
        listed!.Single().SelectedText.Should().Be(created.SelectedText);

        var passage = await Client.GetFromJsonAsync<PassageDto>($"/api/notes/{created.Id}/passage");
        passage!.Title.Should().Be("The Grand Inquisitor");
        passage.AnchorKind.Should().Be("epub_cfi");
        passage.Start.Should().Be("epubcfi(/6/14!/4/2/1:0)");
        passage.End.Should().Be("epubcfi(/6/16!/4/88/1:120)");
        passage.Text.Should().Be(LongText);
        passage.Html.Should().Be("<p>Imagine, <em>then</em></p>kept");
        passage.BookTitle.Should().Be(book.Title);
        passage.HighlightColour.Should().Be("blue");
    }

    [Fact]
    public async Task A_passage_is_found_by_words_from_its_middle_and_hit_rows_carry_its_title()
    {
        var book = await CreateBookAsync();
        var word = $"zosima{Guid.NewGuid():N}"[..14];
        var text = LongText + $" The elder {word} said nothing. " + LongText;
        var created = await CreatePassageAsync(book.Id, text);

        var hits = await Client.GetFromJsonAsync<List<NoteSearchHitDto>>($"/api/notes/search?query={word}");
        var hit = hits!.Single(h => h.Id == created.Id);
        hit.Kind.Should().Be("passage");
        hit.PassageTitle.Should().Be("The Grand Inquisitor");
        hit.Snippet.Should().Contain(word);
        hit.SelectedText!.Length.Should().BeLessThanOrEqualTo(PassageText.OpeningLength + 1);
    }

    [Fact]
    public async Task Editing_a_passage_changes_its_title_and_note_but_never_cuts_its_text()
    {
        var book = await CreateBookAsync();
        var created = await CreatePassageAsync(book.Id, LongText);

        // A client edits from a list row, which only has the opening.
        var response = await Client.PutAsJsonAsync($"/api/notes/{created.Id}", new
        {
            content = "Read this every year",
            selectedText = created.SelectedText,
            passageTitle = "The Inquisitor's speech",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var passage = await Client.GetFromJsonAsync<PassageDto>($"/api/notes/{created.Id}/passage");
        passage!.Title.Should().Be("The Inquisitor's speech");
        passage.Content.Should().Be("Read this every year");
        passage.Text.Should().Be(LongText);

        var blank = await Client.PutAsJsonAsync($"/api/notes/{created.Id}", new { content = "", passageTitle = " " });
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pdf_and_audio_passages_are_ranges_that_must_run_forwards()
    {
        var book = await CreateBookAsync();

        var pdf = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "",
            kind = "passage",
            passageTitle = "Chapter V",
            sourceAnchorKind = "pdf_page",
            sourceAnchorValue = "241",
            passageEnd = "256",
        });
        pdf.StatusCode.Should().Be(HttpStatusCode.Created, "a scanned PDF has no text, only its pages");

        var backwards = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "",
            kind = "passage",
            passageTitle = "Chapter V",
            sourceAnchorKind = "pdf_page",
            sourceAnchorValue = "256",
            passageEnd = "241",
        });
        backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var audio = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "",
            kind = "passage",
            passageTitle = "The poem",
            sourceAnchorKind = "audio_timestamp",
            sourceAnchorValue = "3600.5",
            passageEnd = "5400",
        });
        audio.StatusCode.Should().Be(HttpStatusCode.Created);
        var clip = (await audio.Content.ReadFromJsonAsync<NoteDto>())!;
        clip.PassageWordCount.Should().Be(0);
    }

    [Fact]
    public async Task An_incomplete_passage_or_an_unknown_kind_is_refused()
    {
        var book = await CreateBookAsync();

        var untitled = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "",
            kind = "passage",
            sourceAnchorKind = "epub_cfi",
            sourceAnchorValue = "epubcfi(/6/2!/4/2/1:0)",
            passageEnd = "epubcfi(/6/2!/4/8/1:0)",
            selectedText = "words",
        });
        untitled.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var noEnd = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "",
            kind = "passage",
            passageTitle = "T",
            sourceAnchorKind = "epub_cfi",
            sourceAnchorValue = "epubcfi(/6/2!/4/2/1:0)",
            selectedText = "words",
        });
        noEnd.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var noText = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "",
            kind = "passage",
            passageTitle = "T",
            sourceAnchorKind = "epub_cfi",
            sourceAnchorValue = "epubcfi(/6/2!/4/2/1:0)",
            passageEnd = "epubcfi(/6/2!/4/8/1:0)",
        });
        noText.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unknown = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "x",
            kind = "chapter",
        });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_ordinary_note_is_a_note_and_has_no_passage()
    {
        var book = await CreateBookAsync();
        var response = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new
        {
            content = "a thought",
            passageTitle = "ignored",
            passageEnd = "ignored",
        });
        var note = (await response.Content.ReadFromJsonAsync<NoteDto>())!;
        note.Kind.Should().Be("note");
        note.PassageTitle.Should().BeNull();
        note.PassageEnd.Should().BeNull();
        note.PassageWordCount.Should().BeNull();

        var passage = await Client.GetAsync($"/api/notes/{note.Id}/passage");
        passage.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Browsing_by_kind_lists_only_passages()
    {
        var book = await CreateBookAsync();
        var passage = await CreatePassageAsync(book.Id, LongText);
        var note = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new { content = "a thought" });
        note.StatusCode.Should().Be(HttpStatusCode.Created);

        var passages = await Client.GetFromJsonAsync<NoteSearchPageDto>($"/api/notes?bookId={book.Id}&kind=passage");
        passages!.TotalCount.Should().Be(1);
        passages.Items.Single().Id.Should().Be(passage.Id);

        var all = await Client.GetFromJsonAsync<NoteSearchPageDto>($"/api/notes?bookId={book.Id}");
        all!.TotalCount.Should().Be(2);

        var unknown = await Client.GetFromJsonAsync<NoteSearchPageDto>($"/api/notes?bookId={book.Id}&kind=chapter");
        unknown!.TotalCount.Should().Be(0);
    }

    [Fact]
    public void Sanitising_keeps_reading_structure_and_drops_everything_else()
    {
        PassageText.Sanitize(
                "<div class=\"x\"><h2 id=\"c5\">V</h2><p lang=\"ru\" style=\"color:red\">a<br/>b</p>" +
                "<!-- c --><a href=\"javascript:x\">link</a><blockquote><p>q</p></blockquote>" +
                "<iframe src=\"x\"><p>gone</p></iframe><hr/></div>")
            .Should().Be("<div><h2>V</h2><p lang=\"ru\">a<br>b</p>link<blockquote><p>q</p></blockquote><hr></div>");
    }

    private async Task<NoteDto> CreatePassageAsync(Guid bookId, string text)
    {
        var response = await Client.PostAsJsonAsync($"/api/books/{bookId}/notes", new
        {
            content = "",
            kind = "passage",
            passageTitle = "The Grand Inquisitor",
            sourceAnchorKind = "epub_cfi",
            sourceAnchorValue = "epubcfi(/6/14!/4/2/1:0)",
            passageEnd = "epubcfi(/6/16!/4/88/1:120)",
            selectedText = text,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<NoteDto>())!;
    }

    private async Task<BookDto> CreateBookAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Passage Book {Guid.NewGuid():N}",
            author = "Fyodor Dostoevsky",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }
}

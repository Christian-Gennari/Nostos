using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Pins the wire format of every error response the refactored endpoint groups
/// return: status, <c>Content-Type</c> and the exact body, byte for byte (apart
/// from the per-request <c>traceId</c> Problem Details adds).
///
/// The point is not to bless the formats as good. Nostos-Cloud and the Angular
/// client parse them, and issue #1632 explicitly forbids changing them while
/// the endpoint layer learns to share one helper that PRODUCES them. These
/// tests are that guarantee: they must pass untouched before and after the
/// refactor, and they fail the moment a body gains, loses or reorders a key.
///
/// The five shapes are all represented here, on purpose — a later decision to
/// unify them is the owner's, and it will have to update this file knowingly.
/// </summary>
public sealed class EndpointErrorWireFormatTests : IClassFixture<LibraryEndpointFactory>
{
    private const string Json = "application/json; charset=utf-8";
    private const string Problem = "application/problem+json";

    private readonly LibraryEndpointFactory _factory;

    public EndpointErrorWireFormatTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    // ---------------------------------------------------------------- library

    [Fact]
    public async Task Library_errors_are_problem_details_with_the_machine_code_in_the_title()
    {
        var unknown = await Client.GetAsync($"/api/books/{Guid.NewGuid()}");
        await AssertProblemAsync(
            unknown,
            HttpStatusCode.NotFound,
            """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"book_not_found","status":404,"detail":"Book not found."}
            """);

        var invalid = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = "   ",
        });
        await AssertProblemAsync(
            invalid,
            HttpStatusCode.BadRequest,
            """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"invalid_book_identity","status":400,"detail":"Title is required."}
            """);
    }

    [Fact]
    public async Task Isbn_lookup_problem_details_carry_a_human_title_and_a_code_extension()
    {
        var response = await Client.GetAsync("/api/books/lookup/not-a-real-isbn");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Invalid ISBN","status":400,"detail":"Enter a valid ISBN-10 or ISBN-13.","code":"invalid_isbn"}
            """);
    }

    // ----------------------------------------------------------------- books

    [Fact]
    public async Task Book_validation_failures_keep_their_bare_string_body()
    {
        var book = await CreateBookAsync();

        using (var form = MultipartWithField())
        {
            await AssertBodyAsync(
                await Client.PostAsync($"/api/books/{book.Id}/file", form),
                HttpStatusCode.BadRequest,
                Json,
                "\"Missing file.\"");
        }

        using (var form = MultipartWithField())
        {
            await AssertBodyAsync(
                await Client.PostAsync($"/api/books/{book.Id}/cover", form),
                HttpStatusCode.BadRequest,
                Json,
                "\"Missing cover file.\"");
        }

        using (var form = MultipartWithField())
        {
            await AssertBodyAsync(
                await Client.PostAsync($"/api/books/{book.Id}/tracks", form),
                HttpStatusCode.BadRequest,
                Json,
                "\"Only an audiobook can be made from several audio files.\"");
        }
    }

    [Fact]
    public async Task Book_upload_conflicts_keep_the_code_and_message_envelope()
    {
        var book = await CreateBookAsync();
        await UploadTextFileAsync(book.Id);

        using var form = MultipartWithFile("Another small text edition.", "text/plain", "copy.txt");
        await AssertBodyAsync(
            await Client.PostAsync($"/api/books/{book.Id}/file", form),
            HttpStatusCode.Conflict,
            Json,
            """
            {"code":"book_has_file","message":"This book already has a file. Pass ?replace=true to replace it."}
            """);
    }

    [Fact]
    public async Task Book_upload_failures_keep_the_error_and_message_envelope()
    {
        var book = await CreateBookAsync();

        using (var form = MultipartWithFile(
            "PK\u0003\u0004 not really a zip", "application/epub+zip", "truncated.epub"))
        {
            await AssertBodyAsync(
                await Client.PostAsync($"/api/books/{book.Id}/file", form),
                HttpStatusCode.BadRequest,
                Json,
                """
                {"error":"book_upload_invalid_file","message":"This file is not a valid EPUB. It may be damaged or incomplete."}
                """);
        }

        var emptySession = await Client.PostAsJsonAsync(
            $"/api/books/{book.Id}/file/uploads",
            new
            {
                totalBytes = 0,
                fileName = "empty.epub",
                contentType = "application/epub+zip",
                sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            });
        await AssertBodyAsync(
            emptySession,
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"book_upload_invalid_file","message":"This file is empty."}
            """);
    }

    [Fact]
    public async Task Book_import_rejections_keep_the_error_envelope()
    {
        using var form = MultipartWithField();
        await AssertBodyAsync(
            await Client.PostAsync("/api/books/imports/preview", form),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"Choose a Goodreads or StoryGraph export to import."}
            """);
    }

    // ----------------------------------------------------------------- notes

    [Fact]
    public async Task Note_rejections_keep_the_error_envelope()
    {
        await AssertBodyAsync(
            await Client.GetAsync("/api/notes?order=bogus"),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"order is newest, oldest or book."}
            """);

        await AssertBodyAsync(
            await Client.GetAsync(
                "/api/notes/by-ids?ids=" + string.Join(",", Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()))),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"At most 200 ids."}
            """);

        await AssertBodyAsync(
            await Client.PostAsync("/api/notes/import/koreader", new StringContent("not a form")),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"Expected multipart/form-data with a KOReader metadata.lua file."}
            """);

        var unknownBook = Guid.NewGuid();
        await AssertBodyAsync(
            await Client.PostAsJsonAsync($"/api/books/{unknownBook}/notes", new CreateNoteDto("words")),
            HttpStatusCode.NotFound,
            Json,
            $$"""
            {"error":"Book {{unknownBook}} not found."}
            """);

        await AssertBodyAsync(
            await Client.PostAsJsonAsync(
                $"/api/notes/{Guid.NewGuid()}/reprocess",
                new { processingMode = "clarify" }),
            HttpStatusCode.NotFound,
            Json,
            """
            {"error":"Note not found."}
            """);
    }

    // ---------------------------------------------------------------- topics

    [Fact]
    public async Task Topic_rejections_keep_the_error_envelope()
    {
        var name = $"Wire Topic {Guid.NewGuid():N}";
        var book = await CreateBookAsync();
        await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new CreateNoteDto($"[[{name}]] words"));

        var topic = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!
            .Single(candidate => candidate.Name == name);

        await AssertBodyAsync(
            await Client.PutAsJsonAsync($"/api/topics/{topic.Id}", new UpdateTopicDto("   ")),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"Topic name cannot be empty."}
            """);

        await AssertBodyAsync(
            await Client.PostAsJsonAsync($"/api/topics/{topic.Id}/merge", new MergeTopicDto(topic.Id)),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"A topic cannot be merged into itself."}
            """);

        await AssertBodyAsync(
            await Client.PutAsJsonAsync(
                $"/api/topics/{topic.Id}/description",
                new UpdateTopicDescriptionDto(new string('x', 4001))),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"A topic's description is at most 4000 characters."}
            """);
    }

    // -------------------------------------------------------------- writings

    [Fact]
    public async Task Writing_rejections_keep_their_bare_string_bodies()
    {
        await AssertBodyAsync(
            await Client.PostAsJsonAsync("/api/writings", new { name = "x", type = "Bogus" }),
            HttpStatusCode.BadRequest,
            Json,
            "\"Invalid type. Must be 'Folder' or 'Document'.\"");

        var root = await CreateWritingAsync("Wire Root", type: "Folder");
        var child = await CreateWritingAsync("Wire Child", type: "Folder", parentId: root.Id);

        await AssertBodyAsync(
            await Client.PutAsJsonAsync($"/api/writings/{root.Id}/move", new MoveWritingDto(root.Id)),
            HttpStatusCode.BadRequest,
            Json,
            "\"Cannot move a folder into itself.\"");

        await AssertBodyAsync(
            await Client.PutAsJsonAsync($"/api/writings/{root.Id}/move", new MoveWritingDto(child.Id)),
            HttpStatusCode.BadRequest,
            Json,
            "\"Cannot move a folder into its own descendant.\"");

        await AssertBodyAsync(
            await Client.PutAsJsonAsync(
                $"/api/writings/{root.Id}/page-setup",
                new WritingPageSetupDto("tabloid", 25, "newsreader", 1.5, "apa")),
            HttpStatusCode.BadRequest,
            Json,
            "\"Paper must be 'a4' or 'letter'.\"");

        await AssertBodyAsync(
            await Client.PostAsJsonAsync(
                $"/api/writings/{root.Id}/notes",
                new AddWritingSourceDto(Guid.NewGuid())),
            HttpStatusCode.BadRequest,
            Json,
            "\"Cannot keep notes on a folder.\"");
    }

    // ---------------------------------------------------------- reading list

    [Fact]
    public async Task Reading_list_rejections_are_problem_details_with_the_code_in_both_places()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/reading-list",
            new SaveReadingListEntryDto("   "));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"invalid_reading_list_entry","status":400,"detail":"A book needs a title.","code":"invalid_reading_list_entry"}
            """);
    }

    // --------------------------------------------------------------- calibre

    [Fact]
    public async Task Calibre_guard_keeps_the_code_and_message_envelope()
    {
        await AssertBodyAsync(
            await Client.GetAsync("/api/calibre/preview"),
            HttpStatusCode.Conflict,
            Json,
            """
            {"code":"calibre_not_configured","message":"No Calibre library is set up yet. Upload a zipped Calibre library to begin."}
            """);
    }

    // -------------------------------------------------------------- providers

    [Fact]
    public async Task Provider_rejections_keep_their_problem_details_and_error_envelope()
    {
        await AssertProblemAsync(
            await Client.GetAsync("/api/providers/nope/search?query=classic"),
            HttpStatusCode.NotFound,
            """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"provider_unknown","status":404,"detail":"No content provider is registered as 'nope'."}
            """);

        await AssertBodyAsync(
            await Client.GetAsync(
                "/api/providers/gutenberg/search?query=" + new string('a', 201)),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"Search text is limited to 200 characters."}
            """);
    }

    [Fact]
    public async Task Provider_settings_rejection_is_problem_details()
    {
        await AssertProblemAsync(
            await Client.PutAsJsonAsync("/api/settings/providers/gutenberg", new { }),
            HttpStatusCode.BadRequest,
            """
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"invalid_provider_preference","status":400,"detail":"Provide the 'enabled' value for this provider."}
            """);
    }

    [Fact]
    public async Task Ai_provider_settings_validation_keeps_the_error_envelope()
    {
        await AssertBodyAsync(
            await Client.PutAsJsonAsync(
                "/api/settings/ai-provider",
                new { llm = new { baseUrl = "not-a-url" } }),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"baseUrl must be an absolute http:// or https:// URL."}
            """);
    }

    // ------------------------------------------------------------- portability

    [Fact]
    public async Task Empty_portable_archive_keeps_the_error_and_message_envelope()
    {
        using var content = new StreamContent(new MemoryStream());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        await AssertBodyAsync(
            await Client.PostAsync("/api/portability/import", content),
            HttpStatusCode.BadRequest,
            Json,
            """
            {"error":"empty_archive","message":"Portable archive request body is empty."}
            """);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// Compares the body exactly, except for the <c>traceId</c> Problem Details
    /// stamps per request; that it is present is asserted separately, so removing
    /// it from the comparison cannot hide a missing one.
    /// </summary>
    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string expectedBody)
    {
        var body = await AssertBodyAsync(response, status, Problem, expectedBody);
        Regex.IsMatch(body, "\"traceId\":\"[^\"]+\"").Should()
            .BeTrue("every Problem Details body carries a traceId, got: " + body);
    }

    private static async Task<string> AssertBodyAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string contentType,
        string expectedBody)
    {
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.ToString().Should().Be(contentType);

        // One leading/trailing newline is a raw-string delimiter, never part of
        // the body: the expected values above are single-line JSON.
        var actual = Regex.Replace(body.Trim(), ",\"traceId\":\"[^\"]*\"", "");
        actual.Should().Be(expectedBody.Trim());

        return body;
    }

    private static MultipartFormDataContent MultipartWithField()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("x"), "note");
        return form;
    }

    private static MultipartFormDataContent MultipartWithFile(
        string content,
        string contentType,
        string fileName)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return form;
    }

    private async Task<BookDto> CreateBookAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Wire {Guid.NewGuid():N}",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private async Task UploadTextFileAsync(Guid bookId)
    {
        using var form = MultipartWithFile(
            "A small text edition for the wire-format contract.",
            "text/plain",
            "copy.txt");
        (await Client.PostAsync($"/api/books/{bookId}/file", form)).EnsureSuccessStatusCode();
    }

    private async Task<WritingDto> CreateWritingAsync(
        string name,
        string type = "Document",
        Guid? parentId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/writings", new
        {
            name = $"{name} {Guid.NewGuid():N}",
            type,
            parentId,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WritingDto>())!;
    }
}

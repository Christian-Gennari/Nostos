using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nostos.Backend.Data;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Mcp;

/// <summary>
/// The MCP wire contract, frozen. A connected app sees the tool names, titles,
/// descriptions, argument schemas, annotations, server instructions and result
/// shapes in the checked-in files under <c>Mcp/Expected/</c>; this test fails on
/// any byte that differs. It is a characterization test: it was captured from
/// the server before the single MCP tool class was split by feature
/// (issue #1599), and it is what proves the split changed no caller's view.
///
/// To regenerate deliberately (only when a contract change is intended), run
/// with <c>NOSTOS_UPDATE_MCP_SNAPSHOTS=1</c> and copy the file named in the
/// failure message from the test output directory into <c>Mcp/Expected/</c>.
/// </summary>
public sealed class McpToolContractTests : IAsyncLifetime
{
    private const string UpdateVariable = "NOSTOS_UPDATE_MCP_SNAPSHOTS";

    private readonly McpEndpointFactory _factory = new();
    private HttpClient _http = null!;
    private McpClient _mcp = null!;

    private Guid _mobyId;
    private Guid _waldenId;
    private Guid _quoteNoteId;
    private Guid _folderId;
    private Guid _draftId;
    private string _topicId = string.Empty;

    public async Task InitializeAsync()
    {
        _http = _factory.CreateClient();
        (await _http.GetAsync("/health/live")).EnsureSuccessStatusCode();
        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", McpEndpointFactory.Token);
        _mcp = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(authed.BaseAddress!, NostosMcp.Path),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                authed,
                ownsHttpClient: true),
            new McpClientOptions
            {
                InitializationTimeout = TimeSpan.FromMinutes(3),
                DiscoverProbeTimeout = TimeSpan.FromMinutes(1),
            });
    }

    public async Task DisposeAsync()
    {
        await _mcp.DisposeAsync();
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task The_tools_and_server_instructions_match_the_checked_in_contract()
    {
        var instructions = _mcp.ServerInstructions;
        var tools = (await _mcp.ListToolsAsync())
            .Select(tool => new
            {
                name = tool.Name,
                title = tool.ProtocolTool.Title,
                description = tool.ProtocolTool.Description,
                annotations = tool.ProtocolTool.Annotations,
                inputSchema = tool.ProtocolTool.InputSchema,
            })
            .OrderBy(tool => tool.name, StringComparer.Ordinal)
            .ToList();

        var actual = McpSnapshots.Canonical(new { instructions, tools });
        var expected = McpSnapshots.Compare("tool-contract.json", actual);
        expected.Should().Be(actual,
            "the MCP tool list and instructions are the wire contract every connected app depends on");
    }

    [Fact]
    public async Task Every_tool_returns_the_checked_in_result_shape()
    {
        await SeedAsync();
        var shapes = await CaptureEveryToolAsync();

        var actual = McpSnapshots.Canonical(shapes);
        var expected = McpSnapshots.Compare("result-shapes.json", actual);
        expected.Should().Be(actual, "each tool's JSON shape is what a connected app reads");
    }

    // ------------------------------------------------------------------
    // Seeded library
    // ------------------------------------------------------------------

    private async Task SeedAsync()
    {
        _mobyId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851", "9780142437247");
        _quoteNoteId = await CreateNoteAsync(_mobyId, new CreateNoteDto(
            Content: "Ishmael's restlessness",
            SelectedText: "Call me Ishmael.",
            SourceAnchorKind: "physical_page",
            SourceAnchorValue: "3",
            Topics: ["Whiteness"]));
        await CreateNoteAsync(_mobyId, new CreateNoteDto(
            Content: "A book about the sea and what it refuses to say.",
            Topics: ["Whiteness"]));

        // Walden is an ebook with exactly one indexed passage, so search and
        // find_quote answer from real book text rather than a paper book's
        // empty index. The row and the index are seeded directly: the pipeline
        // that normally produces them needs a real file and an extraction run,
        // neither of which this contract test is about.
        _waldenId = await CreateBookAsync("Walden", "Henry David Thoreau", "1854", null, "ebook");
        await SeedBookTextAsync(_waldenId);

        var folder = await _http.PostAsJsonAsync("/api/writings", new CreateWritingDto("Essays", "Folder", null));
        folder.EnsureSuccessStatusCode();
        _folderId = (await folder.Content.ReadFromJsonAsync<WritingDto>())!.Id;
        var draft = await _http.PostAsJsonAsync("/api/writings", new CreateWritingDto("On simplicity", "Document", _folderId));
        draft.EnsureSuccessStatusCode();
        _draftId = (await draft.Content.ReadFromJsonAsync<WritingDto>())!.Id;
        var content = await _http.PutAsJsonAsync($"/api/writings/{_draftId}", new UpdateWritingDto(
            "On simplicity",
            $"# On simplicity\n\nThoreau's advice: [Simplify, simplify.](nostos:cite/{_waldenId:D}?as=quote&src={_quoteNoteId:D})\n"));
        content.EnsureSuccessStatusCode();

        var listed = await CallAsync("list_topics", new Dictionary<string, object?>());
        _topicId = listed.GetProperty("topics")[0].GetProperty("id").GetString()!;
    }

    private async Task<Guid> CreateBookAsync(string title, string author, string publishedDate, string? isbn, string type = "physical")
    {
        var response = await _http.PostAsJsonAsync("/api/books", new { type, title, author, publishedDate, isbn });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateNoteAsync(Guid bookId, CreateNoteDto note)
    {
        var response = await _http.PostAsJsonAsync($"/api/books/{bookId}/notes", note);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NoteDto>())!.Id;
    }

    private async Task SeedBookTextAsync(Guid bookId)
    {
        using var scope = _factory.Services.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<IBookTextIndex>();
        await index.EnsureSchemaAsync();

        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bookId.ToString("D"))));
        const string text =
            "Simplify, simplify. Instead of three meals a day, if it be necessary eat but one; " +
            "instead of a hundred dishes, five; and reduce other things in proportion. " +
            "Our life is frittered away by detail.";
        const string version = BookTextArtifactSchema.CurrentExtractorVersion;

        await index.ScheduleAsync(bookId, "walden.epub", BookTextSourceFormat.Epub);
        var work = await index.TryClaimNextAsync(TimeSpan.FromMinutes(5));
        work.Should().NotBeNull();
        var revision = new BookTextSourceRevision(bookId, sha, version, BookTextSourceFormat.Epub);
        var chunk = new BookTextIndexedChunk(
            BookTextIdentity.ChunkId(revision, 0),
            bookId,
            sha,
            version,
            BookTextSourceFormat.Epub,
            0,
            text,
            ["Economy"],
            [new BookTextSourceSegment(0, text.Length, new EpubBookTextSourceLocator(0, "chapter-1.xhtml"))]);
        (await index.ReplaceReadyAsync(revision, [chunk], text.Length, work!.Attempt)).Should().BeTrue();

        // A book text passage is citable only for a book that has a file.
        await using var db = await Contexts().CreateDbContextAsync();
        var book = await db.Books.SingleAsync(b => b.Id == bookId);
        book.FileDetails.HasFile = true;
        book.FileDetails.FileName = "walden.epub";
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------
    // Every tool, in a deterministic order
    // ------------------------------------------------------------------

    private async Task<JsonObject> CaptureEveryToolAsync()
    {
        var shapes = new JsonObject();

        void Add(string label, JsonElement result) => shapes[label] = McpSnapshots.Shape(result);

        // search, once per kind so every hit shape is in the contract.
        Add("search(notes)", await CallAsync("search", new() { ["query"] = "Ishmael", ["kind"] = "notes" }));
        Add("search(topics)", await CallAsync("search", new() { ["query"] = "whiteness", ["kind"] = "topics" }));
        var bookText = await CallAsync("search", new() { ["query"] = "Simplify", ["kind"] = "book_text" });
        Add("search(book_text)", bookText);
        Add("search(all)", await CallAsync("search", new() { ["query"] = "Simplify" }));

        var textId = bookText.GetProperty("results")[0].GetProperty("id").GetString()!;

        // fetch, once per evidence kind.
        Add("fetch(note)", await CallAsync("fetch", new() { ["id"] = $"note:{_quoteNoteId:D}" }));
        Add("fetch(topic)", await CallAsync("fetch", new() { ["id"] = _topicId }));
        Add("fetch(book_text)", await CallAsync("fetch", new() { ["id"] = textId }));

        var found = await CallAsync("find_quote", new()
        {
            ["bookId"] = _waldenId,
            ["startsWith"] = "Simplify",
            ["endsWith"] = "proportion",
        });
        Add("find_quote", found);

        Add("overview", await CallAsync("overview", new()));

        // list_books: the shelf read, then the ISBN branch.
        Add("list_books(search)", await CallAsync("list_books", new() { ["search"] = "Moby" }));
        Add("list_books(isbn)", await CallAsync("list_books", new() { ["isbn"] = "9780142437247" }));
        Add("get_book", await CallAsync("get_book", new() { ["bookId"] = _mobyId }));
        Add("list_notes", await CallAsync("list_notes", new() { ["bookId"] = _mobyId }));
        Add("list_topics", await CallAsync("list_topics", new()));
        Add("list_topics(search)", await CallAsync("list_topics", new() { ["search"] = "Ishmael" }));
        Add("get_topic", await CallAsync("get_topic", new() { ["topicId"] = _topicId }));

        Add("list_drafts", await CallAsync("list_drafts", new() { ["query"] = "simplicity" }));
        Add("list_drafts(folders)", await CallAsync("list_drafts", new() { ["type"] = "Folder" }));
        Add("read_draft", await CallAsync("read_draft", new() { ["writingId"] = _draftId }));

        Add("add_to_reading_list", await CallAsync("add_to_reading_list", new()
        {
            ["title"] = "Austerlitz",
            ["author"] = "W. G. Sebald",
            ["why"] = "Mara said it is the strangest of the four.",
            ["readNext"] = true,
        }));
        Add("add_to_reading_list(again)", await CallAsync("add_to_reading_list", new()
        {
            ["title"] = "Austerlitz",
            ["author"] = "W. G. Sebald",
        }));
        Add("reading_list", await CallAsync("reading_list", new()));

        Add("save_note", await CallAsync("save_note", new()
        {
            ["bookId"] = _mobyId,
            ["thought"] = "Seeing is a kind of work.",
            ["quote"] = "Call me Ishmael.",
            ["topics"] = new[] { "Whiteness" },
        }));

        // save_passage answers with the same McpSavedNote record as save_note
        // (its shape is covered above). It is not called here: keeping a
        // passage needs the book's real EPUB file, which the passage anchors
        // read to place both ends of the quote, and this test seeds an index,
        // not a publication.

        var draft = new Dictionary<string, object?>
        {
            ["name"] = "On detail",
            ["content"] = "# On detail\n\nA draft the contract test writes.\n",
        };
        Add("create_draft", await CallAsync("create_draft", draft));
        Add("create_draft(replay)", await CallAsync("create_draft", draft));

        Add("add_book", await CallAsync("add_book", new()
        {
            ["title"] = "The Rings of Saturn",
            ["author"] = "W. G. Sebald",
            ["publishedDate"] = "1995",
        }));
        Add("add_book(matched)", await CallAsync("add_book", new()
        {
            ["title"] = "The Rings of Saturn",
            ["author"] = "W. G. Sebald",
        }));
        Add("add_book(choose)", await CallAsync("add_book", new()
        {
            ["title"] = "Moby-Dick",
        }));

        Add("update_book", await CallAsync("update_book", new()
        {
            ["bookId"] = _mobyId,
            ["description"] = "A novel about a whale.",
        }));
        Add("update_book(kept)", await CallAsync("update_book", new()
        {
            ["bookId"] = _mobyId,
            ["description"] = "Another description.",
        }));

        return shapes;
    }

    // ------------------------------------------------------------------

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _mcp.CallToolAsync(tool, args);
        result.IsError.Should().NotBe(true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private IDbContextFactory<NostosDbContext> Contexts() =>
        _factory.Services.GetRequiredService<IDbContextFactory<NostosDbContext>>();

    // ------------------------------------------------------------------
    // Snapshot plumbing
    // ------------------------------------------------------------------

    private static class McpSnapshots
    {
        private static readonly JsonSerializerOptions Pretty = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Stable text for a value: property order kept, nothing locale-dependent.</summary>
        public static string Canonical(object value) =>
            JsonSerializer.Serialize(value, Pretty) + "\n";

        /// <summary>
        /// The checked-in file's text, or the regenerated text when the update
        /// variable is set. A difference is reported by the caller as a failing
        /// assertion, with this message naming the file to copy.
        /// </summary>
        public static string Compare(string fileName, string actual)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Mcp", "Expected", fileName);
            if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, actual);
                return actual;
            }

            File.Exists(path).Should().BeTrue(
                $"the contract file '{fileName}' must be checked in under Nostos.Backend.Tests/Mcp/Expected; regenerate with {UpdateVariable}=1");
            return File.ReadAllText(path);
        }

        /// <summary>
        /// A JSON value's shape: the property names in order, and the kind of
        /// every value, with one element standing for a list. Ids, timestamps and
        /// prose change between runs, but the shape a client reads must not.
        /// </summary>
        public static JsonNode? Shape(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => new JsonObject(element.EnumerateObject()
                .Select(p => KeyValuePair.Create(p.Name, Shape(p.Value)))),
            JsonValueKind.Array => element.GetArrayLength() == 0
                ? new JsonArray()
                : new JsonArray(Shape(element[0])),
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "undefined",
        };
    }
}

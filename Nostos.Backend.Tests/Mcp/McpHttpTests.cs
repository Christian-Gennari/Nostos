using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nostos.Backend.Data;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Mcp;

/// <summary>
/// A SelfHosted host with MCP switched on the way a deployment does it: an
/// authentication scheme and the <see cref="NostosMcp.AuthorizationPolicy"/>
/// policy. The scheme here accepts one fixed bearer token.
/// </summary>
public sealed class McpEndpointFactory : LibraryEndpointFactory
{
    public const string Token = "test-mcp-token";
    private const string SchemeName = "TestMcp";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TokenHandler>(SchemeName, _ => { });
            services.AddAuthorization(options => options.AddPolicy(
                NostosMcp.AuthorizationPolicy,
                policy => policy.AddAuthenticationSchemes(SchemeName).RequireAuthenticatedUser()));
        });
    }

    private sealed class TokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (header != $"Bearer {Token}")
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "agent")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

public sealed class McpHttpTests : IAsyncLifetime
{
    private readonly McpEndpointFactory _factory = new();
    private HttpClient _http = null!;
    private McpClient _mcp = null!;

    public async Task InitializeAsync()
    {
        _http = _factory.CreateClient();
        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", McpEndpointFactory.Token);
        _mcp = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(authed.BaseAddress!, NostosMcp.Path),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            authed,
            ownsHttpClient: true));
    }

    public async Task DisposeAsync()
    {
        await _mcp.DisposeAsync();
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_token_the_endpoint_refuses_the_call()
    {
        var response = await _http.PostAsJsonAsync(NostosMcp.Path, new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_host_that_registers_no_policy_never_maps_mcp_and_never_serves_the_app_shell_there()
    {
        await using var plain = new LibraryEndpointFactory();
        var client = plain.CreateClient();

        var post = await client.PostAsJsonAsync(NostosMcp.Path, new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        var get = await client.GetAsync(NostosMcp.Path);

        post.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await get.Content.ReadAsStringAsync()).Should().NotContain("<app-root");
    }

    [Fact]
    public async Task It_lists_the_v1_tools_with_honest_annotations_and_the_citation_rules()
    {
        var tools = await _mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            "search", "fetch", "find_quote", "overview", "list_books", "get_book", "list_notes", "list_topics", "save_note");
        foreach (var tool in tools.Where(t => t.Name != "save_note"))
            tool.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue(tool.Name);
        var save = tools.Single(t => t.Name == "save_note").ProtocolTool.Annotations!;
        save.ReadOnlyHint.Should().BeFalse();
        save.DestructiveHint.Should().BeFalse();
        _mcp.ServerInstructions.Should().Contain("Quote only words a Nostos tool returned");
    }

    [Fact]
    public async Task Search_finds_a_kept_quote_and_cites_it_in_the_Studios_own_format()
    {
        var bookId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto(
            Content: "Ishmael's restlessness",
            SelectedText: "Call me Ishmael.",
            SourceAnchorKind: "physical_page",
            SourceAnchorValue: "3"));

        var result = await CallAsync("search", new() { ["query"] = "Ishmael" });

        var hit = result.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == $"note:{noteId:D}");
        hit.GetProperty("kind").GetString().Should().Be("note");
        hit.GetProperty("url").GetString().Should().EndWith($"/brain/notes/{noteId:D}").And.StartWith("http");
        var citation = hit.GetProperty("citation");
        citation.GetProperty("book").GetString().Should().Be("Moby-Dick");
        citation.GetProperty("author").GetString().Should().Be("Herman Melville");
        citation.GetProperty("year").GetString().Should().Be("1851");
        citation.GetProperty("location").GetString().Should().Be("p. 3");
        citation.GetProperty("link").GetString().Should().Be($"nostos:cite/{bookId:D}?p=3&as=quote&src={noteId:D}");
    }

    [Fact]
    public async Task Fetch_reads_a_search_result_in_full()
    {
        var bookId = await CreateBookAsync("Walden", "Henry David Thoreau", "1854");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto(
            Content: "Simplicity as a discipline, not a mood.",
            SelectedText: "Simplify, simplify."));

        var document = await CallAsync("fetch", new() { ["id"] = $"note:{noteId:D}" });

        document.GetProperty("kind").GetString().Should().Be("note");
        document.GetProperty("text").GetString().Should().Contain("Simplify, simplify.").And.Contain("Simplicity as a discipline");
        document.GetProperty("citation").GetProperty("markdown").GetString()
            .Should().Be($"[Simplify, simplify.](nostos:cite/{bookId:D}?as=quote&src={noteId:D})");
    }

    [Fact]
    public async Task Books_are_found_by_title_and_read_with_their_year()
    {
        var bookId = await CreateBookAsync("The Rings of Saturn", "W. G. Sebald", "1995-01-01");

        var list = await CallAsync("list_books", new() { ["search"] = "Saturn" });
        var book = await CallAsync("get_book", new() { ["bookId"] = bookId });

        list.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).Should().Contain(bookId);
        book.GetProperty("year").GetString().Should().Be("1995");
        book.GetProperty("citation").GetProperty("link").GetString().Should().Be($"nostos:cite/{bookId:D}?as=reference");
    }

    [Fact]
    public async Task A_books_notes_come_in_page_order()
    {
        var bookId = await CreateBookAsync("Middlemarch", "George Eliot", "1871");
        var later = await CreateNoteAsync(bookId, new CreateNoteDto("Later", SourceAnchorKind: "physical_page", SourceAnchorValue: "200"));
        var earlier = await CreateNoteAsync(bookId, new CreateNoteDto("Earlier", SourceAnchorKind: "physical_page", SourceAnchorValue: "12"));

        var notes = await CallAsync("list_notes", new() { ["bookId"] = bookId });

        notes.GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("id").GetString())
            .Should().ContainInOrder($"note:{earlier:D}", $"note:{later:D}");
        notes.GetProperty("total").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Save_note_keeps_one_note_for_a_resent_call_marks_it_as_mcp_and_moves_the_change_feed()
    {
        var bookId = await CreateBookAsync("Pilgrim at Tinker Creek", "Annie Dillard", "1974");
        var before = await StateVersionAsync();
        var args = new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["thought"] = "Seeing is a kind of work.",
            ["quote"] = "The secret of seeing is, then, the pearl of great price.",
        };

        var first = await CallAsync("save_note", args);
        var again = await CallAsync("save_note", args);

        again.GetProperty("id").GetString().Should().Be(first.GetProperty("id").GetString());
        await using var db = await Contexts().CreateDbContextAsync();
        var saved = await db.Notes.Where(n => n.BookId == bookId).ToListAsync();
        saved.Should().ContainSingle();
        saved[0].CaptureSource.Should().Be(NostosMcp.CaptureSource);
        saved[0].SelectedText.Should().Be("The secret of seeing is, then, the pearl of great price.");
        saved[0].Content.Should().Be("Seeing is a kind of work.");
        (await StateVersionAsync()).Should().NotBe(before);
        first.GetProperty("citation").GetProperty("link").GetString()
            .Should().Be($"nostos:cite/{bookId:D}?as=quote&src={saved[0].Id:D}");
    }

    [Fact]
    public async Task Topics_are_listed_by_use_found_by_their_notes_and_read_through_fetch()
    {
        var bookId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto("The whiteness of the whale terrifies.", Topics: ["Whiteness"]));

        var listed = await CallAsync("list_topics", new());
        var found = await CallAsync("list_topics", new() { ["search"] = "terrifies" });
        var topicId = listed.GetProperty("topics").EnumerateArray().Single().GetProperty("id").GetString()!;
        var topic = await CallAsync("fetch", new() { ["id"] = topicId });

        listed.GetProperty("topics")[0].GetProperty("name").GetString().Should().Be("Whiteness");
        found.GetProperty("topics").EnumerateArray().Select(t => t.GetProperty("id").GetString()).Should().Contain(topicId);
        topic.GetProperty("kind").GetString().Should().Be("topic");
        topic.GetProperty("text").GetString().Should().Contain($"note:{noteId:D}");
    }

    [Fact]
    public async Task The_overview_counts_the_brain_and_lists_the_library()
    {
        var bookId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851");
        await CreateNoteAsync(bookId, new CreateNoteDto("One"));
        await CreateNoteAsync(bookId, new CreateNoteDto("Two"));

        var overview = await CallAsync("overview", new());

        overview.GetProperty("brain").GetProperty("notes").GetInt32().Should().Be(2);
        overview.GetProperty("brain").GetProperty("booksWithMostNotes")[0].GetProperty("id").GetString().Should().Be(bookId.ToString("D"));
        overview.GetProperty("library").GetRawText().Should().Contain("Moby-Dick");
    }

    [Fact]
    public async Task A_quote_saved_over_mcp_can_be_found_and_drawn_in_the_reader_like_one_Ask_Nostos_saved()
    {
        var bookId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851");
        await CallAsync("save_note", new() { ["bookId"] = bookId, ["quote"] = "Call me Ishmael." });

        await using var db = await Contexts().CreateDbContextAsync();
        var saved = await db.Notes.SingleAsync(n => n.BookId == bookId);
        Nostos.Backend.Services.Notes.FoundNoteLocation.IsAskNostosQuote(saved).Should().BeTrue();
    }

    [Fact]
    public async Task Tool_errors_come_back_as_errors_the_model_can_read()
    {
        var missingBook = await _mcp.CallToolAsync("save_note", new Dictionary<string, object?>
        {
            ["bookId"] = Guid.NewGuid(),
            ["thought"] = "A thought for no book.",
        });
        var badId = await _mcp.CallToolAsync("fetch", new Dictionary<string, object?> { ["id"] = "nonsense" });
        var empty = await _mcp.CallToolAsync("save_note", new Dictionary<string, object?> { ["bookId"] = Guid.NewGuid() });

        missingBook.IsError.Should().BeTrue();
        badId.IsError.Should().BeTrue();
        Text(badId).Should().Contain("not a Nostos result id");
        empty.IsError.Should().BeTrue();
        Text(empty).Should().Contain("thought");
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

    private async Task<Guid> CreateBookAsync(string title, string author, string publishedDate)
    {
        var response = await _http.PostAsJsonAsync("/api/books", new { type = "physical", title, author, publishedDate });
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

    private IDbContextFactory<NostosDbContext> Contexts() =>
        _factory.Services.GetRequiredService<IDbContextFactory<NostosDbContext>>();

    private async Task<string> StateVersionAsync()
    {
        await using var db = await Contexts().CreateDbContextAsync();
        var state = await db.Set<Nostos.Backend.Data.Models.LibraryState>().AsNoTracking().SingleOrDefaultAsync();
        return state?.StateVersion ?? string.Empty;
    }
}

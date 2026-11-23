using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nostos.Backend.Data;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
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

    /// <summary>A token whose caller was granted <c>nostos:read</c> only.</summary>
    public const string ReadOnlyToken = "test-mcp-read-only-token";
    private const string SchemeName = "TestMcp";

    /// <summary>
    /// Answers the provider-content client, so a cover URL is a fake HTTP
    /// exchange in every MCP test and never the network.
    /// </summary>
    public StubProviderContentHandler ProviderContent { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TokenHandler>(SchemeName, _ => { });
            services.AddSingleton<IMcpRequestGate, RefuseOnHeaderGate>();
            services.AddAuthorization(options => options.AddPolicy(
                NostosMcp.AuthorizationPolicy,
                policy => policy.AddAuthenticationSchemes(SchemeName).RequireAuthenticatedUser()));
            services.AddHttpClient(ProviderContentDownloader.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => ProviderContent);
        });
    }

    public sealed class StubProviderContentHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        /// <summary>What the next request gets; 404 unless a test says otherwise.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request));
        }
    }

    /// <summary>Stands in for a host gate (Cloud's revoked OAuth clients): refuses a marked request.</summary>
    public const string RefuseHeader = "X-Test-Refuse";

    private sealed class RefuseOnHeaderGate : IMcpRequestGate
    {
        public ValueTask<bool> AllowAsync(ClaimsPrincipal user, HttpContext http, CancellationToken cancellationToken) =>
            ValueTask.FromResult(user.Identity?.IsAuthenticated == true && !http.Request.Headers.ContainsKey(RefuseHeader));
    }

    private sealed class TokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            var scope = header == $"Bearer {Token}" ? $"{NostosMcp.ReadScope} {NostosMcp.WriteScope}"
                : header == $"Bearer {ReadOnlyToken}" ? NostosMcp.ReadScope
                : null;
            if (scope is null)
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "agent"),
                    new Claim(NostosMcp.ScopeClaimType, scope),
                ],
                SchemeName);
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
        // Start the host before the handshake. In a full parallel test run the
        // machine is busy, so the client also waits longer than a real one.
        (await _http.GetAsync("/health/live")).EnsureSuccessStatusCode();
        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", McpEndpointFactory.Token);
        _mcp = await McpClient.CreateAsync(new HttpClientTransport(
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
    public async Task Without_a_token_the_endpoint_refuses_the_call()
    {
        var response = await _http.PostAsJsonAsync(NostosMcp.Path, new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void A_host_that_registers_no_policy_never_maps_mcp()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Services.AddNostosMcp();
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        app.MapNostosMcp().Should().BeFalse();
    }

    [Fact]
    public async Task A_host_gate_refuses_an_authenticated_request_before_the_server_sees_it()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, NostosMcp.Path)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", McpEndpointFactory.Token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add(McpEndpointFactory.RefuseHeader, "1");

        var refused = await _http.SendAsync(request);
        var tools = await _mcp.ListToolsAsync();

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refused.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        tools.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_app_shell_is_never_served_on_mcp()
    {
        var get = await _http.GetAsync(NostosMcp.Path);

        get.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        (await get.Content.ReadAsStringAsync()).Should().NotContain("<app-root");
    }

    [Fact]
    public async Task It_lists_the_v1_tools_with_honest_annotations_and_the_citation_rules()
    {
        var tools = await _mcp.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo(
            "search", "fetch", "find_quote", "overview", "list_books", "get_book", "list_notes", "list_topics",
            "get_topic", "list_topic_kinds", "list_drafts", "read_draft", "reading_list",
            "save_note", "save_passage", "update_note", "update_passage", "delete_note", "delete_passage",
            "create_draft", "add_to_reading_list", "add_book", "update_book",
            "set_topic_kinds", "create_topic_kind");
        string[] writes = ["save_note", "save_passage", "update_note", "update_passage", "delete_note", "delete_passage", "create_draft", "add_to_reading_list", "add_book", "update_book", "set_topic_kinds", "create_topic_kind"];
        foreach (var tool in tools.Where(t => !writes.Contains(t.Name)))
            tool.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue(tool.Name);
        foreach (var tool in tools.Where(t => writes.Contains(t.Name)))
        {
            tool.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeFalse(tool.Name);
            // update_book can replace a detail (with replace: true), the note
            // and passage edits replace words or topics, and the deletes remove
            // what the user kept, so they all say so.
            tool.ProtocolTool.Annotations!.DestructiveHint.Should().Be(
                tool.Name is "update_book" or "set_topic_kinds" or "update_note" or "update_passage" or "delete_note" or "delete_passage",
                tool.Name);
        }
        foreach (var tool in tools.Where(t => t.Name is "add_book" or "update_book"))
            tool.ProtocolTool.Annotations!.OpenWorldHint.Should().BeTrue(
                $"{tool.Name} may fetch a cover image from an https URL the client gives it");
        _mcp.ServerInstructions.Should().Contain("Quote only words a Nostos tool returned");
        _mcp.ServerInstructions.Should().Contain("Edit or delete a note, highlight or passage only when the user asks");
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
        // Editions vs books once per work: say so, or a model reports a bug (#1486).
        overview.GetProperty("library").GetProperty("countsNote").GetString()
            .Should().Contain("every edition").And.Contain("each book once");
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
    public async Task A_topic_comes_with_its_own_words_and_cited_notes()
    {
        var bookId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto(
            "Ahab's obsession",
            SelectedText: "He piled upon the whale's white hump the sum of all the general rage.",
            SourceAnchorKind: "physical_page",
            SourceAnchorValue: "184",
            Topics: ["Obsession"]));
        var topicId = (await CallAsync("list_topics", new())).GetProperty("topics")[0].GetProperty("id").GetString()!;

        var topic = await CallAsync("get_topic", new() { ["topicId"] = topicId });

        topic.GetProperty("name").GetString().Should().Be("Obsession");
        var note = topic.GetProperty("linkedNotes").EnumerateArray().Single();
        note.GetProperty("id").GetString().Should().Be($"note:{noteId:D}");
        note.GetProperty("citation").GetProperty("link").GetString().Should().Be($"nostos:cite/{bookId:D}?p=184&as=quote&src={noteId:D}");
    }

    [Fact]
    public async Task A_draft_written_with_citation_links_is_one_Studio_document_with_live_citations_and_a_resend_keeps_one()
    {
        var bookId = await CreateBookAsync("Walden", "Henry David Thoreau", "1854");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto("", SelectedText: "Simplify, simplify."));
        var quote = (await CallAsync("fetch", new() { ["id"] = $"note:{noteId:D}" }))
            .GetProperty("citation").GetProperty("markdown").GetString();
        var args = new Dictionary<string, object?>
        {
            ["name"] = "On simplicity",
            ["content"] = $"# On simplicity\n\nThoreau's advice is short: {quote}\n",
        };

        var first = await CallAsync("create_draft", args);
        var again = await CallAsync("create_draft", args);
        var drafts = await CallAsync("list_drafts", new() { ["query"] = "simplicity" });
        var read = await CallAsync("read_draft", new() { ["writingId"] = first.GetProperty("id").GetGuid() });

        again.GetProperty("id").GetGuid().Should().Be(first.GetProperty("id").GetGuid());
        again.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        first.GetProperty("citations").GetInt32().Should().Be(1);
        first.GetProperty("link").GetString().Should().StartWith("http").And.Contain("/studio");
        drafts.GetProperty("items").EnumerateArray().Should().ContainSingle();
        read.GetProperty("content").GetString().Should().Contain($"src={noteId:D}");
        await using var db = await Contexts().CreateDbContextAsync();
        (await db.Set<Nostos.Backend.Data.Models.WritingCitationModel>().CountAsync(c => c.NoteId == noteId)).Should().Be(1);
    }

    [Fact]
    public async Task A_book_goes_on_To_read_once()
    {
        var args = new Dictionary<string, object?> { ["title"] = "Austerlitz", ["author"] = "W. G. Sebald", ["readNext"] = true };

        var first = await CallAsync("add_to_reading_list", args);
        var again = await CallAsync("add_to_reading_list", args);
        var list = await CallAsync("reading_list", new());

        first.GetProperty("duplicate").GetBoolean().Should().BeFalse();
        again.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        list.GetProperty("total").GetInt32().Should().Be(1);
        list.GetProperty("link").GetString().Should().StartWith("http").And.EndWith("/library/to-read");
    }

    [Fact]
    public async Task An_app_adds_a_paper_book_and_edits_its_title_author_and_description_and_the_app_shows_it()
    {
        var added = await CallAsync("add_book", new()
        {
            ["title"] = "The Rings of Saturn",
            ["author"] = "W. G. Sebald",
            ["publishedDate"] = "1995",
        });
        added.GetProperty("outcome").GetString().Should().Be("created");
        added.GetProperty("duplicate").GetBoolean().Should().BeFalse();
        var book = added.GetProperty("book");
        var bookId = book.GetProperty("id").GetGuid();
        book.GetProperty("type").GetString().Should().Be("physical");
        book.GetProperty("url").GetString().Should().StartWith("http").And.EndWith($"/library/{bookId:D}");

        // Without replace, a detail the book has is kept; an empty one is filled.
        var filled = await CallAsync("update_book", new()
        {
            ["bookId"] = bookId,
            ["title"] = "Die Ringe des Saturn",
            ["description"] = "A walk along the Suffolk coast.",
        });
        filled.GetProperty("changed").EnumerateArray().Select(c => c.GetProperty("field").GetString()).Should().Equal("description");
        filled.GetProperty("kept")[0].GetProperty("field").GetString().Should().Be("title");
        filled.GetProperty("kept")[0].GetProperty("value").GetString().Should().Be("The Rings of Saturn");

        var replaced = await CallAsync("update_book", new()
        {
            ["bookId"] = bookId,
            ["title"] = "The Rings of Saturn: An English Pilgrimage",
            ["author"] = "W.G. Sebald",
            ["description"] = "A walk along the Suffolk coast, and what it recalls.",
            ["replace"] = true,
        });
        var changes = replaced.GetProperty("changed").EnumerateArray()
            .ToDictionary(c => c.GetProperty("field").GetString()!, c => c.GetProperty("from").GetString());
        changes.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["title"] = "The Rings of Saturn",
            ["author"] = "W. G. Sebald",
            ["description"] = "A walk along the Suffolk coast.",
        });
        replaced.GetProperty("undo").GetProperty("replace").GetBoolean().Should().BeTrue();

        // The app reads the same book through its own API.
        var shown = await _http.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
        shown!.Title.Should().Be("The Rings of Saturn: An English Pilgrimage");
        shown.Author.Should().Be("W.G. Sebald");
        shown.Description.Should().Be("A walk along the Suffolk coast, and what it recalls.");
        shown.PublishedDate.Should().Be("1995");

        // undo puts every old value back.
        var undo = replaced.GetProperty("undo").EnumerateObject().ToDictionary(p => p.Name, p => (object?)JsonSerializer.Deserialize<object>(p.Value.GetRawText()));
        await CallAsync("update_book", undo);
        var restored = await _http.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
        restored!.Title.Should().Be("The Rings of Saturn");
        restored.Author.Should().Be("W. G. Sebald");
        restored.Description.Should().Be("A walk along the Suffolk coast.");
    }

    [Fact]
    public async Task A_book_added_with_a_cover_url_has_the_cover_the_app_shows()
    {
        _factory.ProviderContent.Respond = _ => Image(CoverPng);

        var added = await CallAsync("add_book", new()
        {
            ["title"] = "The Rings of Saturn",
            ["author"] = "W. G. Sebald",
            ["coverUrl"] = "https://covers.example/rings-of-saturn.png",
        });

        added.GetProperty("outcome").GetString().Should().Be("created");
        added.TryGetProperty("warning", out _).Should().BeFalse("nothing went wrong with the cover");
        var bookId = added.GetProperty("book").GetProperty("id").GetGuid();

        var shown = await _http.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
        shown!.CoverUrl.Should().NotBeNullOrEmpty();
        shown.CoverVersion.Should().NotBeNullOrEmpty();

        var stored = await _http.GetAsync($"/api/books/{bookId}/cover");
        stored.EnsureSuccessStatusCode();
        (await stored.Content.ReadAsByteArrayAsync()).Should().Equal(CoverPng);
        _factory.ProviderContent.Requests.Should().Equal(new Uri("https://covers.example/rings-of-saturn.png"));
    }

    [Fact]
    public async Task A_cover_url_that_cannot_be_used_still_adds_the_book_and_says_why()
    {
        _factory.ProviderContent.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/page.html" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Not found</body></html>", Encoding.UTF8, "text/html"),
            },
            "/huge.png" => TooLargeResponse(),
            "/redirect.png" => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/cover.png") },
            },
            _ => Image(CoverPng),
        };

        var notAnImage = await CallAsync("add_book", new()
        {
            ["title"] = "An HTML page",
            ["author"] = "A. Page",
            ["coverUrl"] = "https://covers.example/page.html",
        });
        var tooLarge = await CallAsync("add_book", new()
        {
            ["title"] = "A huge image",
            ["author"] = "H. Image",
            ["coverUrl"] = "https://covers.example/huge.png",
        });
        var insecure = await CallAsync("add_book", new()
        {
            ["title"] = "A plaintext URL",
            ["author"] = "P. Text",
            ["coverUrl"] = "http://covers.example/cover.png",
        });
        var redirected = await CallAsync("add_book", new()
        {
            ["title"] = "A redirected URL",
            ["author"] = "R. Direct",
            ["coverUrl"] = "https://covers.example/redirect.png",
        });

        foreach (var (added, title) in new[]
                 {
                     (notAnImage, "An HTML page"),
                     (tooLarge, "A huge image"),
                     (insecure, "A plaintext URL"),
                     (redirected, "A redirected URL"),
                 })
        {
            added.GetProperty("outcome").GetString().Should().Be("created");
            added.GetProperty("book").GetProperty("title").GetString().Should().Be(title);
            added.GetProperty("warning").GetString().Should().StartWith("added without cover: ");
        }

        notAnImage.GetProperty("warning").GetString().Should().Contain("PNG or JPEG");
        tooLarge.GetProperty("warning").GetString().Should().Contain("larger than 12 MB");
        insecure.GetProperty("warning").GetString().Should().Contain("https");
        redirected.GetProperty("warning").GetString().Should().Contain("not allowed");

        var bookIds = new[] { notAnImage, tooLarge, insecure, redirected }
            .Select(added => added.GetProperty("book").GetProperty("id").GetGuid());
        foreach (var bookId in bookIds)
        {
            var shown = await _http.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
            shown!.CoverUrl.Should().BeNull();
        }

        _factory.ProviderContent.Requests.Should().NotContain(new Uri("http://covers.example/cover.png"),
            "a plaintext URL is refused before anything is fetched");
        _factory.ProviderContent.Requests.Should().NotContain(new Uri("https://evil.example/cover.png"),
            "a redirect is checked against the policy before it is followed");
    }

    [Fact]
    public async Task An_app_sets_a_cover_on_a_book_with_none_and_replaces_it_only_with_replace()
    {
        _factory.ProviderContent.Respond = _ => Image(CoverPng);
        var bookId = await CreateBookAsync("Austerlitz", "W. G. Sebald", "2001");

        var set = await CallAsync("update_book", new()
        {
            ["bookId"] = bookId,
            ["coverUrl"] = "https://covers.example/austerlitz.png",
        });
        set.GetProperty("message").GetString().Should().Contain("Cover set");
        set.GetProperty("changed")[0].GetProperty("field").GetString().Should().Be("cover");
        set.GetProperty("undo").GetProperty("cover").GetString().Should().Contain("cannot be removed");

        var afterSet = await _http.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
        afterSet!.CoverVersion.Should().NotBeNullOrEmpty();
        var version = afterSet.CoverVersion;
        (await (await _http.GetAsync($"/api/books/{bookId}/cover")).Content.ReadAsByteArrayAsync())
            .Should().Equal(CoverPng);

        // Without replace, an existing cover is kept, exactly as a detail is.
        _factory.ProviderContent.Respond = _ => Image(SecondCoverPng);
        var kept = await CallAsync("update_book", new()
        {
            ["bookId"] = bookId,
            ["coverUrl"] = "https://covers.example/other-austerlitz.png",
        });
        kept.GetProperty("kept")[0].GetProperty("field").GetString().Should().Be("cover");
        kept.GetProperty("message").GetString().Should().Contain("Kept what the book already has for cover");
        (await _http.GetFromJsonAsync<BookDto>($"/api/books/{bookId}"))!.CoverVersion.Should().Be(version);

        // With replace, the bytes and the version change.
        var replaced = await CallAsync("update_book", new()
        {
            ["bookId"] = bookId,
            ["coverUrl"] = "https://covers.example/other-austerlitz.png",
            ["replace"] = true,
        });
        replaced.GetProperty("message").GetString().Should().Contain("Cover replaced").And.Contain("cannot be restored");
        replaced.GetProperty("undo").GetProperty("cover").GetString().Should().Contain("cannot be restored");

        var afterReplace = await _http.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
        afterReplace!.CoverVersion.Should().NotBeNullOrEmpty().And.NotBe(version);
        (await (await _http.GetAsync($"/api/books/{bookId}/cover")).Content.ReadAsByteArrayAsync())
            .Should().Equal(SecondCoverPng);
    }

    [Fact]
    public async Task An_app_changes_a_notes_words_and_topics_and_undo_puts_them_back()
    {
        var bookId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto(
            Content: "Ishmael's restlessness",
            SelectedText: "Call me Ishmael.",
            SourceAnchorKind: "physical_page",
            SourceAnchorValue: "3",
            Topics: ["Whiteness"]));

        var updated = await CallAsync("update_note", new()
        {
            ["noteId"] = $"note:{noteId:D}",
            ["thought"] = "Ishmael cannot settle anywhere.",
            ["topics"] = new[] { "Restlessness" },
        });

        updated.GetProperty("id").GetString().Should().Be($"note:{noteId:D}");
        updated.GetProperty("kind").GetString().Should().Be("note");
        updated.GetProperty("note").GetString().Should().Be("Ishmael cannot settle anywhere.");
        updated.GetProperty("quote").GetString().Should().Be("Call me Ishmael.");
        updated.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).Should().Equal("Restlessness");
        updated.GetProperty("url").GetString().Should().EndWith($"/brain/notes/{noteId:D}");

        var undo = updated.GetProperty("undo");
        undo.GetProperty("noteId").GetString().Should().Be($"note:{noteId:D}");
        undo.GetProperty("thought").GetString().Should().Be("Ishmael's restlessness");
        undo.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).Should().Equal("Whiteness");

        // The edit changed the words and the topics, and nothing else: the
        // quote and its page are exactly where they were.
        var stored = await _http.GetFromJsonAsync<NoteSearchHitDto>($"/api/notes/{noteId}");
        stored!.Content.Should().Be("Ishmael cannot settle anywhere.\n\n[[Restlessness]]");
        stored.SelectedText.Should().Be("Call me Ishmael.");
        stored.SourceAnchorKind.Should().Be("physical_page");
        stored.SourceAnchorValue.Should().Be("3");
        stored.TopicNames.Should().Equal("Restlessness");

        // `undo`, passed back as it came, restores the previous words and topics.
        await CallAsync("update_note", new Dictionary<string, object?>
        {
            ["noteId"] = undo.GetProperty("noteId").GetString(),
            ["thought"] = undo.GetProperty("thought").GetString(),
            ["topics"] = undo.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ToArray(),
        });
        var restored = await _http.GetFromJsonAsync<NoteSearchHitDto>($"/api/notes/{noteId}");
        restored!.Content.Should().Be("Ishmael's restlessness\n\n[[Whiteness]]");
        restored.SelectedText.Should().Be("Call me Ishmael.");
        restored.TopicNames.Should().Equal("Whiteness");
    }

    [Fact]
    public async Task Changing_only_the_topics_keeps_the_notes_words_and_leaves_nothing_else_changed()
    {
        var bookId = await CreateBookAsync("Middlemarch", "George Eliot", "1871");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto("Dorothea's choice", Topics: ["Vocation"]));

        var updated = await CallAsync("update_note", new()
        {
            ["noteId"] = noteId.ToString("D"),
            ["topics"] = new[] { "Marriage" },
        });
        var stored = await _http.GetFromJsonAsync<NoteSearchHitDto>($"/api/notes/{noteId}");

        updated.GetProperty("note").GetString().Should().Be("Dorothea's choice");
        updated.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).Should().Equal("Marriage");
        updated.GetProperty("undo").GetProperty("thought").GetString().Should().Be("Dorothea's choice");
        stored!.Content.Should().Be("Dorothea's choice\n\n[[Marriage]]");
        stored.TopicNames.Should().Equal("Marriage");
    }

    [Fact]
    public async Task An_app_removes_a_note_and_gets_back_what_it_held()
    {
        var bookId = await CreateBookAsync("Walden", "Henry David Thoreau", "1854");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto(
            Content: "Simplicity as a discipline.",
            SelectedText: "Simplify, simplify.",
            SourceAnchorKind: "physical_page",
            SourceAnchorValue: "97",
            Topics: ["Simplicity"]));

        var deleted = await CallAsync("delete_note", new() { ["noteId"] = $"note:{noteId:D}" });

        deleted.GetProperty("id").GetString().Should().Be($"note:{noteId:D}");
        deleted.GetProperty("kind").GetString().Should().Be("note");
        deleted.GetProperty("book").GetString().Should().Be("Walden");
        deleted.GetProperty("quote").GetString().Should().Be("Simplify, simplify.");
        deleted.GetProperty("note").GetString().Should().Be("Simplicity as a discipline.");
        deleted.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).Should().Equal("Simplicity");
        deleted.GetProperty("message").GetString().Should().Contain("Removed").And.Contain("save_note");

        (await _http.GetAsync($"/api/notes/{noteId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = await Contexts().CreateDbContextAsync();
        (await db.Notes.CountAsync(n => n.Id == noteId)).Should().Be(0);
    }

    [Fact]
    public async Task An_edited_or_removed_passage_goes_through_its_own_tools_and_keeps_its_title_and_ends()
    {
        var bookId = await CreateBookAsync("The Ring of Bright Water", "Gavin Maxwell", "1960");
        var passageId = await CreateNoteAsync(bookId, new CreateNoteDto(
            Content: "The otters made the house theirs.",
            SelectedText: "There were otters in the burn.",
            SourceAnchorKind: "pdf_page",
            SourceAnchorValue: "10",
            PassageEnd: "12",
            Kind: "passage",
            PassageTitle: "Otters",
            Topics: ["Otters"]));

        var updated = await CallAsync("update_passage", new()
        {
            ["passageId"] = $"note:{passageId:D}",
            ["thought"] = "They took over the whole house.",
            ["topics"] = new[] { "Home" },
        });
        var stored = await _http.GetFromJsonAsync<NoteSearchHitDto>($"/api/notes/{passageId}");

        updated.GetProperty("kind").GetString().Should().Be("passage");
        updated.GetProperty("note").GetString().Should().Be("They took over the whole house.");
        updated.GetProperty("passageTitle").GetString().Should().Be("Otters");
        updated.GetProperty("undo").GetProperty("passageId").GetString().Should().Be($"note:{passageId:D}");
        stored!.Kind.Should().Be("passage");
        stored.PassageTitle.Should().Be("Otters");
        stored.SelectedText.Should().Be("There were otters in the burn.");
        stored.SourceAnchorKind.Should().Be("pdf_page");
        stored.SourceAnchorValue.Should().Be("10");
        stored.PassageEnd.Should().Be("12");
        stored.TopicNames.Should().Equal("Home");

        var deleted = await CallAsync("delete_passage", new() { ["passageId"] = $"note:{passageId:D}" });

        deleted.GetProperty("kind").GetString().Should().Be("passage");
        deleted.GetProperty("passageTitle").GetString().Should().Be("Otters");
        deleted.GetProperty("quote").GetString().Should().Be("There were otters in the burn.");
        deleted.GetProperty("note").GetString().Should().Be("They took over the whole house.");
        deleted.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).Should().Equal("Home");
        (await _http.GetAsync($"/api/notes/{passageId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_id_and_the_wrong_kind_are_tool_errors_the_model_can_act_on()
    {
        var bookId = await CreateBookAsync("Moby-Dick", "Herman Melville", "1851");
        var noteId = await CreateNoteAsync(bookId, new CreateNoteDto("A note."));
        var passageId = await CreateNoteAsync(bookId, new CreateNoteDto(
            Content: "",
            SelectedText: "The whale is a symbol.",
            SourceAnchorKind: "pdf_page",
            SourceAnchorValue: "4",
            PassageEnd: "6",
            Kind: "passage",
            PassageTitle: "The whale"));

        var missing = await _mcp.CallToolAsync("update_note", new Dictionary<string, object?>
        {
            ["noteId"] = $"note:{Guid.NewGuid():D}",
            ["thought"] = "Words for no note.",
        });
        var notAnId = await _mcp.CallToolAsync("delete_note", new Dictionary<string, object?> { ["noteId"] = "nonsense" });
        var notAPassage = await _mcp.CallToolAsync("update_passage", new Dictionary<string, object?>
        {
            ["passageId"] = $"note:{noteId:D}",
            ["thought"] = "Words for the wrong kind.",
        });
        var notANote = await _mcp.CallToolAsync("delete_note", new Dictionary<string, object?> { ["noteId"] = passageId.ToString("D") });
        var nothing = await _mcp.CallToolAsync("update_note", new Dictionary<string, object?> { ["noteId"] = noteId.ToString("D") });

        missing.IsError.Should().BeTrue();
        Text(missing).Should().Contain("No note in this library has that id").And.Contain("list_notes");
        notAnId.IsError.Should().BeTrue();
        Text(notAnId).Should().Contain("not a note id");
        notAPassage.IsError.Should().BeTrue();
        Text(notAPassage).Should().Contain("is a note or highlight, not a passage").And.Contain("update_note");
        notANote.IsError.Should().BeTrue();
        Text(notANote).Should().Contain("is a passage").And.Contain("delete_passage");
        nothing.IsError.Should().BeTrue();
        Text(nothing).Should().Contain("thought").And.Contain("topics");

        // Nothing was removed by a refused call.
        (await _http.GetAsync($"/api/notes/{noteId}")).EnsureSuccessStatusCode();
        (await _http.GetAsync($"/api/notes/{passageId}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Adding_a_book_that_is_on_To_read_moves_it_and_keeps_the_reason_on_the_book()
    {
        await CallAsync("add_to_reading_list", new()
        {
            ["title"] = "Vertigo",
            ["author"] = "W. G. Sebald",
            ["why"] = "Mara said it is the strangest of the four.",
        });

        var added = await CallAsync("add_book", new() { ["title"] = "Vertigo", ["author"] = "W. G. Sebald" });
        var resent = await CallAsync("add_book", new() { ["title"] = "Vertigo", ["author"] = "W. G. Sebald" });
        var again = await CallAsync("add_book", new() { ["title"] = "Vertigo", ["author"] = "W. G. Sebald", ["publishedDate"] = "1990" });

        var bookId = added.GetProperty("book").GetProperty("id").GetGuid();
        added.GetProperty("movedFromToRead").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("reasonKeptAsNote").GetBoolean().Should().BeTrue();
        resent.GetProperty("book").GetProperty("id").GetGuid().Should().Be(bookId, "a resend returns the first answer");
        again.GetProperty("outcome").GetString().Should().Be("matched");
        again.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        again.GetProperty("book").GetProperty("id").GetGuid().Should().Be(bookId);
        again.TryGetProperty("movedFromToRead", out _).Should().BeFalse();
        (await CallAsync("reading_list", new())).GetProperty("total").GetInt32().Should().Be(0);
        await using var db = await Contexts().CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(1);
        (await db.Notes.SingleAsync(n => n.BookId == bookId)).Content.Should().Contain("Mara said it is the strangest of the four.");
    }

    [Fact]
    public async Task Adding_a_book_the_user_already_owns_and_keeps_on_To_read_leaves_the_entry_there()
    {
        var owned = await CreateBookAsync("Time and Free Will", "Henri Bergson", "1889");
        var listed = await CallAsync("add_to_reading_list", new()
        {
            ["title"] = "Time and Free Will",
            ["author"] = "Henri Bergson",
            ["why"] = "Before Matter and Memory.",
            ["readNext"] = true,
        });
        var entryId = listed.GetProperty("entry").GetProperty("id").GetGuid();
        listed.GetProperty("entry").GetProperty("ownedBookId").GetGuid().Should().Be(owned);

        var matched = await CallAsync("add_book", new() { ["title"] = "Time and Free Will", ["author"] = "Henri Bergson" });

        matched.GetProperty("outcome").GetString().Should().Be("matched");
        matched.GetProperty("book").GetProperty("id").GetGuid().Should().Be(owned);
        matched.TryGetProperty("movedFromToRead", out _).Should().BeFalse();
        var kept = matched.GetProperty("onToRead").EnumerateArray().Should().ContainSingle().Subject;
        kept.GetProperty("entryId").GetGuid().Should().Be(entryId);
        kept.GetProperty("shelf").GetString().Should().Be("next");
        matched.GetProperty("message").GetString().Should().Contain("To read");

        // A separate copy made on purpose does not take the entry off either.
        var separate = await CallAsync("add_book", new() { ["title"] = "Time and Free Will", ["author"] = "Henri Bergson", ["forceCreate"] = true });
        separate.GetProperty("outcome").GetString().Should().Be("created");
        separate.TryGetProperty("movedFromToRead", out _).Should().BeFalse();

        var list = await CallAsync("reading_list", new());
        list.GetProperty("total").GetInt32().Should().Be(1);
        list.GetProperty("entries")[0].GetProperty("id").GetGuid().Should().Be(entryId);
        await using var db = await Contexts().CreateDbContextAsync();
        (await db.Notes.CountAsync(n => n.Content.Contains("Before Matter and Memory."))).Should().Be(0, "the reason only moves to a book when the entry leaves the list");
    }

    [Fact]
    public async Task A_title_alone_that_may_be_another_book_adds_nothing_and_offers_the_candidates()
    {
        var existing = await CreateBookAsync("Austerlitz", "W. G. Sebald", "2001");
        var before = await StateVersionAsync();

        var asked = await CallAsync("add_book", new() { ["title"] = "Austerlitz" });

        asked.GetProperty("outcome").GetString().Should().Be("choose");
        asked.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).Should().Equal(existing);
        (await StateVersionAsync()).Should().Be(before);

        var chosen = await CallAsync("add_book", new() { ["title"] = "Austerlitz", ["confirmedBookId"] = existing });
        var separate = await CallAsync("add_book", new() { ["title"] = "Austerlitz", ["forceCreate"] = true });

        chosen.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        chosen.GetProperty("book").GetProperty("id").GetGuid().Should().Be(existing);
        separate.GetProperty("outcome").GetString().Should().Be("created");
        separate.GetProperty("book").GetProperty("id").GetGuid().Should().NotBe(existing);
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

    /// <summary>A 1x1 PNG, a valid cover image.</summary>
    private static readonly byte[] CoverPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGM4IScHAAK2AQU0pnWqAAAAAElFTkSuQmCC");

    /// <summary>A different 2x2 PNG, so a replacement can be told from the cover it replaced.</summary>
    private static readonly byte[] SecondCoverPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGOQqzgBRAwQCgAjrgV5XtGN4QAAAABJRU5ErkJggg==");

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage TooLargeResponse()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentLength = CoverDownload.MaxCoverBytes + 1;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

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

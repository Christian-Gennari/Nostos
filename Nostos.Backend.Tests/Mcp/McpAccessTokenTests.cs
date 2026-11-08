using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Services.Mcp;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Mcp;

/// <summary>
/// The SelfHosted host as it ships (issue #1469): <c>/mcp</c> behind access
/// tokens made in Settings, with every log line kept so a test can prove the
/// token never reaches one.
/// </summary>
public sealed class McpAccessTokenFactory : LibraryEndpointFactory
{
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            lines.Enqueue($"{category} scope: {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {structured} {exception}");
        }
    }
}

public sealed class McpAccessTokenTests : IAsyncLifetime
{
    private readonly McpAccessTokenFactory _factory = new();
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _http = _factory.CreateClient();
        (await _http.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_token_is_made_listed_and_revoked_and_shown_only_once()
    {
        var create = await _http.PostAsJsonAsync(McpAccessTokenEndpoints.Route, new { name = "Claude Code", scope = "read_write" });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        create.Headers.CacheControl!.NoStore.Should().BeTrue();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("token").GetString()!;
        token.Should().StartWith("nst_").And.HaveLength(47);
        var id = created.GetProperty("details").GetProperty("id").GetString();
        created.GetProperty("details").GetProperty("scope").GetString().Should().Be("read_write");
        created.GetProperty("details").GetProperty("kind").GetString().Should().Be("token");

        var list = await _http.GetStringAsync(McpConnectedAppsEndpoints.Route);
        var page = JsonDocument.Parse(list).RootElement;
        page.GetProperty("canCreate").GetBoolean().Should().BeTrue();
        var item = page.GetProperty("apps").EnumerateArray().Single();
        item.GetProperty("id").GetString().Should().Be(id);
        item.GetProperty("kind").GetString().Should().Be("token");
        item.GetProperty("name").GetString().Should().Be("Claude Code");
        item.GetProperty("scope").GetString().Should().Be("read_write");
        item.GetProperty("createdAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        item.GetProperty("lastUsedAt").ValueKind.Should().Be(JsonValueKind.Null);
        list.Should().NotContain(token).And.NotContain(token[4..]).And.NotContain(McpAccessTokenService.Hash(token));

        await using (var db = await Contexts().CreateDbContextAsync())
        {
            var row = await db.McpAccessTokens.AsNoTracking().SingleAsync();
            row.TokenHash.Should().Be(McpAccessTokenService.Hash(token)).And.NotContain(token[4..]);
        }

        (await _http.DeleteAsync($"{McpConnectedAppsEndpoints.Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _http.DeleteAsync($"{McpConnectedAppsEndpoints.Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.DeleteAsync($"{McpConnectedAppsEndpoints.Route}/not-an-id")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.GetFromJsonAsync<JsonElement>(McpConnectedAppsEndpoints.Route)).GetProperty("apps").GetArrayLength().Should().Be(0);

        // A revoked SelfHosted token is gone, not blocked: there is nothing to
        // allow again (#1483), so restore finds nothing and brings nothing back.
        (await _http.PostAsync($"{McpConnectedAppsEndpoints.Route}/{id}/restore", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.GetFromJsonAsync<JsonElement>(McpConnectedAppsEndpoints.Route)).GetProperty("apps").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("", "read")]
    [InlineData("   ", "read_write")]
    [InlineData("Cursor", "write")]
    [InlineData("Cursor", "admin")]
    public async Task A_token_needs_a_name_and_a_known_scope(string name, string scope)
    {
        var response = await _http.PostAsJsonAsync(McpAccessTokenEndpoints.Route, new { name, scope });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Contexts().CreateDbContext().McpAccessTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_capabilities_offer_connected_apps_on_SelfHosted()
    {
        var capabilities = await _http.GetFromJsonAsync<JsonElement>("/api/runtime/capabilities");

        capabilities.GetProperty("supportsMcpConnectedApps").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Mcp_refuses_a_call_without_a_token_with_a_wrong_one_and_with_a_malformed_one()
    {
        await CreateTokenAsync("real", "read");

        var none = await ListToolsRawAsync(null);
        var wrong = await ListToolsRawAsync("nst_" + new string('A', 43));
        var malformed = await ListToolsRawAsync("not-a-token");
        var basic = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, NostosMcp.Path)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz") },
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        });

        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        none.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrong.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        malformed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        basic.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_write_token_searches_and_saves_a_note_and_revoking_it_refuses_the_next_call()
    {
        var bookId = await CreateBookAsync("Moby-Dick");
        var (id, token) = await CreateTokenAsync("Claude Code", "read_write");
        await using var mcp = await ConnectAsync(token);

        var search = await mcp.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "whale" });
        var save = await mcp.CallToolAsync("save_note", new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["thought"] = "The whiteness of the whale is the point.",
        });

        search.IsError.Should().NotBe(true, Text(search));
        save.IsError.Should().NotBe(true, Text(save));
        await using (var db = await Contexts().CreateDbContextAsync())
        {
            (await db.Notes.CountAsync(n => n.BookId == bookId)).Should().Be(1);
            (await db.McpAccessTokens.SingleAsync()).LastUsedAtUtc.Should().NotBeNull();
        }

        (await _http.DeleteAsync($"{McpConnectedAppsEndpoints.Route}/{id}")).EnsureSuccessStatusCode();

        (await ListToolsRawAsync(token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("save_note")]
    [InlineData("save_passage")]
    [InlineData("create_draft")]
    [InlineData("add_to_reading_list")]
    [InlineData("add_book")]
    [InlineData("update_book")]
    public async Task A_read_only_token_is_refused_every_write_tool_and_nothing_is_written(string tool)
    {
        var bookId = await CreateBookAsync("Walden");
        var (_, token) = await CreateTokenAsync("Cursor", "read");
        await using var mcp = await ConnectAsync(token);
        var before = await CountsAsync();

        var result = await mcp.CallToolAsync(tool, new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["thought"] = "Simplify.",
            ["title"] = "A draft",
            ["markdown"] = "Some words.",
            ["quoteRef"] = "q1-00000000000000000000000000000000-abc-0-0-0-1",
        });
        var read = await mcp.CallToolAsync("list_books", new Dictionary<string, object?>());

        result.IsError.Should().BeTrue();
        Text(result).Should().Be(McpScopes.ReadOnlyRefusal);
        read.IsError.Should().NotBe(true, Text(read));
        (await CountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task The_token_never_reaches_a_log_line_or_a_later_response()
    {
        var bookId = await CreateBookAsync("Middlemarch");
        var (id, token) = await CreateTokenAsync("Hermes", "read_write");
        await using (var mcp = await ConnectAsync(token))
        {
            await mcp.CallToolAsync("save_note", new Dictionary<string, object?>
            {
                ["bookId"] = bookId,
                ["thought"] = "Dorothea's choice.",
            });
        }
        var wrong = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");
        await ListToolsRawAsync(wrong);
        var list = await _http.GetStringAsync(McpConnectedAppsEndpoints.Route);
        var capabilities = await _http.GetStringAsync("/api/runtime/capabilities");
        await _http.DeleteAsync($"{McpConnectedAppsEndpoints.Route}/{id}");
        await ListToolsRawAsync(token);

        _factory.Logs.Lines.Should().NotBeEmpty();
        var secret = token[4..];
        foreach (var line in _factory.Logs.Lines)
        {
            line.Should().NotContain(secret);
            line.Should().NotContain(wrong[4..]);
            line.Should().NotContain(McpAccessTokenService.Hash(token));
        }
        list.Should().NotContain(secret);
        capabilities.Should().NotContain(secret);
    }

    [Fact]
    public async Task Failed_attempts_are_rate_limited_and_a_good_token_does_not_count()
    {
        var (_, token) = await CreateTokenAsync("real", "read");
        for (var i = 0; i < 3; i++)
            (await ListToolsRawAsync(token)).StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        for (var i = 0; i < McpAccessTokenFailedAttemptThrottle.MaxFailures; i++)
            (await ListToolsRawAsync("nst_" + new string('B', 43))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var blocked = await ListToolsRawAsync("nst_" + new string('C', 43));
        var blockedGood = await ListToolsRawAsync(token);

        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.Should().NotBeNull();
        blockedGood.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void The_failed_attempt_window_ends()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T10:00:00Z"));
        var throttle = new McpAccessTokenFailedAttemptThrottle(clock);
        for (var i = 0; i < McpAccessTokenFailedAttemptThrottle.MaxFailures; i++)
            throttle.RecordFailure("10.0.0.2");

        throttle.RetryAfter("10.0.0.2").Should().NotBeNull();
        throttle.RetryAfter("10.0.0.3").Should().BeNull();
        clock.Now += McpAccessTokenFailedAttemptThrottle.Window;
        throttle.RetryAfter("10.0.0.2").Should().BeNull();
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-07T10:00:00Z"));
        var service = new McpAccessTokenService(Contexts(), clock);
        var created = await service.CreateAsync("Agent", "read");

        (await service.AuthenticateAsync(created.Token)).Should().NotBeNull();
        var first = await LastUsedAsync();
        clock.Now += TimeSpan.FromSeconds(30);
        await service.AuthenticateAsync(created.Token);
        var unchanged = await LastUsedAsync();
        clock.Now += TimeSpan.FromSeconds(31);
        await service.AuthenticateAsync(created.Token);
        var moved = await LastUsedAsync();

        first.Should().Be(new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc));
        unchanged.Should().Be(first);
        moved.Should().Be(first!.Value.AddSeconds(61));
        (await service.AuthenticateAsync(created.Token.ToUpperInvariant())).Should().BeNull();
        (await service.AuthenticateAsync(created.Token + "x")).Should().BeNull();
    }

    // ------------------------------------------------------------------

    private async Task<(string Id, string Token)> CreateTokenAsync(string name, string scope)
    {
        var response = await _http.PostAsJsonAsync(McpAccessTokenEndpoints.Route, new { name, scope });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("details").GetProperty("id").GetString()!, body.GetProperty("token").GetString()!);
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await McpClient.CreateAsync(
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

    private Task<HttpResponseMessage> ListToolsRawAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, NostosMcp.Path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(request);
    }

    private async Task<Guid> CreateBookAsync(string title)
    {
        var response = await _http.PostAsJsonAsync("/api/books", new { type = "physical", title, author = "Someone" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<(int Notes, int Writings, int ReadingList)> CountsAsync()
    {
        await using var db = await Contexts().CreateDbContextAsync();
        return (await db.Notes.CountAsync(), await db.Writings.CountAsync(), await db.ReadingListEntries.CountAsync());
    }

    private async Task<DateTime?> LastUsedAsync()
    {
        await using var db = await Contexts().CreateDbContextAsync();
        return (await db.McpAccessTokens.AsNoTracking().SingleAsync()).LastUsedAtUtc;
    }

    private IDbContextFactory<NostosDbContext> Contexts() =>
        _factory.Services.GetRequiredService<IDbContextFactory<NostosDbContext>>();

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

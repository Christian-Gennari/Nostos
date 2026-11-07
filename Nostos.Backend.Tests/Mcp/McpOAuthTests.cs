using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nostos.Backend.Data;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Services.Mcp;
using Nostos.Backend.Services.Mcp.OAuth;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Mcp;

/// <summary>
/// The SelfHosted host with <c>Mcp:PublicBaseUrl</c> set (issue #1470). The
/// one stand-in is the client metadata fetch, which would need the internet;
/// its limits are covered by <see cref="McpClientMetadataFetcherTests"/>.
/// </summary>
public sealed class McpOAuthFactory : LibraryEndpointFactory
{
    public const string PublicBase = "https://nostos.example.ts.net";
    public const string ClaudeClientId = "https://claude.ai/oauth/mcp-oauth-client-metadata";
    public const string ClaudeRedirect = "https://claude.ai/api/mcp/auth_callback";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(McpOAuthSettings.ConfigurationKey, PublicBase);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMcpClientMetadataFetcher>();
            services.AddSingleton<IMcpClientMetadataFetcher>(new KnownClients(
                new McpClientMetadata(ClaudeClientId, "Claude", [ClaudeRedirect])));
        });
    }

    private sealed class KnownClients(params McpClientMetadata[] clients) : IMcpClientMetadataFetcher
    {
        public Task<McpClientMetadataResult> FetchAsync(string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(clients.FirstOrDefault(c => c.ClientId == clientId) is { } client
                ? new McpClientMetadataResult(client, null)
                : McpClientMetadataResult.Fail("The app's address answered 404."));
    }
}

public sealed class McpOAuthTests : IAsyncLifetime
{
    private const string Resource = McpOAuthFactory.PublicBase + "/mcp";
    private readonly McpOAuthFactory _factory = new();

    /// <summary>The owner, on the private network.</summary>
    private HttpClient _local = null!;

    /// <summary>The app, through the tunnel: requests carry the public host.</summary>
    private HttpClient _public = null!;

    public async Task InitializeAsync()
    {
        _local = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _public = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(McpOAuthFactory.PublicBase),
        });
        (await _local.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _local.Dispose();
        _public.Dispose();
        await _factory.DisposeAsync();
    }

    // --- metadata -------------------------------------------------------

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task The_protected_resource_metadata_names_the_public_mcp_url(string path)
    {
        var prm = await _public.GetFromJsonAsync<JsonElement>(path);

        prm.GetProperty("resource").GetString().Should().Be(Resource);
        prm.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()).Should().Equal(McpOAuthFactory.PublicBase);
        prm.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()).Should().Equal("nostos:read", "nostos:write");
        prm.GetProperty("bearer_methods_supported").EnumerateArray().Select(e => e.GetString()).Should().Equal("header");
    }

    [Fact]
    public async Task The_authorization_server_metadata_offers_code_pkce_and_cimd_only()
    {
        var meta = await _public.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");

        meta.GetProperty("issuer").GetString().Should().Be(McpOAuthFactory.PublicBase);
        meta.GetProperty("authorization_endpoint").GetString().Should().Be(McpOAuthFactory.PublicBase + "/oauth/authorize");
        meta.GetProperty("token_endpoint").GetString().Should().Be(McpOAuthFactory.PublicBase + "/oauth/token");
        meta.GetProperty("revocation_endpoint").GetString().Should().Be(McpOAuthFactory.PublicBase + "/oauth/revoke");
        Strings(meta, "response_types_supported").Should().Equal("code");
        Strings(meta, "grant_types_supported").Should().Equal("authorization_code", "refresh_token");
        Strings(meta, "code_challenge_methods_supported").Should().Equal("S256");
        Strings(meta, "token_endpoint_auth_methods_supported").Should().Equal("none");
        Strings(meta, "scopes_supported").Should().Equal("nostos:read", "nostos:write");
        meta.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
        meta.TryGetProperty("registration_endpoint", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_401_on_mcp_points_at_the_protected_resource_metadata()
    {
        var none = await ListToolsRawAsync(_public, null);
        var wrong = await ListToolsRawAsync(_public, "nsa_" + new string('A', 43));

        none.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var challenge = none.Headers.WwwAuthenticate.ToString();
        challenge.Should().Contain($"resource_metadata=\"{McpOAuthFactory.PublicBase}/.well-known/oauth-protected-resource/mcp\"");
        challenge.Should().Contain("scope=\"nostos:read nostos:write\"");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrong.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token").And.Contain("resource_metadata=");
    }

    // --- the whole flow -------------------------------------------------

    [Fact]
    public async Task Claude_connects_after_the_owner_approves_and_appears_in_connected_apps()
    {
        var bookId = await CreateBookAsync("Moby-Dick");
        var verifier = Verifier();
        var page = await AuthorizeAsync(Challenge(verifier), state: "xyz");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        page.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'");
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("Claude wants to connect to your Nostos").And.Contain("Settings → Connected apps");

        var pending = await PendingAsync();
        var request = pending.Single();
        html.Should().Contain(request.GetProperty("code").GetString());
        request.GetProperty("code").GetString().Should().MatchRegex("^[A-Z]{4}$");
        request.GetProperty("clientName").GetString().Should().Be("Claude");
        request.GetProperty("clientHost").GetString().Should().Be("claude.ai");
        request.GetProperty("redirectHost").GetString().Should().Be("claude.ai");
        request.GetProperty("requestedScope").GetString().Should().Be("read_write");

        (await PollAsync(RequestId(html))).GetProperty("status").GetString().Should().Be("pending");
        (await _local.PostAsJsonAsync($"/api/mcp/oauth/requests/{request.GetProperty("id").GetString()}/approve", new { scope = "read_write" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var redirect = new Uri((await PollAsync(RequestId(html))).GetProperty("redirect").GetString()!);
        (await PendingAsync()).Should().BeEmpty();

        redirect.GetLeftPart(UriPartial.Path).Should().Be(McpOAuthFactory.ClaudeRedirect);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(redirect.Query);
        query["state"].ToString().Should().Be("xyz");
        query["iss"].ToString().Should().Be(McpOAuthFactory.PublicBase);

        var token = await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"].ToString(),
            ["redirect_uri"] = McpOAuthFactory.ClaudeRedirect,
            ["client_id"] = McpOAuthFactory.ClaudeClientId,
            ["code_verifier"] = verifier,
            ["resource"] = Resource,
        });
        token.StatusCode.Should().Be(HttpStatusCode.OK);
        token.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.GetProperty("expires_in").GetInt32().Should().Be(3600);
        body.GetProperty("scope").GetString().Should().Be("nostos:read nostos:write");
        var access = body.GetProperty("access_token").GetString()!;
        access.Should().StartWith("nsa_");
        body.GetProperty("refresh_token").GetString().Should().StartWith("nsr_");

        await using (var mcp = await ConnectAsync(access))
        {
            var save = await mcp.CallToolAsync("save_note", new Dictionary<string, object?>
            {
                ["bookId"] = bookId,
                ["thought"] = "Call me Ishmael.",
            });
            save.IsError.Should().NotBe(true, Text(save));

            // The public address serves no app pages: no link may point there.
            var book = await mcp.CallToolAsync("get_book", new Dictionary<string, object?> { ["bookId"] = bookId });
            book.IsError.Should().NotBe(true, Text(book));
            Text(book).Should().NotContain(McpOAuthFactory.PublicBase).And.Contain($"/library/{bookId:D}");
        }

        var apps = (await _local.GetFromJsonAsync<JsonElement>(McpConnectedAppsEndpoints.Route)).GetProperty("apps");
        var app = apps.EnumerateArray().Single();
        app.GetProperty("kind").GetString().Should().Be("oauth_app");
        app.GetProperty("name").GetString().Should().Be("Claude");
        app.GetProperty("scope").GetString().Should().Be("read_write");
        app.GetProperty("lastUsedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        apps.GetRawText().Should().NotContain(access);

        await using var db = await Contexts().CreateDbContextAsync();
        (await db.McpOAuthTokens.AnyAsync(t => t.TokenHash == McpAccessTokenService.Hash(access))).Should().BeTrue();
        (await db.McpOAuthTokens.Select(t => t.TokenHash).ToListAsync()).Should().NotContain(access);
    }

    [Fact]
    public async Task A_read_only_approval_is_refused_on_write_tools()
    {
        var bookId = await CreateBookAsync("Walden");
        var (access, _) = await ConnectClaudeAsync(approveScope: "read");
        await using var mcp = await ConnectAsync(access);

        var save = await mcp.CallToolAsync("save_note", new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["thought"] = "Simplify.",
        });
        var read = await mcp.CallToolAsync("list_books", new Dictionary<string, object?>());

        save.IsError.Should().BeTrue();
        Text(save).Should().Be(McpScopes.ReadOnlyRefusal);
        read.IsError.Should().NotBe(true, Text(read));
        await using var db = await Contexts().CreateDbContextAsync();
        (await db.Notes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_denied_request_sends_the_app_back_with_access_denied()
    {
        var html = await (await AuthorizeAsync(Challenge(Verifier()), state: "s1")).Content.ReadAsStringAsync();
        var id = (await PendingAsync()).Single().GetProperty("id").GetString();

        (await _local.PostAsJsonAsync($"/api/mcp/oauth/requests/{id}/deny", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var redirect = new Uri((await PollAsync(RequestId(html))).GetProperty("redirect").GetString()!);

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(redirect.Query);
        query["error"].ToString().Should().Be("access_denied");
        query["state"].ToString().Should().Be("s1");
        query.ContainsKey("code").Should().BeFalse();
        (await _local.PostAsJsonAsync($"/api/mcp/oauth/requests/{id}/approve", new { scope = "read" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- authorize errors ---------------------------------------------

    [Fact]
    public async Task A_redirect_uri_the_client_does_not_list_gets_an_error_page_and_no_redirect()
    {
        var response = await _public.GetAsync(AuthorizeUrl(Challenge(Verifier()), redirectUri: "https://evil.example/callback"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Location.Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().Contain("cannot connect");
        (await PendingAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_client_gets_an_error_page_and_no_redirect()
    {
        var response = await _public.GetAsync(AuthorizeUrl(Challenge(Verifier()), clientId: "https://unknown.example/client.json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Location.Should().BeNull();
    }

    [Theory]
    [InlineData("code_challenge_method=plain", "invalid_request")]
    [InlineData("no_challenge", "invalid_request")]
    [InlineData("response_type=token", "unsupported_response_type")]
    [InlineData("resource=https://elsewhere.example/mcp", "invalid_target")]
    [InlineData("scope=openid", "invalid_scope")]
    public async Task A_bad_authorization_request_is_sent_back_with_an_error(string variant, string error)
    {
        var url = AuthorizeUrl(Challenge(Verifier()), state: "st");
        url = variant switch
        {
            "no_challenge" => Regex.Replace(url, "&code_challenge=[^&]*", ""),
            "code_challenge_method=plain" => url.Replace("code_challenge_method=S256", "code_challenge_method=plain"),
            "response_type=token" => url.Replace("response_type=code", "response_type=token"),
            "resource=https://elsewhere.example/mcp" => Regex.Replace(url, "&resource=[^&]*", "&resource=" + Uri.EscapeDataString("https://elsewhere.example/mcp")),
            "scope=openid" => Regex.Replace(url, "&scope=[^&]*", "&scope=openid"),
            _ => url,
        };

        var response = await _public.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be(McpOAuthFactory.ClaudeRedirect);
        query["error"].ToString().Should().Be(error);
        query["state"].ToString().Should().Be("st");
        (await PendingAsync()).Should().BeEmpty();
    }

    // --- token errors ---------------------------------------------------

    [Fact]
    public async Task A_wrong_code_verifier_fails_and_leaves_the_code_for_the_real_client()
    {
        var verifier = Verifier();
        var code = await ApprovedCodeAsync(Challenge(verifier), "read_write");

        var wrong = await RedeemAsync(code, Verifier());
        var right = await RedeemAsync(code, verifier);

        await ExpectOAuthErrorAsync(wrong, "invalid_grant");
        right.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = await Contexts().CreateDbContextAsync();
        (await db.McpOAuthGrants.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_wrong_redirect_uri_or_client_at_the_token_endpoint_fails()
    {
        var verifier = Verifier();
        var first = await ApprovedCodeAsync(Challenge(verifier), "read");
        var second = await ApprovedCodeAsync(Challenge(verifier), "read");

        await ExpectOAuthErrorAsync(await RedeemAsync(first, verifier, redirectUri: "https://claude.ai/other"), "invalid_grant");
        await ExpectOAuthErrorAsync(await RedeemAsync(second, verifier, clientId: "https://other.example/client.json"), "invalid_grant");
    }

    [Fact]
    public async Task A_code_used_twice_fails_and_revokes_what_the_first_use_made()
    {
        var verifier = Verifier();
        var code = await ApprovedCodeAsync(Challenge(verifier), "read");
        var first = await RedeemAsync(code, verifier);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        (await ListToolsRawAsync(_public, access)).StatusCode.Should().Be(HttpStatusCode.OK);

        await ExpectOAuthErrorAsync(await RedeemAsync(code, verifier), "invalid_grant");

        (await ListToolsRawAsync(_public, access)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_and_reusing_an_old_refresh_token_revokes_the_grant()
    {
        var (access1, refresh1) = await ConnectClaudeAsync("read_write");

        var rotated = await RefreshAsync(refresh1);
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await rotated.Content.ReadFromJsonAsync<JsonElement>();
        var access2 = body.GetProperty("access_token").GetString()!;
        var refresh2 = body.GetProperty("refresh_token").GetString()!;
        refresh2.Should().NotBe(refresh1);
        body.GetProperty("scope").GetString().Should().Be("nostos:read nostos:write");
        (await ListToolsRawAsync(_public, access2)).StatusCode.Should().Be(HttpStatusCode.OK);
        await AgeSpentRefreshTokensAsync(McpOAuthService.RefreshReuseGrace + TimeSpan.FromSeconds(1));

        var replay = await RefreshAsync(refresh1);

        await ExpectOAuthErrorAsync(replay, "invalid_grant");
        await ExpectOAuthErrorAsync(await RefreshAsync(refresh2), "invalid_grant");
        (await ListToolsRawAsync(_public, access2)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ListToolsRawAsync(_public, access1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _local.GetFromJsonAsync<JsonElement>(McpConnectedAppsEndpoints.Route)).GetProperty("apps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_refresh_retried_within_the_grace_window_gets_a_fresh_pair_and_keeps_the_grant()
    {
        var (_, refresh1) = await ConnectClaudeAsync("read");

        var first = await RefreshAsync(refresh1);
        var retry = await RefreshAsync(refresh1);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var b = await retry.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("access_token").GetString().Should().NotBe(a.GetProperty("access_token").GetString());
        (await ListToolsRawAsync(_public, a.GetProperty("access_token").GetString())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListToolsRawAsync(_public, b.GetProperty("access_token").GetString())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefreshAsync(b.GetProperty("refresh_token").GetString()!)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_grant_idle_past_the_refresh_lifetime_is_no_longer_listed()
    {
        var (access, _) = await ConnectClaudeAsync("read");
        await using (var db = await Contexts().CreateDbContextAsync())
        {
            await db.McpOAuthTokens.ExecuteUpdateAsync(set => set.SetProperty(t => t.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        }

        (await _local.GetFromJsonAsync<JsonElement>(McpConnectedAppsEndpoints.Route)).GetProperty("apps").GetArrayLength().Should().Be(0);
        (await ListToolsRawAsync(_public, access)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using var check = await Contexts().CreateDbContextAsync();
        (await check.McpOAuthGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task One_address_can_keep_only_a_few_requests_waiting()
    {
        for (var i = 0; i < McpOAuthRequests.MaxPendingPerAddress; i++)
            (await AuthorizeAsync(Challenge(Verifier()))).StatusCode.Should().Be(HttpStatusCode.OK);

        var refused = await AuthorizeAsync(Challenge(Verifier()), state: "st");

        refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(refused.Headers.Location!.Query)["error"].ToString()
            .Should().Be("temporarily_unavailable");
        (await PendingAsync()).Should().HaveCount(McpOAuthRequests.MaxPendingPerAddress);
    }

    [Fact]
    public void A_code_tried_again_while_its_grant_is_written_is_caught()
    {
        var requests = new McpOAuthRequests();
        var client = new McpClientMetadata(McpOAuthFactory.ClaudeClientId, "Claude", [McpOAuthFactory.ClaudeRedirect]);
        var (id, _) = requests.Create(client, McpOAuthFactory.ClaudeRedirect, null, Challenge(Verifier()), "read", McpOAuthFactory.PublicBase)!.Value;
        requests.Approve(id, "read").Should().BeTrue();
        var redirect = ((McpOAuthPollResult.Done)requests.Poll(id)).RedirectUrl;
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        requests.Redeem(code, _ => false, out _, out var none).Should().BeNull();
        none.Should().BeNull("a failed check leaves the code unspent");
        requests.Redeem(code, _ => true, out var grantId, out _).Should().NotBeNull();
        requests.Redeem(code, _ => true, out _, out var spent).Should().BeNull();

        spent.Should().Be(grantId);
        requests.FinishRedeem(code).Should().BeTrue();
    }

    [Fact]
    public async Task A_refresh_for_another_client_fails_without_revoking()
    {
        var (access, refresh) = await ConnectClaudeAsync("read");

        await ExpectOAuthErrorAsync(await RefreshAsync(refresh, "https://other.example/client.json"), "invalid_grant");

        (await RefreshAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_in_settings_stops_the_next_refresh_and_the_next_mcp_call()
    {
        var (access, refresh) = await ConnectClaudeAsync("read_write");
        (await ListToolsRawAsync(_public, access)).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await _local.GetFromJsonAsync<JsonElement>(McpConnectedAppsEndpoints.Route))
            .GetProperty("apps").EnumerateArray().Single().GetProperty("id").GetString();

        (await _local.DeleteAsync($"{McpConnectedAppsEndpoints.Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ListToolsRawAsync(_public, access)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await ExpectOAuthErrorAsync(await RefreshAsync(refresh), "invalid_grant");
        await using var db = await Contexts().CreateDbContextAsync();
        (await db.McpOAuthTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_revocation_endpoint_revokes_the_grant_for_a_refresh_token()
    {
        var (access, refresh) = await ConnectClaudeAsync("read");

        var revoke = await _public.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refresh,
            ["token_type_hint"] = "refresh_token",
        }));
        var unknown = await _public.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = "nsr_" + new string('Q', 43),
        }));

        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListToolsRawAsync(_public, access)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("password", "unsupported_grant_type")]
    [InlineData(null, "invalid_request")]
    public async Task Other_grant_types_are_refused(string? grantType, string error)
    {
        var form = new Dictionary<string, string> { ["client_id"] = McpOAuthFactory.ClaudeClientId };
        if (grantType is not null)
            form["grant_type"] = grantType;

        await ExpectOAuthErrorAsync(await TokenAsync(form), error);
    }

    // --- the public host ----------------------------------------------

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/index.html")]
    [InlineData("GET", "/library")]
    [InlineData("GET", "/settings")]
    [InlineData("GET", "/api/books")]
    [InlineData("GET", "/api/runtime/capabilities")]
    [InlineData("GET", "/api/mcp/apps")]
    [InlineData("POST", "/api/mcp/tokens")]
    [InlineData("GET", "/api/mcp/oauth")]
    [InlineData("POST", "/api/mcp/oauth/requests/abc/approve")]
    [InlineData("POST", "/api/mcp/oauth/requests/abc/deny")]
    [InlineData("GET", "/opds")]
    [InlineData("GET", "/opds/")]
    [InlineData("GET", "/health/live")]
    [InlineData("GET", "/openapi/v1.json")]
    [InlineData("GET", "/mcp/../api/books")]
    [InlineData("GET", "/mcpx")]
    [InlineData("GET", "/oauth/other")]
    [InlineData("GET", "/.well-known/openid-configuration")]
    public async Task Everything_outside_the_allowlist_is_404_on_the_public_host(string method, string path)
    {
        var response = await _public.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { scope = "read_write", name = "x" }) : null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(LibraryEndpointFactory.SpaShellMarker);
    }

    [Theory]
    [InlineData("X-Forwarded-Host", "nostos.example.ts.net")]
    [InlineData("X-Forwarded-Host", "NOSTOS.example.ts.net:443")]
    [InlineData("Tailscale-Funnel-Request", "?1")]
    public async Task A_request_forwarded_for_the_public_host_or_through_funnel_is_public_too(string header, string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/books");
        request.Headers.TryAddWithoutValidation(header, value);

        (await _local.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _local.GetAsync("/api/books")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_allowed_paths_answer_on_the_public_host()
    {
        (await _public.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _public.GetAsync("/.well-known/oauth-authorization-server")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _public.GetAsync(AuthorizeUrl(Challenge(Verifier())))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _public.GetAsync("/oauth/authorize/status?request=nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ListToolsRawAsync(_public, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void The_owner_routes_recognise_the_public_host_in_every_form()
    {
        var settings = McpOAuthSettings.From(McpOAuthFactory.PublicBase);
        HttpContext Request(string host, params (string Name, string Value)[] headers)
        {
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString(host);
            foreach (var (name, value) in headers)
                context.Request.Headers[name] = value;
            return context;
        }

        McpPublicHost.IsPublicRequest(Request("nostos.example.ts.net"), settings).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("Nostos.Example.TS.net:443"), settings).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("nostos.example.ts.net."), settings).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("nostos.example.ts.net:8443"), settings).Should().BeFalse();
        McpPublicHost.IsPublicRequest(Request("nostos.example.ts.net:8443"), McpOAuthSettings.From(McpOAuthFactory.PublicBase + ":8443")).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("127.0.0.1:5214", ("X-Original-Host", "nostos.example.ts.net")), settings).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("127.0.0.1:5214", ("X-Forwarded-Host", "a.example, nostos.example.ts.net")), settings).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("192.168.1.20:5214", ("Tailscale-Funnel-Request", "?1")), settings).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("192.168.1.20:5214", ("Tailscale-Funnel-Request", "?1")), McpOAuthSettings.Disabled).Should().BeTrue();
        McpPublicHost.IsPublicRequest(Request("192.168.1.20:5214"), settings).Should().BeFalse();
        McpPublicHost.IsPublicRequest(Request("nostos.example.ts.net.evil.example"), settings).Should().BeFalse();
        McpPublicHost.IsPublicRequest(Request("nostos.example.ts.net"), McpOAuthSettings.Disabled).Should().BeFalse();
    }

    [Fact]
    public async Task Approve_and_deny_need_a_json_body_from_this_site()
    {
        await AuthorizeAsync(Challenge(Verifier()));
        var id = (await PendingAsync()).Single().GetProperty("id").GetString();
        var approve = $"/api/mcp/oauth/requests/{id}/approve";

        // A cross-site page can send these without a CORS preflight.
        (await _local.PostAsync(approve, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // A form or text body fails JSON binding first (415): refused either way.
        (await _local.PostAsync(approve, new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = "read" })))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.UnsupportedMediaType);
        (await _local.PostAsync(approve, new StringContent("{\"scope\":\"read\"}", Encoding.UTF8, "text/plain")))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.UnsupportedMediaType);
        (await _local.PostAsync($"/api/mcp/oauth/requests/{id}/deny", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var crossSite = new HttpRequestMessage(HttpMethod.Post, approve) { Content = JsonContent.Create(new { scope = "read" }) };
        crossSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        (await _local.SendAsync(crossSite)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PendingAsync()).Should().ContainSingle("nothing above approved the request");

        var sameSite = new HttpRequestMessage(HttpMethod.Post, approve) { Content = JsonContent.Create(new { scope = "read" }) };
        sameSite.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        (await _local.SendAsync(sameSite)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData(null, null, null, null, true)]                                      // direct, any browser
    [InlineData("same-origin", null, null, null, true)]
    [InlineData("none", null, null, null, true)]
    [InlineData("cross-site", null, null, null, false)]
    [InlineData("same-site", null, null, null, false)]
    [InlineData(null, "http://evil.example", null, null, false)]                     // no Sec-Fetch-Site: Origin decides
    [InlineData(null, "http://nas.local:5214", null, null, true)]
    [InlineData(null, null, "100.101.102.103", "127.0.0.1", true)]                  // Tailscale serve, applied
    [InlineData(null, null, "203.0.113.9, 8.8.8.8", "127.0.0.1", false)]            // proxied from the internet
    [InlineData(null, null, null, "8.8.8.8", true)]                                  // direct: address not checked
    [InlineData(null, null, "192.168.1.4", "172.17.0.1", true)]                      // proxy Nostos does not trust, private
    [InlineData(null, null, "8.8.4.4", "172.17.0.1", false)]                         // same, public caller
    public void An_owner_decision_comes_from_this_site_and_the_owners_network(
        string? fetchSite, string? origin, string? forwardedFor, string? remote, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Host = new HostString("nas.local:5214");
        context.Request.ContentType = "application/json";
        if (fetchSite is not null)
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        if (forwardedFor is not null)
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (remote is not null)
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);

        McpOAuthEndpoints.IsOwnerDecision(context).Should().Be(allowed);
    }

    [Fact]
    public async Task The_authorize_endpoint_is_rate_limited_per_address()
    {
        var url = AuthorizeUrl(Challenge(Verifier()), clientId: "https://unknown.example/client.json");
        for (var i = 0; i < McpOAuthRateLimiter.PermitsPerMinute(McpOAuthRateLimiter.Endpoint.Authorize); i++)
            (await _public.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _public.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://nostos.example.ts.net", false)]
    [InlineData("https://nostos.example.ts.net/nostos", false)]
    [InlineData("https://nostos.example.ts.net?x=1", false)]
    [InlineData("nostos.example.ts.net", false)]
    [InlineData("https://nostos.example.ts.net", true)]
    [InlineData("https://Nostos.Example.ts.net/", true)]
    [InlineData("https://nostos.example.ts.net:8443", true)]
    public void Oauth_is_on_only_for_an_https_base_url_without_a_path(string? value, bool enabled)
    {
        var settings = McpOAuthSettings.From(value);

        settings.Enabled.Should().Be(enabled);
        if (enabled)
        {
            settings.Resource.Should().Be(settings.Issuer + "/mcp");
            settings.Issuer.Should().NotEndWith("/");
        }
        else if (!string.IsNullOrEmpty(value))
        {
            settings.Problem.Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData(null, "read_write")]
    [InlineData("nostos:read nostos:write", "read_write")]
    [InlineData("nostos:read", "read")]
    [InlineData("nostos:write", "read_write")]
    [InlineData("nostos:read offline_access", "read")]
    [InlineData("openid", null)]
    public void The_requested_scope_maps_to_what_settings_offers(string? scope, string? expected) =>
        McpOAuthEndpoints.ParseScope(scope).Should().Be(expected);

    // ------------------------------------------------------------------

    private async Task<(string Access, string Refresh)> ConnectClaudeAsync(string approveScope)
    {
        var verifier = Verifier();
        var code = await ApprovedCodeAsync(Challenge(verifier), approveScope);
        var response = await RedeemAsync(code, verifier);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("access_token").GetString()!, body.GetProperty("refresh_token").GetString()!);
    }

    private async Task<string> ApprovedCodeAsync(string challenge, string approveScope)
    {
        var html = await (await AuthorizeAsync(challenge)).Content.ReadAsStringAsync();
        var requestId = RequestId(html);
        var id = (await PendingAsync()).Single().GetProperty("id").GetString();
        (await _local.PostAsJsonAsync($"/api/mcp/oauth/requests/{id}/approve", new { scope = approveScope })).EnsureSuccessStatusCode();
        var redirect = new Uri((await PollAsync(requestId)).GetProperty("redirect").GetString()!);
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(redirect.Query)["code"].ToString();
    }

    private Task<HttpResponseMessage> RedeemAsync(string code, string verifier, string? redirectUri = null, string? clientId = null) =>
        TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri ?? McpOAuthFactory.ClaudeRedirect,
            ["client_id"] = clientId ?? McpOAuthFactory.ClaudeClientId,
            ["code_verifier"] = verifier,
        });

    private Task<HttpResponseMessage> RefreshAsync(string refresh, string? clientId = null) =>
        TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = clientId ?? McpOAuthFactory.ClaudeClientId,
        });

    private Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form) =>
        _public.PostAsync("/oauth/token", new FormUrlEncodedContent(form));

    private static async Task ExpectOAuthErrorAsync(HttpResponseMessage response, string error)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be(error);
        body.GetProperty("error_description").GetString().Should().NotBeNullOrEmpty();
    }

    private Task<HttpResponseMessage> AuthorizeAsync(string challenge, string? state = null) =>
        _public.GetAsync(AuthorizeUrl(challenge, state: state));

    private static string AuthorizeUrl(string challenge, string? redirectUri = null, string? clientId = null, string? state = null) =>
        "/oauth/authorize?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(clientId ?? McpOAuthFactory.ClaudeClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri ?? McpOAuthFactory.ClaudeRedirect)
        + "&code_challenge=" + challenge
        + "&code_challenge_method=S256"
        + "&scope=" + Uri.EscapeDataString("nostos:read nostos:write")
        + "&resource=" + Uri.EscapeDataString(Resource)
        + (state is null ? "" : "&state=" + Uri.EscapeDataString(state));

    private async Task<List<JsonElement>> PendingAsync()
    {
        var status = await _local.GetFromJsonAsync<JsonElement>("/api/mcp/oauth");
        status.GetProperty("enabled").GetBoolean().Should().BeTrue();
        status.GetProperty("connectorUrl").GetString().Should().Be(Resource);
        return status.GetProperty("requests").EnumerateArray().ToList();
    }

    private async Task<JsonElement> PollAsync(string requestId)
    {
        var response = await _public.GetAsync($"/oauth/authorize/status?request={Uri.EscapeDataString(requestId)}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string RequestId(string html)
    {
        var match = Regex.Match(html, "data-status=\"[^\"]*request=([^\"&]+)\"");
        match.Success.Should().BeTrue("the waiting page names its status URL");
        return Uri.UnescapeDataString(System.Net.WebUtility.HtmlDecode(match.Groups[1].Value));
    }

    private static string Verifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string Challenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static IEnumerable<string?> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString());

    private async Task<McpClient> ConnectAsync(string token)
    {
        var authed = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(McpOAuthFactory.PublicBase) });
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

    private static Task<HttpResponseMessage> ListToolsRawAsync(HttpClient http, string? token)
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
        return http.SendAsync(request);
    }

    private async Task<Guid> CreateBookAsync(string title)
    {
        var response = await _local.PostAsJsonAsync("/api/books", new { type = "physical", title, author = "Someone" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private IDbContextFactory<NostosDbContext> Contexts() =>
        _factory.Services.GetRequiredService<IDbContextFactory<NostosDbContext>>();

    private async Task AgeSpentRefreshTokensAsync(TimeSpan by)
    {
        var at = DateTime.UtcNow - by;
        await using var db = await Contexts().CreateDbContextAsync();
        await db.McpOAuthTokens.Where(t => t.ConsumedAtUtc != null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.ConsumedAtUtc, at));
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

/// <summary>OAuth off: the SelfHosted host as it ships without <c>Mcp:PublicBaseUrl</c>.</summary>
public sealed class McpOAuthOffTests : IAsyncLifetime
{
    private readonly McpAccessTokenFactory _factory = new();
    private HttpClient _http = null!;

    public Task InitializeAsync()
    {
        _http = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Without_a_public_base_url_there_is_no_oauth_and_the_challenge_stays_plain()
    {
        (await _http.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.GetAsync("/.well-known/oauth-authorization-server")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.GetAsync("/oauth/authorize")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _http.PostAsync("/oauth/token", new FormUrlEncodedContent([]))).StatusCode.Should().NotBe(HttpStatusCode.OK);

        var status = await _http.GetFromJsonAsync<JsonElement>("/api/mcp/oauth");
        status.GetProperty("enabled").GetBoolean().Should().BeFalse();
        status.GetProperty("requests").GetArrayLength().Should().Be(0);

        var mcp = await _http.PostAsync(NostosMcp.Path, new StringContent("{}", Encoding.UTF8, "application/json"));
        mcp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mcp.Headers.WwwAuthenticate.ToString().Should().NotContain("resource_metadata");
    }

    [Fact]
    public async Task An_oauth_access_token_made_while_oauth_was_on_stops_working_when_it_is_turned_off()
    {
        var oauth = _factory.Services.GetRequiredService<McpOAuthService>();
        var tokens = await oauth.CreateGrantAsync(
            new McpOAuthCodeGrant(McpOAuthFactory.ClaudeClientId, "Claude", McpOAuthFactory.ClaudeRedirect, "unused", "read"),
            Guid.NewGuid());
        (await oauth.AuthenticateAsync(tokens!.AccessToken)).Should().NotBeNull("the token itself is live");

        var request = new HttpRequestMessage(HttpMethod.Post, NostosMcp.Path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        (await _http.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

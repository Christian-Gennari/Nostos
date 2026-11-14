using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services.Mcp.OAuth;
using Xunit;
using Nostos.Backend.Tests.Support;

namespace Nostos.Backend.Tests.Mcp;

/// <summary>
/// CIMD fetch limits (issue #1470). The address filter and the client id
/// checks run on real address parsing and real DNS (for <c>localhost</c>);
/// only documents that would need the internet come from a stand-in transport.
/// </summary>
public sealed class McpClientMetadataFetcherTests
{
    private const string ClientId = "https://client.example/oauth/metadata.json";

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.5.6.7", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.101.102.103", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd7a:115c:a1e0::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2002:a00:1::", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:0:4136:e378::1", false)]
    public void Only_public_unicast_addresses_pass(string address, bool expected) =>
        McpNetworkAddressFilter.IsPublic(IPAddress.Parse(address)).Should().Be(expected);

    [Theory]
    [InlineData("http://client.example/metadata.json")]
    [InlineData("ftp://client.example/metadata.json")]
    [InlineData("https://client.example/")]
    [InlineData("https://user:pass@client.example/metadata.json")]
    [InlineData("https://client.example/metadata.json#x")]
    [InlineData("client.example/metadata.json")]
    [InlineData("")]
    public async Task A_client_id_that_is_not_an_https_document_url_is_refused_without_a_request(string clientId)
    {
        var transport = new StubTransport(_ => Json(Document(clientId)));
        using var fetcher = new McpClientMetadataFetcher(transport, PublicDns, null);

        var result = await fetcher.FetchAsync(clientId, CancellationToken.None);

        result.Metadata.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
        transport.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://localhost/oauth/metadata.json")]
    [InlineData("https://127.0.0.1/oauth/metadata.json")]
    [InlineData("https://[::1]/oauth/metadata.json")]
    [InlineData("https://10.0.0.8/oauth/metadata.json")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://100.100.100.100/oauth/metadata.json")]
    public async Task The_real_fetcher_refuses_private_targets_including_after_DNS(string clientId)
    {
        // The production fetcher: real DNS for localhost, real sockets, no stand-ins.
        using var fetcher = new McpClientMetadataFetcher();

        var result = await fetcher.FetchAsync(clientId, CancellationToken.None);

        result.Metadata.Should().BeNull();
        result.Error.Should().Contain("private network");
    }

    [Fact]
    public async Task A_host_whose_DNS_answer_is_private_is_refused_before_any_request()
    {
        var transport = new StubTransport(_ => Json(Document(ClientId)));
        using var fetcher = new McpClientMetadataFetcher(transport, (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }), null);

        var result = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        result.Error.Should().Contain("private network");
        transport.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_with_one_private_address_among_public_ones_is_refused()
    {
        var transport = new StubTransport(_ => Json(Document(ClientId)));
        using var fetcher = new McpClientMetadataFetcher(transport,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("127.0.0.1") }), null);

        (await fetcher.FetchAsync(ClientId, CancellationToken.None)).Error.Should().Contain("private network");
        transport.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_redirect_to_plain_http_is_refused()
    {
        var transport = new StubTransport(_ => Redirect("http://client.example/plain.json"));
        using var fetcher = new McpClientMetadataFetcher(transport, PublicDns, null);

        var result = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        result.Error.Should().Contain("https");
        transport.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_redirect_to_a_host_that_resolves_privately_is_refused()
    {
        var transport = new StubTransport(request => request.RequestUri!.Host == "client.example"
            ? Redirect("https://inner.example/metadata.json")
            : Json(Document(ClientId)));
        using var fetcher = new McpClientMetadataFetcher(transport,
            (host, _) => Task.FromResult(new[] { IPAddress.Parse(host == "inner.example" ? "192.168.1.5" : "93.184.216.34") }), null);

        var result = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        result.Error.Should().Contain("private network");
        transport.Requests.Select(r => r.Host).Should().Equal("client.example");
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_an_error_not_a_crash()
    {
        var transport = new StubTransport(_ => Json(Document(ClientId)));
        using var fetcher = new McpClientMetadataFetcher(transport,
            (_, _) => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound), null);

        var result = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        result.Metadata.Should().BeNull();
        result.Error.Should().Contain("could not be loaded");
        transport.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_redirect_to_a_private_address_literal_is_refused()
    {
        var transport = new StubTransport(_ => Redirect("https://127.0.0.1:8443/metadata.json"));
        using var fetcher = new McpClientMetadataFetcher(transport, PublicDns, null);

        (await fetcher.FetchAsync(ClientId, CancellationToken.None)).Error.Should().Contain("private network");
        transport.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Too_many_redirects_are_refused()
    {
        var transport = new StubTransport(request => Redirect($"https://client.example/hop{transportCount++}"));
        using var fetcher = new McpClientMetadataFetcher(transport, PublicDns, null);

        (await fetcher.FetchAsync(ClientId, CancellationToken.None)).Error.Should().Contain("redirects");
        transport.Requests.Should().HaveCount(McpClientMetadataFetcher.MaxRedirects + 1);
    }

    private int transportCount;

    [Fact]
    public async Task A_document_over_16_KiB_is_refused_with_or_without_a_length()
    {
        var big = Document(ClientId).Replace("\"Claude\"", "\"" + new string('a', 20_000) + "\"");
        var declared = new StubTransport(_ => Json(big));
        var streamed = new StubTransport(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnseekableStream(Encoding.UTF8.GetBytes(big))),
        });
        using var a = new McpClientMetadataFetcher(declared, PublicDns, null);
        using var b = new McpClientMetadataFetcher(streamed, PublicDns, null);

        (await a.FetchAsync(ClientId, CancellationToken.None)).Error.Should().Contain("16 KiB");
        (await b.FetchAsync(ClientId, CancellationToken.None)).Error.Should().Contain("16 KiB");
    }

    [Fact]
    public async Task A_slow_document_is_given_up_on_after_five_seconds()
    {
        var transport = new StubTransport(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(Document(ClientId));
        });
        using var fetcher = new McpClientMetadataFetcher(transport, PublicDns, null);
        var watch = Stopwatch.StartNew();

        var result = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        result.Error.Should().Contain("5 seconds");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData("""{"client_id":"https://other.example/metadata.json","client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback"]}""", "different client id")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json/","client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback"]}""", "different client id")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json","redirect_uris":["https://claude.ai/api/mcp/auth_callback"]}""", "name")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json","client_name":"Claude","redirect_uris":[]}""", "redirect")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json","client_name":"Claude","redirect_uris":["http://claude.ai/callback"]}""", "redirect")]
    [InlineData("""{"client_id":"https://client.example/oauth/metadata.json","client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"token_endpoint_auth_method":"client_secret_basic"}""", "client secret")]
    [InlineData("""not json""", "JSON")]
    [InlineData("""["https://client.example/oauth/metadata.json"]""", "object")]
    public async Task A_document_that_does_not_check_out_is_refused(string json, string reason)
    {
        using var fetcher = new McpClientMetadataFetcher(new StubTransport(_ => Json(json)), PublicDns, null);

        var result = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        result.Metadata.Should().BeNull();
        result.Error.Should().Contain(reason);
    }

    [Fact]
    public async Task A_good_document_is_read_and_kept_for_ten_minutes()
    {
        var transport = new StubTransport(_ => Json(Document(ClientId)));
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-11-07T10:00:00Z"));
        using var fetcher = new McpClientMetadataFetcher(transport, PublicDns, clock);

        var first = await fetcher.FetchAsync(ClientId, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(9));
        var cached = await fetcher.FetchAsync(ClientId, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        await fetcher.FetchAsync(ClientId, CancellationToken.None);

        first.Metadata!.ClientName.Should().Be("Claude");
        first.Metadata.ClientHost.Should().Be("client.example");
        first.Metadata.RedirectUris.Should().Equal("https://claude.ai/api/mcp/auth_callback", "http://127.0.0.1:33418/callback");
        cached.Metadata.Should().Be(first.Metadata);
        transport.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("https://claude.ai/api/mcp/auth_callback/", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback?x=1", false)]
    [InlineData("https://evil.example/api/mcp/auth_callback", false)]
    [InlineData("http://127.0.0.1:33418/callback", true)]
    [InlineData("http://127.0.0.1:50000/callback", true)]
    [InlineData("http://127.0.0.1:50000/other", false)]
    [InlineData("http://localhost:50000/callback", false)]
    [InlineData("https://127.0.0.1:50000/callback", false)]
    public void A_redirect_uri_must_be_listed_and_loopback_may_change_port(string redirectUri, bool allowed)
    {
        var metadata = new McpClientMetadata(ClientId, "Claude",
            ["https://claude.ai/api/mcp/auth_callback", "http://127.0.0.1:33418/callback"]);

        metadata.AllowsRedirect(redirectUri).Should().Be(allowed);
    }

    // ------------------------------------------------------------------

    private static Task<IPAddress[]> PublicDns(string host, CancellationToken ct) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static string Document(string clientId) =>
        $$"""{"client_id":"{{clientId}}","client_name":"Claude","redirect_uris":["https://claude.ai/api/mcp/auth_callback","http://127.0.0.1:33418/callback"],"token_endpoint_auth_method":"none"}""";

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class StubTransport : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = (request, _) => Task.FromResult(respond(request));

        public StubTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            _respond = respond;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return _respond(request, cancellationToken);
        }
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

}

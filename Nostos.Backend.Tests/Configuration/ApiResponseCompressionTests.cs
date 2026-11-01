using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

/// <summary>
/// API JSON is compressed and nothing else is. Streams have to reach the client
/// line by line and byte-range responses have to stay addressable.
/// </summary>
public sealed class ApiResponseCompressionTests : IClassFixture<LibraryEndpointFactory>, IAsyncLifetime
{
    private static readonly string Body = "{\"title\":\"" + string.Concat(Enumerable.Repeat("nostos ", 400)) + "\"}";

    private readonly LibraryEndpointFactory _factory;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public ApiResponseCompressionTests(LibraryEndpointFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiResponseCompression();
        _app = builder.Build();
        _app.UseResponseCompression();
        Map("/api/json", "application/json");
        Map("/api/problem", "application/problem+json");
        Map("/api/events", "text/event-stream");
        Map("/api/ndjson", "application/x-ndjson; charset=utf-8");
        Map("/api/book", "application/epub+zip");
        Map("/api/audio", "audio/mpeg");
        Map("/api/cover", "image/webp");
        Map("/opds/json", "application/json");
        _app.MapGet("/api/rangeable", (HttpContext context) =>
        {
            context.Response.Headers.AcceptRanges = "bytes";
            return Results.Text(Body, "application/json");
        });
        _app.MapGet("/api/partial", (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = "bytes 0-9/100";
            return context.Response.WriteAsync(Body, new UTF8Encoding(false), context.RequestAborted);
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/api/json", "br, gzip")]
    [InlineData("/api/json", "gzip")]
    [InlineData("/api/problem", "gzip")]
    public async Task Compresses_api_json(string path, string acceptEncoding)
    {
        using var response = await SendAsync(path, acceptEncoding);

        response.Content.Headers.ContentEncoding.Should().Equal("gzip");
        response.Headers.Vary.Should().Contain("Accept-Encoding");
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().BeLessThan(Body.Length / 4);
    }

    [Theory]
    [InlineData("/api/events")]
    [InlineData("/api/ndjson")]
    [InlineData("/api/book")]
    [InlineData("/api/audio")]
    [InlineData("/api/cover")]
    [InlineData("/api/rangeable")]
    [InlineData("/api/partial")]
    [InlineData("/opds/json")]
    public async Task Leaves_everything_else_alone(string path)
    {
        using var response = await SendAsync(path, "br, gzip");

        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        (await response.Content.ReadAsStringAsync()).Should().Be(Body);
    }

    [Fact]
    public async Task A_range_request_is_never_compressed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/json");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        request.Headers.Range = new RangeHeaderValue(0, 9);

        using var response = await _client.SendAsync(request);

        response.Content.Headers.ContentEncoding.Should().BeEmpty();
    }

    [Fact]
    public async Task The_real_host_compresses_json_and_not_the_change_feed()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");

        using var json = await client.GetAsync("/api/collections");
        json.EnsureSuccessStatusCode();
        json.Content.Headers.ContentEncoding.Should().Equal("gzip");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/library/changes");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var feed = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        feed.StatusCode.Should().Be(HttpStatusCode.OK);
        feed.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        feed.Content.Headers.ContentEncoding.Should().BeEmpty();
    }

    private void Map(string path, string contentType) =>
        _app.MapGet(path, () => Results.Text(Body, contentType));

    private async Task<HttpResponseMessage> SendAsync(string path, string acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await _client.SendAsync(request);
    }
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Pins the Server-Sent Events contract shared by <c>GET /api/imports/stream</c>
/// and <c>GET /api/library/changes</c>: the exact response headers a proxy has
/// to see, and the exact bytes of the first frames.
///
/// Both routes move to one shared stream helper in issue #1632. Moving them
/// must not change a header (a dropped <c>X-Accel-Buffering</c> freezes the UI
/// behind nginx until the stream ends) and must not change a frame (the client
/// parses these literally). Reading a slice rather than the whole body also
/// proves the frames leave the process instead of sitting in a buffer.
/// </summary>
public sealed class EndpointStreamingContractTests : IClassFixture<LibraryEndpointFactory>
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly LibraryEndpointFactory _factory;

    public EndpointStreamingContractTests(LibraryEndpointFactory factory) => _factory = factory;

    [Fact]
    public async Task Import_stream_sets_the_proxy_safe_headers_and_opens_with_the_current_list()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/imports/stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        AssertStreamHeaders(response);

        // The library is empty in this fixture, and an empty list is still an
        // answer: a client that connects while nothing runs must not sit in
        // silence until the first heartbeat.
        var body = await ReadPrefixAsync(response, Budget);
        body.Should().StartWith("event: progress\ndata: []\n\n");
    }

    [Fact]
    public async Task Library_changes_stream_sets_the_proxy_safe_headers_and_opens_with_retry_then_the_revision()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/library/changes");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        AssertStreamHeaders(response);

        var body = await ReadPrefixAsync(response, Budget);
        body.Should().MatchRegex(
            @"\Aretry: 2000\n\nevent: library\ndata: \{""stateVersion"":""[0-9]+""\}\n\n");
    }

    private static void AssertStreamHeaders(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // No charset on the content type: EventSource compares it literally.
        response.Content.Headers.ContentType!.ToString().Should().Be("text/event-stream");

        // no-cache stops an intermediary serving a stale copy of a live feed;
        // X-Accel-Buffering: no is nginx's documented opt-out from buffering.
        response.Headers.CacheControl!.ToString().Should().Be("no-cache");
        response.Headers.GetValues("X-Accel-Buffering").Should().ContainSingle().Which.Should().Be("no");

        // A stream has no length: anything here would make a proxy wait for it.
        response.Content.Headers.ContentLength.Should().BeNull();
    }

    private static async Task<string> ReadPrefixAsync(HttpResponseMessage response, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var text = new StringBuilder();
        var buffer = new char[2048];

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer, cts.Token);
                if (read <= 0)
                    break;

                text.Append(buffer, 0, read);

                // Two frames are enough for both routes; stop rather than wait
                // out the budget against a stream that stays open on purpose.
                if (text.ToString().Count(c => c == '\n') >= 4)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The read budget elapsed; whatever arrived is what we assert on.
        }

        return text.ToString();
    }
}

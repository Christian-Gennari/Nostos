using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class LoopbackRangeServerTests
{
    private static readonly byte[] Content =
        Enumerable.Range(0, 100)
            .Select(value => (byte)value)
            .ToArray();

    private static async Task<LoopbackRangeServer> StartAsync(
        Action<StorageByteRange>? onOpen = null)
    {
        return await LoopbackRangeServer.StartAsync(
            Content.Length,
            (range, _) =>
            {
                onOpen?.Invoke(range);

                var bytes = Content
                    .AsSpan(
                        checked((int)range.Start),
                        checked((int)range.Length))
                    .ToArray();

                StoredAssetRead result = new(
                    new StoredAssetInfo(
                        "audio.m4b",
                        "audio/mp4",
                        Content.Length,
                        "\"revision-1\"",
                        DateTimeOffset.UtcNow),
                    new MemoryStream(bytes),
                    range);

                return Task.FromResult<StoredAssetRead?>(result);
            });
    }

    [Fact]
    public async Task Full_GET_returns_exact_content_and_range_headers()
    {
        StorageByteRange? opened = null;

        await using var server = await StartAsync(
            range => opened = range);

        using var client = new HttpClient();

        using var response = await client.GetAsync(server.Url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response.Content.Headers.ContentLength
            .Should().Be(Content.Length);

        response.Headers.AcceptRanges
            .Should().Contain("bytes");

        (await response.Content.ReadAsByteArrayAsync())
            .Should().Equal(Content);

        opened.Should().Be(
            new StorageByteRange(0, Content.Length - 1));
    }

    [Fact]
    public async Task Bounded_range_returns_206_and_only_requested_bytes()
    {
        StorageByteRange? opened = null;

        await using var server = await StartAsync(
            range => opened = range);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, server.Url);

        request.Headers.Range =
            new RangeHeaderValue(10, 19);

        using var response = await client.SendAsync(request);

        response.StatusCode
            .Should().Be(HttpStatusCode.PartialContent);

        response.Content.Headers.ContentLength
            .Should().Be(10);

        response.Content.Headers.ContentRange
            .Should().BeEquivalentTo(
                new ContentRangeHeaderValue(10, 19, 100));

        (await response.Content.ReadAsByteArrayAsync())
            .Should().Equal(Content.Skip(10).Take(10));

        opened.Should().Be(new StorageByteRange(10, 19));
    }

    [Fact]
    public async Task Suffix_range_returns_last_requested_bytes()
    {
        await using var server = await StartAsync();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, server.Url);

        request.Headers.Range = new RangeHeaderValue();
        request.Headers.Range.Ranges.Add(
            new RangeItemHeaderValue(null, 5));

        using var response = await client.SendAsync(request);

        response.StatusCode
            .Should().Be(HttpStatusCode.PartialContent);

        (await response.Content.ReadAsByteArrayAsync())
            .Should().Equal(Content.TakeLast(5));

        response.Content.Headers.ContentRange!.From
            .Should().Be(95);

        response.Content.Headers.ContentRange!.To
            .Should().Be(99);
    }

    [Fact]
    public async Task Open_ended_range_returns_remainder()
    {
        await using var server = await StartAsync();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, server.Url);

        request.Headers.Range =
            new RangeHeaderValue(90, null);

        using var response = await client.SendAsync(request);

        response.StatusCode
            .Should().Be(HttpStatusCode.PartialContent);

        (await response.Content.ReadAsByteArrayAsync())
            .Should().Equal(Content.Skip(90));
    }

    [Fact]
    public async Task HEAD_returns_metadata_without_opening_storage()
    {
        var openCount = 0;

        await using var server = await StartAsync(
            _ => openCount++);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Head, server.Url);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response.Content.Headers.ContentLength
            .Should().Be(Content.Length);

        openCount.Should().Be(0);
    }

    [Theory]
    [InlineData("bytes=100-")]
    [InlineData("bytes=200-300")]
    [InlineData("bytes=20-10")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=0-1,3-4")]
    [InlineData("bytes=garbage")]
    [InlineData("items=0-10")]
    public async Task Invalid_ranges_return_416_without_storage_access(
        string value)
    {
        var openCount = 0;

        await using var server = await StartAsync(
            _ => openCount++);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, server.Url);

        request.Headers.TryAddWithoutValidation("Range", value);

        using var response = await client.SendAsync(request);

        response.StatusCode
            .Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);

        response.Content.Headers.ContentRange!.Length
            .Should().Be(100);

        openCount.Should().Be(0);
    }

    [Fact]
    public async Task Incorrect_path_cannot_access_resource()
    {
        var openCount = 0;

        await using var server = await StartAsync(
            _ => openCount++);

        using var client = new HttpClient();

        var wrong = new Uri(
            server.Url.GetLeftPart(UriPartial.Authority) +
            "/wrong-token");

        using var response = await client.GetAsync(wrong);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        openCount.Should().Be(0);
    }

    [Fact]
    public async Task POST_is_rejected_without_opening_storage()
    {
        var openCount = 0;

        await using var server = await StartAsync(
            _ => openCount++);

        using var client = new HttpClient();

        using var response = await client.PostAsync(
            server.Url, new ByteArrayContent([1]));

        response.StatusCode
            .Should().Be(HttpStatusCode.MethodNotAllowed);

        openCount.Should().Be(0);
    }

    [Fact]
    public async Task Resource_token_is_random_for_each_server()
    {
        await using var first = await StartAsync();
        await using var second = await StartAsync();

        first.Url.AbsolutePath.Should()
            .NotBe(second.Url.AbsolutePath);

        first.Url.Host.Should().Be("127.0.0.1");
        second.Url.Host.Should().Be("127.0.0.1");
    }

    [Fact]
    public async Task Server_is_unreachable_after_disposal()
    {
        var server = await StartAsync();
        var url = server.Url;

        await server.DisposeAsync();

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

        Func<Task> action = async () =>
            await client.GetAsync(url);

        await action.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData(null, 100, 0, 99, true)]
    [InlineData("bytes=0-9", 100, 0, 9, true)]
    [InlineData("bytes=90-", 100, 90, 99, true)]
    [InlineData("bytes=-5", 100, 95, 99, true)]
    [InlineData("bytes=-500", 100, 0, 99, true)]
    [InlineData("bytes=0-999", 100, 0, 99, true)]
    [InlineData("bytes=100-", 100, 0, 0, false)]
    [InlineData("bytes=10-5", 100, 0, 0, false)]
    [InlineData("bytes=-0", 100, 0, 0, false)]
    [InlineData("bytes=0-1,4-5", 100, 0, 0, false)]
    public void Range_parser_handles_boundaries(
        string? header,
        long length,
        long start,
        long end,
        bool expected)
    {
        var parsed = LoopbackRangeServer.TryParseRange(
            header, length, out var range);

        parsed.Should().Be(expected);

        if (expected)
        {
            range.Start.Should().Be(start);
            range.EndInclusive.Should().Be(end);
        }
    }
}

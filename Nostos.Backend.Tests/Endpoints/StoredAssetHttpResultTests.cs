using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class StoredAssetHttpResultTests
{
    [Fact]
    public async Task Single_range_returns_206_and_exact_requested_bytes()
    {
        var bytes = Encoding.ASCII.GetBytes("0123456789");
        var context = Context();
        context.Request.Headers.Range = "bytes=2-5";

        var result = await StoredAssetHttpResult.CreateAsync(
            context,
            _ => Task.FromResult<StoredAssetInfo?>(Info(bytes.Length)),
            (range, _) => Task.FromResult<StoredAssetRead?>(
                Open(bytes, range)),
            attachment: false,
            enableRanges: true,
            cacheControl: "private, max-age=300",
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes 2-5/10");
        context.Response.Headers.AcceptRanges.ToString().Should().Be("bytes");
        context.Response.Headers.ETag.ToString().Should().Be("\"etag-0\"");
        context.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=300");
        context.Response.ContentLength.Should().Be(4);
        Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray())
            .Should().Be("2345");
    }

    [Theory]
    [InlineData("bytes=8-", "89")]
    [InlineData("bytes=-3", "789")]
    public async Task Open_ended_and_suffix_ranges_are_supported(
        string rangeHeader,
        string expected)
    {
        var bytes = Encoding.ASCII.GetBytes("0123456789");
        var context = Context();
        context.Request.Headers.Range = rangeHeader;

        var result = await StoredAssetHttpResult.CreateAsync(
            context,
            _ => Task.FromResult<StoredAssetInfo?>(Info(bytes.Length)),
            (range, _) => Task.FromResult<StoredAssetRead?>(Open(bytes, range)),
            attachment: false,
            enableRanges: true,
            cacheControl: null,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray())
            .Should().Be(expected);
    }

    [Fact]
    public async Task Unsatisfiable_range_returns_416_without_opening_asset()
    {
        var context = Context();
        context.Request.Headers.Range = "bytes=99-120";
        var opened = false;

        var result = await StoredAssetHttpResult.CreateAsync(
            context,
            _ => Task.FromResult<StoredAssetInfo?>(Info(10)),
            (_, _) =>
            {
                opened = true;
                return Task.FromResult<StoredAssetRead?>(null);
            },
            attachment: false,
            enableRanges: true,
            cacheControl: null,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        opened.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
        context.Response.Headers.ContentRange.ToString().Should().Be("bytes */10");
    }

    [Fact]
    public async Task Matching_if_none_match_returns_304_without_opening_asset()
    {
        var context = Context();
        context.Request.Headers.IfNoneMatch = "\"etag-1\"";
        var opened = false;

        var result = await StoredAssetHttpResult.CreateAsync(
            context,
            _ => Task.FromResult<StoredAssetInfo?>(
                Info(10) with { EntityTag = "\"etag-1\"" }),
            (_, _) =>
            {
                opened = true;
                return Task.FromResult<StoredAssetRead?>(null);
            },
            attachment: false,
            enableRanges: false,
            cacheControl: "private, max-age=300",
            CancellationToken.None);

        await result.ExecuteAsync(context);

        opened.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status304NotModified);
        context.Response.Headers.ETag.ToString().Should().Be("\"etag-1\"");
        context.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=300");
    }

    [Fact]
    public async Task Changed_entity_tag_returns_replacement_bytes_instead_of_304()
    {
        var replacement = Encoding.ASCII.GetBytes("replacement bytes");
        var replacementInfo = Info(replacement.Length) with { EntityTag = "\"etag-new\"" };
        var context = Context();
        context.Request.Headers.IfNoneMatch = "\"etag-old\"";

        var result = await StoredAssetHttpResult.CreateAsync(
            context,
            _ => Task.FromResult<StoredAssetInfo?>(replacementInfo),
            (range, _) => Task.FromResult<StoredAssetRead?>(
                new StoredAssetRead(
                    replacementInfo,
                    new MemoryStream(replacement, writable: false),
                    range)),
            attachment: false,
            enableRanges: true,
            cacheControl: "private, max-age=300",
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.ETag.ToString().Should().Be("\"etag-new\"");
        context.Response.Headers.CacheControl.ToString().Should().Be("private, max-age=300");
        Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray())
            .Should().Be("replacement bytes");
    }

    [Fact]
    public async Task Attachment_sets_safe_content_disposition()
    {
        var bytes = Encoding.ASCII.GetBytes("abc");
        var context = Context();

        var result = await StoredAssetHttpResult.CreateAsync(
            context,
            _ => Task.FromResult<StoredAssetInfo?>(Info(bytes.Length)),
            (range, _) => Task.FromResult<StoredAssetRead?>(Open(bytes, range)),
            attachment: true,
            enableRanges: true,
            cacheControl: null,
            CancellationToken.None);

        await result.ExecuteAsync(context);

        context.Response.Headers.ContentDisposition.ToString()
            .Should().Contain("attachment");
        context.Response.Headers.ContentDisposition.ToString()
            .Should().Contain("book.epub");
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static StoredAssetInfo Info(long length) =>
        new(
            FileName: "book.epub",
            ContentType: "application/epub+zip",
            Length: length,
            EntityTag: "\"etag-0\"",
            LastModified: DateTimeOffset.UtcNow);

    private static StoredAssetRead Open(
        byte[] bytes,
        StorageByteRange? range)
    {
        var stream = new MemoryStream(bytes, writable: false);
        if (range is { } requested)
            stream.Position = requested.Start;

        return new StoredAssetRead(
            Info(bytes.Length),
            stream,
            range);
    }
}

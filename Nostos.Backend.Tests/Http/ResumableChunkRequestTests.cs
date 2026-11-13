using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nostos.Backend.Endpoints;
using Nostos.Product.Http;
using Xunit;

namespace Nostos.Backend.Tests.Http;

/// <summary>
/// The transport half of the resumable chunk contract (#1629). The book-file
/// and Calibre uploads now read a part's headers, cap its body and recognise
/// Kestrel's 413 through one place; each still answers with its own codes.
/// </summary>
public sealed class ResumableChunkRequestTests
{
    private const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_part_headers_are_read_into_a_range_and_a_checksum()
    {
        var request = PartRequest("bytes 1024-2047/4096", Hash);

        var parsed = ResumableChunkRequest.TryRead(request, out var range, out var sha256);

        parsed.Should().BeTrue();
        range.Should().Be(new ResumableChunkRange(1024, 2047, 4096));
        range.Length.Should().Be(1024);
        sha256.Should().Be(Hash);
    }

    [Theory]
    [InlineData(null, Hash, false)]
    [InlineData("", Hash, false)]
    [InlineData("bytes 0-9", Hash, false)]
    [InlineData("bytes 9-0/10", Hash, false)]
    [InlineData("bytes 0-10/10", Hash, false)]
    [InlineData("bytes 0-9/*", Hash, false)]
    [InlineData("bytes 0-9/10", "not-a-checksum", false)]
    [InlineData("bytes 0-9/10", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]
    [InlineData("bytes 0-9/10", Hash, true)]
    public void Only_a_complete_part_header_pair_is_accepted(
        string? contentRange,
        string sha256,
        bool expected)
    {
        var request = PartRequest(contentRange, sha256);

        ResumableChunkRequest.TryRead(request, out _, out _).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, 1024L, false)]
    [InlineData(1024L, 1024L, false)]
    [InlineData(1025L, 1024L, true)]
    public void A_declared_body_is_compared_with_the_part_ceiling(
        long? contentLength,
        long maxBytes,
        bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentLength = contentLength;

        ResumableChunkRequest.DeclaresMoreThan(request, maxBytes).Should().Be(expected);
    }

    [Fact]
    public void The_body_ceiling_is_applied_to_a_writable_feature()
    {
        var request = new DefaultHttpContext().Request;
        var feature = new BodySizeFeature();
        request.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        ResumableChunkRequest.LimitBodySize(request, 4096);

        feature.MaxRequestBodySize.Should().Be(4096);
    }

    [Fact]
    public void A_frozen_body_ceiling_is_left_alone()
    {
        var request = new DefaultHttpContext().Request;
        var feature = new BodySizeFeature { IsReadOnly = true, MaxRequestBodySize = 30_000_000 };
        request.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        ResumableChunkRequest.LimitBodySize(request, 4096);

        feature.MaxRequestBodySize.Should().Be(30_000_000, "Kestrel has already frozen this request");
    }

    [Fact]
    public void Only_a_413_is_a_payload_too_large()
    {
        ResumableChunkRequest
            .IsPayloadTooLarge(new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge))
            .Should().BeTrue();
        ResumableChunkRequest
            .IsPayloadTooLarge(new BadHttpRequestException("bad", StatusCodes.Status400BadRequest))
            .Should().BeFalse();
    }

    private static HttpRequest PartRequest(string? contentRange, string sha256)
    {
        var request = new DefaultHttpContext().Request;
        if (contentRange is not null)
            request.Headers["Content-Range"] = contentRange;
        request.Headers[MigrationChunkHeaders.ChunkHashHeaderName] = sha256;
        return request;
    }

    private sealed class BodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; set; }

        public long? MaxRequestBodySize { get; set; }
    }
}

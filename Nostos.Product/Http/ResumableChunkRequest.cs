using Microsoft.AspNetCore.Http.Features;
using Nostos.Backend.Endpoints;

namespace Nostos.Product.Http;

/// <summary>The byte range one resumable part claims.</summary>
public readonly record struct ResumableChunkRange(long Start, long End, long Total)
{
    /// <summary>How many bytes this part must carry.</summary>
    public long Length => End - Start + 1;
}

/// <summary>
/// The transport half of the resumable chunk contract that the book-file and
/// Calibre library uploads share (#1629): <c>Content-Range</c>,
/// <c>X-Nostos-Chunk-SHA256</c> and the request-body ceiling. Parsing lives
/// here, on top of <see cref="MigrationChunkHeaders"/>; each endpoint keeps its
/// own stable error codes and status codes, because its callers already depend
/// on them.
/// </summary>
public static class ResumableChunkRequest
{
    /// <summary>
    /// Reads the mandatory chunk headers. False means this is not a part
    /// request at all, and nothing has been read from the body yet.
    /// </summary>
    public static bool TryRead(
        HttpRequest request,
        out ResumableChunkRange range,
        out string sha256)
    {
        ArgumentNullException.ThrowIfNull(request);

        range = default;
        sha256 = string.Empty;
        if (!MigrationChunkHeaders.TryParseContentRange(
                request.Headers["Content-Range"],
                out var start,
                out var end,
                out var total)
            || !MigrationChunkHeaders.IsSha256(
                request.Headers[MigrationChunkHeaders.ChunkHashHeaderName]))
        {
            return false;
        }

        range = new ResumableChunkRange(start, end, total);
        sha256 = request.Headers[MigrationChunkHeaders.ChunkHashHeaderName].ToString();
        return true;
    }

    /// <summary>
    /// True when the client declared a body larger than the host accepts for a
    /// part. Such a request is answered without reading a byte of it.
    /// </summary>
    public static bool DeclaresMoreThan(HttpRequest request, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.ContentLength > maxBytes;
    }

    /// <summary>
    /// Caps how much body Kestrel reads for this request, so a chunked body
    /// that grows past the part size fails with 413 instead of being buffered
    /// in full. A feature Kestrel already froze is left alone.
    /// </summary>
    public static void LimitBodySize(HttpRequest request, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
            bodySize.MaxRequestBodySize = maxBytes;
    }

    /// <summary>
    /// Kestrel's 413 for a body that outgrew the cap while it was being read.
    /// A part-and-parse failure that is not a size failure is not ours.
    /// </summary>
    public static bool IsPayloadTooLarge(BadHttpRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.StatusCode == StatusCodes.Status413PayloadTooLarge;
    }
}

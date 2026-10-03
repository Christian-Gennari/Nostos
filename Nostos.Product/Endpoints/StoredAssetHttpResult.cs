using Microsoft.Net.Http.Headers;
using Nostos.Backend.Services;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// HTTP delivery for provider-neutral stored assets. The storage provider owns
/// where bytes live; this result owns Range/ETag/Content-Disposition semantics.
/// </summary>
public sealed class StoredAssetHttpResult : IResult
{
    private readonly StoredAssetRead _read;
    private readonly bool _attachment;
    private readonly string? _cacheControl;

    private StoredAssetHttpResult(
        StoredAssetRead read,
        bool attachment,
        string? cacheControl)
    {
        _read = read;
        _attachment = attachment;
        _cacheControl = cacheControl;
    }

    public static async Task<IResult> CreateAsync(
        HttpContext http,
        Func<CancellationToken, Task<StoredAssetInfo?>> getInfo,
        Func<StorageByteRange?, CancellationToken, Task<StoredAssetRead?>> open,
        bool attachment,
        bool enableRanges,
        string? cacheControl,
        CancellationToken ct)
    {
        var info = await getInfo(ct);
        if (info is null)
            return Results.NotFound();

        if (MatchesIfNoneMatch(http, info.EntityTag))
        {
            http.Response.Headers.ETag = info.EntityTag;
            if (cacheControl is not null)
                http.Response.Headers.CacheControl = cacheControl;
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        StorageByteRange? range = null;
        if (enableRanges && http.Request.Headers.TryGetValue(HeaderNames.Range, out var rawRange))
        {
            var parsed = ParseSingleRange(rawRange.ToString(), info.Length);
            if (parsed.Status == RangeParseStatus.Invalid)
            {
                http.Response.Headers.ContentRange = $"bytes */{info.Length}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            range = parsed.Range;
        }

        var read = await open(range, ct);
        return read is null
            ? Results.NotFound()
            : new StoredAssetHttpResult(read, attachment, cacheControl);
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        await using var read = _read;

        var response = httpContext.Response;
        var info = read.Info;

        response.StatusCode = read.Range is null
            ? StatusCodes.Status200OK
            : StatusCodes.Status206PartialContent;
        response.ContentType = info.ContentType;
        response.Headers.AcceptRanges = "bytes";
        response.Headers.ETag = info.EntityTag;
        response.Headers.LastModified = info.LastModified.ToUniversalTime().ToString("R");

        if (_cacheControl is not null)
            response.Headers.CacheControl = _cacheControl;

        var bytesToWrite = info.Length;
        if (read.Range is { } range)
        {
            bytesToWrite = range.Length;
            response.Headers.ContentRange =
                $"bytes {range.Start}-{range.EndInclusive}/{info.Length}";
        }

        response.ContentLength = bytesToWrite;

        if (_attachment)
        {
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(info.FileName);
            response.Headers.ContentDisposition = disposition.ToString();
        }

        await CopyExactlyAsync(
            read.Content,
            response.Body,
            bytesToWrite,
            httpContext.RequestAborted);
    }

    private static bool MatchesIfNoneMatch(HttpContext http, string entityTag)
    {
        var raw = http.Request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Split(',')
            .Select(value => value.Trim())
            .Any(value => value == "*" || string.Equals(value, entityTag, StringComparison.Ordinal));
    }

    private static RangeParseResult ParseSingleRange(string raw, long totalLength)
    {
        if (totalLength <= 0
            || string.IsNullOrWhiteSpace(raw)
            || !raw.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return RangeParseResult.Invalid;
        }

        var value = raw[6..].Trim();
        if (value.Contains(','))
            return RangeParseResult.Invalid;

        var dash = value.IndexOf('-');
        if (dash < 0)
            return RangeParseResult.Invalid;

        var left = value[..dash].Trim();
        var right = value[(dash + 1)..].Trim();

        if (left.Length == 0)
        {
            if (!long.TryParse(right, out var suffixLength) || suffixLength <= 0)
                return RangeParseResult.Invalid;

            suffixLength = Math.Min(suffixLength, totalLength);
            var start = totalLength - suffixLength;
            return RangeParseResult.Valid(
                StorageByteRange.Create(start, totalLength - 1, totalLength));
        }

        if (!long.TryParse(left, out var requestedStart)
            || requestedStart < 0
            || requestedStart >= totalLength)
        {
            return RangeParseResult.Invalid;
        }

        long requestedEnd;
        if (right.Length == 0)
        {
            requestedEnd = totalLength - 1;
        }
        else if (!long.TryParse(right, out requestedEnd)
            || requestedEnd < requestedStart)
        {
            return RangeParseResult.Invalid;
        }

        requestedEnd = Math.Min(requestedEnd, totalLength - 1);
        return RangeParseResult.Valid(
            StorageByteRange.Create(requestedStart, requestedEnd, totalLength));
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long bytes,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        var remaining = bytes;

        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                ct);

            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Stored asset ended with {remaining} response bytes still expected.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
    }

    private enum RangeParseStatus
    {
        Valid,
        Invalid,
    }

    private readonly record struct RangeParseResult(
        RangeParseStatus Status,
        StorageByteRange? Range)
    {
        public static RangeParseResult Invalid { get; } =
            new(RangeParseStatus.Invalid, null);

        public static RangeParseResult Valid(StorageByteRange range) =>
            new(RangeParseStatus.Valid, range);
    }
}

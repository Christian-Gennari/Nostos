using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Configuration for the Readwise export API client (issue #1525). The base
/// URL is overridable so tests and a local proxy never call the real service.
/// </summary>
public sealed class ReadwiseOptions
{
    public const string SectionName = "Readwise";

    public string BaseUrl { get; set; } = "https://readwise.io";

    /// <summary>Sent as <c>page_size</c> when positive; 0 leaves the API default.</summary>
    public int PageSize { get; set; }

    /// <summary>A guard against an endless or malicious cursor chain.</summary>
    public int MaxPages { get; set; } = 200;

    public int MaxRateLimitRetries { get; set; } = 3;

    public int MaxRetryAfterSeconds { get; set; } = 60;
}

/// <summary>Readwise rejected the token (401 from any API v2 endpoint).</summary>
internal sealed class ReadwiseInvalidTokenException : Exception
{
    public ReadwiseInvalidTokenException()
        : base("Readwise did not accept that access token.")
    {
    }
}

/// <summary>The owner has not connected Readwise, or the saved token cannot be read.</summary>
public sealed class ReadwiseNotConnectedException : Exception
{
    public ReadwiseNotConnectedException()
        : base("Connect Readwise with an access token first.")
    {
    }
}

/// <summary>
/// Readwise answered with a failure, or its cursor chain did not end. The
/// message contains the status code only: never a body, a title or a token.
/// </summary>
internal class ReadwiseUpstreamException : Exception
{
    public ReadwiseUpstreamException(string message)
        : base(message)
    {
    }
}

internal sealed class ReadwiseRateLimitedException : ReadwiseUpstreamException
{
    public ReadwiseRateLimitedException(TimeSpan retryAfter)
        : base("Readwise is rate limiting this import. Try again in a moment.")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}

/// <summary>Everything one export run mapped, across every page.</summary>
internal sealed record ReadwiseExportData(
    IReadOnlyList<ImportSourceBook> Books,
    IReadOnlyList<HighlightImportSkipped> Skipped);

/// <summary>
/// The two calls Nostos makes to Readwise: validate a token
/// (<c>GET /api/v2/auth/</c>, 204 means valid) and page the export
/// (<c>GET /api/v2/export/</c> with <c>updatedAfter</c> and
/// <c>pageCursor</c>), honouring <c>Retry-After</c> on 429.
///
/// <para>
/// Nothing here logs a response body, a title or the token. A format change is
/// raised as <see cref="ReadwiseFormatException"/> with JSON paths and types
/// only, so the same information can go to the owner and the canary log.
/// </para>
/// </summary>
public sealed class ReadwiseApiClient
{
    public const string HttpClientName = "readwise";

    private static readonly MediaTypeWithQualityHeaderValue Json =
        new("application/json");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ReadwiseOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<ReadwiseApiClient> _logger;

    public ReadwiseApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<ReadwiseOptions> options,
        ILogger<ReadwiseApiClient> logger,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? ((value, token) => Task.Delay(value, token));
        _logger = logger;
    }

    /// <summary>Throws <see cref="ReadwiseInvalidTokenException"/> unless Readwise answers 204.</summary>
    internal async Task ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(token, "api/v2/auth/");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ReadwiseInvalidTokenException();
        throw new ReadwiseUpstreamException(
            $"Readwise answered {(int)response.StatusCode} when the token was checked.");
    }

    /// <summary>
    /// Pages the export from <paramref name="updatedAfter"/> (null: the whole
    /// account). Following the documented cursor loop; a page with no next
    /// cursor ends the run.
    /// </summary>
    internal async Task<ReadwiseExportData> ExportAsync(
        string token,
        DateTime? updatedAfter,
        CancellationToken ct = default)
    {
        var books = new List<ImportSourceBook>();
        var skipped = new SkipTally();
        string? cursor = null;
        var pages = 0;

        do
        {
            using var request = Request(
                token,
                "api/v2/export/",
                updatedAfter: updatedAfter,
                cursor: cursor);
            using var response = await SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ReadwiseInvalidTokenException();
            if (!response.IsSuccessStatusCode)
                throw new ReadwiseUpstreamException(
                    $"Readwise answered {(int)response.StatusCode} for the export.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException)
            {
                throw new ReadwiseFormatException(
                [
                    new ReadwiseSchemaIssue("$", "JSON", "unreadable"),
                ]);
            }

            using var parsed = document;
            var page = ReadwiseApiMapper.Map(parsed.RootElement);
            books.AddRange(page.Books);
            foreach (var entry in page.Skipped)
            {
                for (var i = 0; i < entry.Count; i++)
                    skipped.Add(entry.Title, entry.Author, entry.Reason);
            }

            cursor = NextCursor(parsed.RootElement);
            pages++;
            if (cursor is not null && pages >= _options.MaxPages)
                throw new ReadwiseUpstreamException(
                    $"Readwise's export did not end within {_options.MaxPages} pages.");
        }
        while (cursor is not null);

        _logger.LogInformation(
            "Readwise export read {Pages} page(s): {Books} importable book(s), {Skipped} skipped entry/entries.",
            pages,
            books.Count,
            skipped.ToList().Sum(entry => entry.Count));
        return new ReadwiseExportData(books, skipped.ToList());
    }

    private HttpRequestMessage Request(
        string token,
        string path,
        DateTime? updatedAfter = null,
        string? cursor = null)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/{path}";
        var query = new List<string>();
        if (updatedAfter is { } watermark)
        {
            query.Add("updatedAfter=" + Uri.EscapeDataString(
                watermark.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add("pageCursor=" + Uri.EscapeDataString(cursor));
        if (_options.PageSize > 0)
            query.Add("page_size=" + _options.PageSize.ToString(CultureInfo.InvariantCulture));
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(Json);
        // The token travels in the header, never the URL: a URL can end up in a
        // request log or an exception message.
        request.Headers.TryAddWithoutValidation("Authorization", "Token " + token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 0; ; attempt++)
        {
            var response = await client.SendAsync(
                attempt == 0 ? request : Clone(request),
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests
                || attempt >= Math.Max(0, _options.MaxRateLimitRetries))
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = RetryAfter(response) ?? TimeSpan.FromSeconds(1);
                    response.Dispose();
                    throw new ReadwiseRateLimitedException(retryAfter);
                }

                return response;
            }

            var delay = RetryAfter(response) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            // Politeness is the point: the API asks for a wait, so wait it.
            await _delay(delay, ct);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;

        var delay = header.Delta
            ?? (header.Date is { } date ? date - _clock.GetUtcNow() : null);
        if (delay is null)
            return null;

        var seconds = Math.Clamp(delay.Value.TotalSeconds, 0, _options.MaxRetryAfterSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static HttpRequestMessage Clone(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private static string? NextCursor(JsonElement root)
    {
        if (!root.TryGetProperty("nextPageCursor", out var cursor)
            || cursor.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Documented as a string; the live API returns a number. Both are the
        // opaque value the next request sends back.
        var value = cursor.ValueKind == JsonValueKind.String
            ? cursor.GetString()
            : cursor.GetRawText();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

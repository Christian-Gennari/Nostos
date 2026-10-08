using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.StandardEbooks;

public sealed partial class StandardEbooksProvider
{
    /// <summary>Delay between sequential pages when the feed advertises continuation.</summary>
    public static readonly TimeSpan SnapshotPageDelay = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RateLimitFallbackDelay = TimeSpan.FromMinutes(1);
    private const int MaximumSnapshotPages = 1_000;

    /// <summary>
    /// Reads either the daily new-releases feed or the complete catalog.
    ///
    /// A request with a saved ETag or Last-Modified value is an incremental
    /// check against the small new-releases feed. A request without validators
    /// is a full read of the all-ebooks feed, which lets the host run its weekly
    /// reconcile by omitting the daily checkpoint. Both feeds supply their own
    /// validators; continuation state carries the selected feed and validators
    /// across pages without interpreting them in the host.
    /// </summary>
    public async Task<ProviderSnapshot> ReadAsync(ProviderSnapshotRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        SnapshotCursor? continuation = request.Cursor is { Length: > 0 } encoded
            ? SnapshotCursor.Decode(encoded)
            : null;

        var mode = continuation?.Mode
            ?? (HasCheckpoint(request) ? SnapshotMode.Incremental : SnapshotMode.Full);
        var requestUri = continuation is null
            ? new Uri(StandardEbooksCatalog.BaseUrl + (mode == SnapshotMode.Incremental
                ? StandardEbooksCatalog.NewReleasesPath
                : StandardEbooksCatalog.AllEbooksPath))
            : continuation.NextPageUri;

        if (continuation is not null)
            await Task.Delay(SnapshotPageDelay, _clock, ct);

        HttpResponseMessage response;
        try
        {
            response = await SendSnapshotPageAsync(
                requestUri,
                etag: continuation is null && mode == SnapshotMode.Incremental ? request.ETag : null,
                ifModifiedSince: continuation is null && mode == SnapshotMode.Incremental ? request.IfModifiedSince : null,
                useValidators: continuation is null && mode == SnapshotMode.Incremental,
                ct);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(ProviderIdentifier, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw ProviderException.UnavailableFor(ProviderIdentifier, ex.Message);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (continuation is not null || mode != SnapshotMode.Incremental)
                {
                    throw ProviderException.UnavailableFor(
                        ProviderIdentifier,
                        "the complete catalog unexpectedly answered HTTP 304");
                }

                return ProviderSnapshot.NotModified(
                    etag: response.Headers.ETag?.ToString() ?? request.ETag,
                    lastModified: response.Content.Headers.LastModified ?? request.IfModifiedSince);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw ProviderException.UnavailableFor(
                    ProviderIdentifier,
                    $"the OPDS catalog answered HTTP {(int)response.StatusCode}");
            }

            XDocument feed;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                feed = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
            }
            catch (XmlException ex)
            {
                throw ProviderException.InvalidResponse(ProviderIdentifier, ex.Message);
            }
            catch (HttpRequestException ex)
            {
                throw ProviderException.UnavailableFor(ProviderIdentifier, ex.Message);
            }
            catch (IOException ex)
            {
                throw ProviderException.UnavailableFor(ProviderIdentifier, ex.Message);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw ProviderException.UnavailableFor(ProviderIdentifier, ex.Message);
            }

            if (!StandardEbooksCatalog.IsFeed(feed))
            {
                throw ProviderException.InvalidResponse(
                    ProviderIdentifier,
                    "the response was not an OPDS Atom feed");
            }

            var etag = response.Headers.ETag?.ToString()
                ?? continuation?.ETag
                ?? (mode == SnapshotMode.Incremental ? request.ETag : null);
            var lastModified = response.Content.Headers.LastModified
                ?? continuation?.LastModified
                ?? (mode == SnapshotMode.Incremental ? request.IfModifiedSince : null);

            var nextPage = StandardEbooksCatalog.NextPageUri(feed, requestUri);
            if (nextPage == requestUri)
            {
                throw ProviderException.InvalidResponse(
                    ProviderIdentifier,
                    "the OPDS catalog linked to the page it had just returned");
            }

            if (nextPage is not null && (continuation?.PageNumber ?? 1) >= MaximumSnapshotPages)
            {
                throw ProviderException.InvalidResponse(
                    ProviderIdentifier,
                    $"the OPDS catalog exceeded {MaximumSnapshotPages} sequential pages");
            }

            var nextCursor = nextPage is null
                ? null
                : new SnapshotCursor(
                    Mode: mode,
                    NextPageUri: nextPage,
                    ETag: etag,
                    LastModified: lastModified,
                    PageNumber: (continuation?.PageNumber ?? 1) + 1).Encode();

            var items = StandardEbooksCatalog.Parse(feed)
                .Select(book => ToProviderItem(book, includeAssets: false))
                .ToList();

            return new ProviderSnapshot(
                ProviderSnapshotStatus.Updated,
                items.ToAsyncEnumerable(),
                ETag: etag,
                LastModified: lastModified,
                NextCursor: nextCursor);
        }
    }

    private async Task<HttpResponseMessage> SendSnapshotPageAsync(
        Uri requestUri,
        string? etag,
        DateTimeOffset? ifModifiedSince,
        bool useValidators,
        CancellationToken ct)
    {
        var retriedAfterRateLimit = false;

        while (true)
        {
            HttpResponseMessage response;
            using (var message = new HttpRequestMessage(HttpMethod.Get, requestUri))
            {
                if (useValidators)
                    ApplyValidators(message, etag, ifModifiedSince);

                response = await _snapshotHttp.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests || retriedAfterRateLimit)
                return response;

            var delay = RetryDelay(response);
            _logger.LogWarning(
                "Standard Ebooks rate-limited a catalog read; retrying once after {RetryDelay}",
                delay);
            response.Dispose();

            await Task.Delay(delay, _clock, ct);
            retriedAfterRateLimit = true;
        }
    }

    private TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (retryAfter?.Date is { } date)
        {
            var remaining = date - _clock.GetUtcNow();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return RateLimitFallbackDelay;
    }

    private static bool HasCheckpoint(ProviderSnapshotRequest request) =>
        !string.IsNullOrWhiteSpace(request.ETag)
        || request.IfModifiedSince is not null;

    private static void ApplyValidators(
        HttpRequestMessage request,
        string? etag,
        DateTimeOffset? ifModifiedSince)
    {
        if (!string.IsNullOrWhiteSpace(etag)
            && EntityTagHeaderValue.TryParse(etag, out var entityTag))
        {
            request.Headers.IfNoneMatch.Add(entityTag);
        }

        if (ifModifiedSince is { } modifiedSince)
            request.Headers.IfModifiedSince = modifiedSince;
    }

    private enum SnapshotMode
    {
        Full,
        Incremental,
    }

    /// <summary>
    /// Opaque continuation state. Validators remain attached to the scan that
    /// produced them so a resumed page reports the same checkpoint even if a
    /// response page omits either header.
    /// </summary>
    private sealed record SnapshotCursor(
        SnapshotMode Mode,
        Uri NextPageUri,
        string? ETag,
        DateTimeOffset? LastModified,
        int PageNumber)
    {
        public string Encode()
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new EncodedSnapshotCursor(
                Version: "v1",
                Mode: Mode == SnapshotMode.Incremental ? "incremental" : "full",
                NextUrl: NextPageUri.AbsoluteUri,
                ETag: ETag,
                LastModified: LastModified,
                PageNumber: PageNumber));

            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        public static SnapshotCursor Decode(string encoded)
        {
            try
            {
                if (encoded.Length > 16_384)
                    throw InvalidCursor();

                var base64 = encoded.Replace('-', '+').Replace('_', '/');
                base64 += new string('=', (4 - base64.Length % 4) % 4);
                var value = JsonSerializer.Deserialize<EncodedSnapshotCursor>(Convert.FromBase64String(base64));

                if (value is null
                    || value.Version != "v1"
                    || value.Mode is not ("full" or "incremental")
                    || value.PageNumber is < 2 or > MaximumSnapshotPages
                    || !Uri.TryCreate(value.NextUrl, UriKind.Absolute, out var nextPageUri)
                    || !IsAllowedSnapshotUri(nextPageUri))
                {
                    throw InvalidCursor();
                }

                return new SnapshotCursor(
                    value.Mode == "incremental" ? SnapshotMode.Incremental : SnapshotMode.Full,
                    nextPageUri,
                    value.ETag,
                    value.LastModified,
                    value.PageNumber);
            }
            catch (ProviderException)
            {
                throw;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                throw InvalidCursor();
            }
        }

        private static ProviderException InvalidCursor() =>
            ProviderException.InvalidResponse(
                ProviderIdentifier,
                "the snapshot cursor is not valid");
    }

    private sealed record EncodedSnapshotCursor(
        string Version,
        string Mode,
        string NextUrl,
        string? ETag,
        DateTimeOffset? LastModified,
        int PageNumber);

    private static bool IsAllowedSnapshotUri(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (string.Equals(uri.Host, "standardebooks.org", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".standardebooks.org", StringComparison.OrdinalIgnoreCase))
        && (uri.AbsolutePath.Equals("/feeds/opds", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/feeds/opds/", StringComparison.OrdinalIgnoreCase));
}

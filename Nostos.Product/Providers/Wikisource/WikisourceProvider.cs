using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Wikisource;

/// <summary>
/// English Wikisource as a Nostos ebook source, backed by WS Export OPDS.
///
/// The provider only describes and resolves remote content. The common
/// acquisition layer downloads the chosen ebook representation and turns it
/// into an ordinary local Nostos book.
/// </summary>
public sealed class WikisourceProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy,
    IProviderSnapshotSource
{
    public const string ProviderIdentifier = "wikisource";
    public const string HttpClientName = "wikisource";

    private const long MaxEbookBytes = 128L * 1024 * 1024;
    private const int MaxExternalIdLength = 512;

    /// <summary>
    /// How long a parsed export-ready catalogue is reused before the next
    /// search refetches it (#641). WS Export's list changes slowly; before this
    /// every search downloaded and parsed the whole feed.
    /// </summary>
    public static readonly TimeSpan CatalogFreshFor = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly object _catalogGate = new();
    private CatalogSnapshot? _catalog;
    private Task<CatalogSnapshot>? _catalogRefresh;

    public WikisourceProvider(IHttpClientFactory httpClientFactory, TimeProvider? clock = null)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _clock = clock ?? TimeProvider.System;
    }

    public string Id => ProviderIdentifier;
    public string DisplayName => "Wikisource";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Search
        | ProviderCapabilities.ItemRetrieval
        | ProviderCapabilities.EbookAcquisition
        | ProviderCapabilities.CoverArt
        | ProviderCapabilities.RightsInformation;

    // Rights vary by work. The per-item Atom <rights> value is preserved
    // verbatim rather than turning Wikisource into a blanket public-domain
    // claim.
    public string? RightsNotice => null;

    public bool EnabledByDefault => true;

    public string? Description => "Transcribed public-domain texts in many languages.";

    public IReadOnlyList<string> AllowedHosts =>
    [
        "ws-export.wmcloud.org",
        "upload.wikimedia.org",
        "thumb.wikimedia.org",
    ];

    public long MaxBytesPerPart => MaxEbookBytes;
    public long MaxTotalBytes => MaxEbookBytes;
    public int MaxParts => 1;

    public async Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct)
    {
        if (query.Kind is not null && query.Kind != ProviderMediaKind.Ebook)
            return new ProviderSearchPage([], HasMore: false);

        var books = await GetCatalogAsync(ct);
        var terms = (query.Query ?? string.Empty)
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var filtered = books
            .Where(book => Matches(book, terms))
            .ToList();

        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 100);
        var page = filtered.Skip(offset).Take(limit).Select(b => ToProviderItem(b, false)).ToList();

        return new ProviderSearchPage(
            Items: page,
            HasMore: offset + limit < filtered.Count);
    }

    public async Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct)
    {
        var book = await LoadBookAsync(externalId, ct);
        return book is null ? null : ToProviderItem(book, includeAssets: true);
    }

    /// <summary>
    /// Reads the whole Ready-for-export catalogue as one snapshot, honoring the
    /// caller's conditional validators. WS Export regenerates the feed daily
    /// and supplies ETag and Last-Modified, so a host can re-check it cheaply;
    /// a 304 reports <see cref="ProviderSnapshotStatus.NotModified"/> without a
    /// body. The feed is a single page, so <see cref="ProviderSnapshot.NextCursor"/>
    /// is always null. The live search cache is deliberately not used here:
    /// this read must see the source's own validators rather than a cached copy.
    /// </summary>
    public async Task<ProviderSnapshot> ReadAsync(ProviderSnapshotRequest request, CancellationToken ct)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, WikisourceCatalog.CatalogPath);

            if (!string.IsNullOrWhiteSpace(request.ETag)
                && EntityTagHeaderValue.TryParse(request.ETag, out var entityTag))
            {
                httpRequest.Headers.IfNoneMatch.Add(entityTag);
            }

            if (request.IfModifiedSince is { } ifModifiedSince)
                httpRequest.Headers.IfModifiedSince = ifModifiedSince;

            using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return ProviderSnapshot.NotModified(
                    etag: request.ETag ?? response.Headers.ETag?.ToString(),
                    lastModified: request.IfModifiedSince ?? response.Content.Headers.LastModified);
            }

            if (!response.IsSuccessStatusCode)
                throw ProviderException.UnavailableFor(Id, "WS Export answered HTTP " + (int)response.StatusCode);

            XDocument document;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
            }
            catch (XmlException ex)
            {
                throw ProviderException.InvalidResponse(Id, ex.Message);
            }

            if (!WikisourceCatalog.IsAtomDocument(document))
                throw ProviderException.InvalidResponse(Id, "the response was not an Atom feed");

            return new ProviderSnapshot(
                ProviderSnapshotStatus.Updated,
                WikisourceCatalog.Parse(document)
                    .Select(book => ToProviderItem(book))
                    .ToAsyncEnumerable(),
                ETag: response.Headers.ETag?.ToString(),
                LastModified: response.Content.Headers.LastModified);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The 20 s client timeout surfaces as an OperationCanceledException
            // that has nothing to do with the caller's token. A sync that only
            // knows provider_unavailable would not recognise a raw one.
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
    }

    public async Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
        ProviderAcquisitionRequest request,
        CancellationToken ct)
    {
        var book = await LoadBookAsync(request.ExternalId, ct);
        if (book is null)
            return null;

        var selected = request.AssetId is null
            ? book.Assets.FirstOrDefault(asset => asset.IsPreferred) ?? book.Assets.FirstOrDefault()
            : book.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Id, request.AssetId, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
            throw ProviderException.AssetUnavailableFor(Id, book.Page, request.AssetId ?? "ebook");

        var asset = ToProviderAsset(selected);

        return new ProviderAcquisitionPlan(
            ProviderId: Id,
            ExternalId: book.Page,
            Asset: asset,
            Metadata: MetadataFor(book),
            Parts:
            [
                new ProviderDownloadPart(
                    Url: selected.Url,
                    FileExtension: selected.FileExtension,
                    ExpectedBytes: null,
                    Label: selected.Label),
            ],
            Output: new ProviderOutput(selected.FileExtension, selected.SourceFormat, selected.Label),
            Cover: book.Cover,
            Source: new ProviderSourceInfo(
                ItemUrl: book.SourceUrl?.ToString(),
                RightsStatement: book.Rights,
                RightsUrl: null));
    }

    /// <summary>
    /// The parsed catalogue, reused while fresh. A refresh is single-flight:
    /// concurrent searches share one download instead of each starting their
    /// own. The download runs on its own bounded lifetime (the client's 20 s
    /// timeout), not the caller's token, so a search that gives up (the
    /// aggregate discovery deadline, a superseded request) stops waiting but
    /// still lets the shared refresh finish and warm the cache for the next
    /// search. A failed refresh is not cached; the next search retries it. An
    /// expired catalogue is never served: it is refetched.
    /// </summary>
    private async Task<IReadOnlyList<WikisourceBook>> GetCatalogAsync(CancellationToken ct)
    {
        Task<CatalogSnapshot> refresh;
        lock (_catalogGate)
        {
            if (_catalog is { } cached && _clock.GetUtcNow() - cached.LoadedAt < CatalogFreshFor)
                return cached.Books;

            refresh = _catalogRefresh ??= Task.Run(RefreshCatalogAsync);
        }

        var snapshot = await refresh.WaitAsync(ct);
        return snapshot.Books;
    }

    private async Task<CatalogSnapshot> RefreshCatalogAsync()
    {
        try
        {
            var feed = await LoadAsync(WikisourceCatalog.CatalogPath, CancellationToken.None);
            var snapshot = new CatalogSnapshot(
                feed is null ? [] : WikisourceCatalog.Parse(feed),
                _clock.GetUtcNow());

            lock (_catalogGate)
            {
                _catalog = snapshot;
                _catalogRefresh = null;
            }

            return snapshot;
        }
        catch
        {
            lock (_catalogGate)
                _catalogRefresh = null;
            throw;
        }
    }

    private sealed record CatalogSnapshot(IReadOnlyList<WikisourceBook> Books, DateTimeOffset LoadedAt);

    private async Task<WikisourceBook?> LoadBookAsync(string externalId, CancellationToken ct)
    {
        var page = NormalizeExternalId(externalId);
        if (page is null)
            return null;

        var escaped = Uri.EscapeDataString(page);
        var feed = await LoadAsync("/?lang=en&format=atom&page=" + escaped, ct);
        return feed is null ? null : WikisourceCatalog.ParseItem(feed, page);
    }

    private async Task<XDocument?> LoadAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return null;

            if (!response.IsSuccessStatusCode)
                throw ProviderException.UnavailableFor(Id, "WS Export answered HTTP " + (int)response.StatusCode);

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
                if (!WikisourceCatalog.IsAtomDocument(document))
                    throw ProviderException.InvalidResponse(Id, "the response was not an Atom feed");

                return document;
            }
            catch (XmlException ex)
            {
                throw ProviderException.InvalidResponse(Id, ex.Message);
            }
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
    }

    private static bool Matches(WikisourceBook book, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return true;

        var haystack = string.Join(
            "\n",
            new[] { book.Title, book.Author, book.Categories }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeExternalId(string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        var page = externalId.Trim();
        if (page.Length > MaxExternalIdLength || page.Any(char.IsControl))
            return null;

        return page;
    }

    private ProviderItem ToProviderItem(WikisourceBook book, bool includeAssets = false) => new(
        ProviderId: Id,
        ExternalId: book.Page,
        MediaKind: ProviderMediaKind.Ebook,
        Metadata: MetadataFor(book),
        Assets: includeAssets ? book.Assets.Select(ToProviderAsset).ToList() : [],
        Cover: book.Cover,
        Source: new ProviderSourceInfo(
            ItemUrl: book.SourceUrl?.ToString(),
            RightsStatement: book.Rights,
            RightsUrl: null),
        PartCount: null);

    private static ProviderAsset ToProviderAsset(WikisourceAsset asset) => new(
        Id: asset.Id,
        Kind: ProviderMediaKind.Ebook,
        Label: asset.Label,
        SourceFormat: asset.SourceFormat,
        SizeBytes: null,
        IsPreferred: asset.IsPreferred);

    private static ProviderMetadata MetadataFor(WikisourceBook book) => new(
        Title: book.Title,
        Author: book.Author,
        Description: book.Description,
        Language: book.Language,
        Publisher: book.Publisher,
        PublishedDate: book.PublishedDate,
        Categories: book.Categories);
}

using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Project Gutenberg as a Nostos content source.
///
/// Everything Gutenberg-shaped stops at this boundary. Downstream only ever sees
/// the normalized contracts: an imported book is an ordinary local ebook, and
/// nothing in the library, the reader, notes, work grouping or backups knows
/// this provider exists.
///
/// Uses Gutenberg's machine-readable OPDS/Atom catalogue — the same feeds an
/// e-reader would use — rather than the human-facing website.
/// </summary>
public sealed partial class GutenbergProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy
{
    public const string ProviderIdentifier = "gutenberg";

    /// <summary>Named client for the catalogue, so timeouts and headers live in DI.</summary>
    public const string HttpClientName = "gutenberg";

    /// <summary>
    /// Gutenberg's ebook ids are numeric. Validated before the id is used to
    /// build a request path, so a client cannot walk out of the catalogue with a
    /// crafted id.
    /// </summary>
    [GeneratedRegex(@"^\d{1,7}$")]
    private static partial Regex EbookId();

    private const long MaxEbookBytes = 96L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<GutenbergProvider> _logger;

    public GutenbergProvider(IHttpClientFactory httpClientFactory, ILogger<GutenbergProvider> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _logger = logger;
    }

    public string Id => ProviderIdentifier;

    public string DisplayName => "Project Gutenberg";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Search
        | ProviderCapabilities.ItemRetrieval
        | ProviderCapabilities.EbookAcquisition
        | ProviderCapabilities.CoverArt
        | ProviderCapabilities.RightsInformation;

    /// <summary>
    /// Quoted from the source, not asserted by Nostos: Gutenberg states its
    /// public-domain position for the United States, and that is all this says.
    /// </summary>
    public string? RightsNotice => "Public domain in the USA (Project Gutenberg)";

    // --- IProviderDownloadPolicy ----------------------------------------
    // Suffix-matched (see ProviderHostPolicy), so www.gutenberg.org and any
    // mirror on the same domain are covered by one entry. A single ebook is at
    // most a couple of dozen megabytes, even illustrated; the headroom is there
    // so a genuinely large volume is not rejected, not because we expect it.
    public IReadOnlyList<string> AllowedHosts => ["gutenberg.org"];

    public long MaxBytesPerPart => MaxEbookBytes;

    public long MaxTotalBytes => MaxEbookBytes;

    /// <summary>One ebook asset per import: Gutenberg exposes no multi-file ebook.</summary>
    public int MaxParts => 1;

    // --- IProviderSearch -------------------------------------------------

    public async Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct)
    {
        var search = Uri.EscapeDataString(query.Query);
        var path = $"/ebooks/search.opds/?query={search}";

        // The catalogue pages with a 1-based start index.
        if (query.Offset > 0)
            path += $"&start_index={query.Offset + 1}";

        var feed = await LoadAsync(path, ct);
        if (feed is null)
            return new ProviderSearchPage([], HasMore: false);

        var books = GutenbergCatalog.ParseSearch(feed);
        var perPage = GutenbergCatalog.ItemsPerPage(feed);

        var items = books.Select(book => ToProviderItem(book, includeAssets: false)).ToList();

        return new ProviderSearchPage(
            Items: items,
            // The feed reports no total, so a full page is the only signal that
            // there may be more.
            HasMore: books.Count >= perPage && items.Count > 0,
            Notice: null);
    }

    // --- IProviderCatalog ------------------------------------------------

    public async Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct)
    {
        var book = await LoadBookAsync(externalId, ct);
        return book is null ? null : ToProviderItem(book, includeAssets: true);
    }

    // --- IProviderAcquisitionPlanner -------------------------------------

    public async Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
        ProviderAcquisitionRequest request,
        CancellationToken ct)
    {
        // The catalogue is re-read here rather than reusing whatever the UI last
        // saw: the plan is what actually gets downloaded, so it is resolved from
        // the source as it is now, and a stale asset id fails loudly instead of
        // fetching a URL that is no longer offered.
        var book = await LoadBookAsync(request.ExternalId, ct);
        if (book is null)
            return null;

        var asset = request.AssetId is null
            ? book.Assets.FirstOrDefault(a => a.IsPreferred) ?? book.Assets.FirstOrDefault()
            : book.Assets.FirstOrDefault(a => string.Equals(a.Id, request.AssetId, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
            throw ProviderException.AssetUnavailableFor(Id, book.Id, request.AssetId ?? "ebook");

        return new ProviderAcquisitionPlan(
            ProviderId: Id,
            ExternalId: book.Id,
            Asset: new ProviderAsset(
                Id: asset.Id,
                Kind: ProviderMediaKind.Ebook,
                Label: asset.Label,
                SourceFormat: asset.SourceFormat,
                SizeBytes: asset.SizeBytes,
                IsPreferred: asset.IsPreferred),
            Metadata: new ProviderMetadata(
                Title: book.Title,
                Author: book.Author,
                Description: book.Description,
                Language: book.Language,
                Categories: book.Categories),
            Parts:
            [
                new ProviderDownloadPart(
                    Url: asset.Url,
                    FileExtension: asset.FileExtension,
                    ExpectedBytes: asset.SizeBytes,
                    Label: asset.Label),
            ],
            Output: new ProviderOutput(asset.FileExtension, asset.SourceFormat, asset.OutputLabel),
            Cover: GutenbergCatalog.CoverFor(book.Id),
            Source: new ProviderSourceInfo(
                ItemUrl: $"{GutenbergCatalog.BaseUrl}/ebooks/{book.Id}",
                RightsStatement: book.Rights,
                RightsUrl: $"{GutenbergCatalog.BaseUrl}/policy/terms_of_use.html"));
    }

    // --- internals -------------------------------------------------------

    private async Task<GutenbergBook?> LoadBookAsync(string externalId, CancellationToken ct)
    {
        var id = externalId?.Trim() ?? string.Empty;

        // A non-numeric id cannot address an ebook, and is rejected before it
        // reaches a request path rather than after.
        if (!EbookId().IsMatch(id))
            return null;

        var feed = await LoadAsync($"/ebooks/{id}.opds", ct);
        return feed is null ? null : GutenbergCatalog.ParseDetail(feed, id);
    }

    private async Task<XDocument?> LoadAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);

        // The catalogue answers 404 for an unknown ebook; that is an ordinary
        // "no such item", not a source failure.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;

        if (!response.IsSuccessStatusCode)
            throw ProviderException.UnavailableFor(Id, $"the catalogue answered HTTP {(int)response.StatusCode}");

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }
        catch (XmlException ex)
        {
            // A maintenance page or an HTML error document: worth reporting as a
            // source problem, never worth passing off as an empty catalogue.
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    private ProviderItem ToProviderItem(GutenbergBook book, bool includeAssets) => new(
        ProviderId: Id,
        ExternalId: book.Id,
        MediaKind: ProviderMediaKind.Ebook,
        Metadata: new ProviderMetadata(
            Title: book.Title,
            Author: book.Author,
            Description: book.Description,
            Language: book.Language,
            Categories: book.Categories),
        Assets: includeAssets
            ? book.Assets
                .Select(asset => new ProviderAsset(
                    Id: asset.Id,
                    Kind: ProviderMediaKind.Ebook,
                    Label: asset.Label,
                    SourceFormat: asset.SourceFormat,
                    SizeBytes: asset.SizeBytes,
                    IsPreferred: asset.IsPreferred))
                .ToList()
            : [],
        Cover: GutenbergCatalog.CoverFor(book.Id),
        Source: new ProviderSourceInfo(
            ItemUrl: $"{GutenbergCatalog.BaseUrl}/ebooks/{book.Id}",
            RightsStatement: book.Rights,
            RightsUrl: $"{GutenbergCatalog.BaseUrl}/policy/terms_of_use.html"),
        PartCount: null);
}

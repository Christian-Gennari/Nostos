using System.Net;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.StandardEbooks;

/// <summary>
/// Standard Ebooks through the project's approved OPDS access.
///
/// Access is intentionally based on the User-Agent Standard Ebooks approved for
/// Nostos. There is no shared credential to redistribute, so the same provider
/// works in SelfHosted and Cloud without committing or exposing a secret.
/// </summary>
public sealed class StandardEbooksProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy
{
    public const string ProviderIdentifier = "standard-ebooks";
    public const string HttpClientName = "standard-ebooks";

    /// <summary>
    /// Approved by Standard Ebooks on 2026-09-25. Keep the identity stable unless
    /// Standard Ebooks explicitly approves a replacement. The repository has since been renamed
    /// to Nostos; this historical URL remains intentionally because it is the allowlisted identity.
    /// </summary>
    public const string ApprovedUserAgent =
        "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos-Rebirth)";

    public const string OpdsAccept = "application/atom+xml";

    private const int MaxSearchPageSize = 24;
    private const int DetailSearchPageSize = 24;
    private const long MaxEbookBytes = 128L * 1024 * 1024;

    private readonly HttpClient _http;

    public StandardEbooksProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<StandardEbooksProvider> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);

        // The feed is whitelisted by this exact project identity. Set it here,
        // inside the provider boundary, as well as in product composition so a
        // custom/test IHttpClientFactory cannot accidentally turn approved access
        // into an anonymous 401.
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ApprovedUserAgent);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.ParseAdd(OpdsAccept);

        _ = logger;
    }

    public string Id => ProviderIdentifier;

    public string DisplayName => "Standard Ebooks";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Search
        | ProviderCapabilities.ItemRetrieval
        | ProviderCapabilities.EbookAcquisition
        | ProviderCapabilities.CoverArt
        | ProviderCapabilities.RightsInformation;

    /// <summary>
    /// Source wording. The complete per-item statement is retained in
    /// ProviderSourceInfo and persisted as acquisition provenance.
    /// </summary>
    public string? RightsNotice => "Public domain in the United States.";

    public bool EnabledByDefault => true;

    public string? Description => "Expertly produced public-domain ebooks, mostly English.";

    public IReadOnlyList<string> AllowedHosts => ["standardebooks.org"];

    public long MaxBytesPerPart => MaxEbookBytes;

    public long MaxTotalBytes => MaxEbookBytes;

    public int MaxParts => 1;

    public async Task<ProviderSearchPage> SearchAsync(
        ProviderSearchQuery query,
        CancellationToken ct)
    {
        if (query.Kind == ProviderMediaKind.Audiobook)
            return new ProviderSearchPage([], HasMore: false);

        var search = query.Query.Trim();
        if (search.Length == 0)
            return new ProviderSearchPage([], HasMore: false);

        var pageSize = Math.Clamp(query.Limit, 1, MaxSearchPageSize);
        var page = (Math.Max(0, query.Offset) / pageSize) + 1;
        var path = BuildSearchPath(search, pageSize, page);

        var feed = await LoadFeedAsync(path, ct);
        if (feed is null)
            return new ProviderSearchPage([], HasMore: false);

        var books = StandardEbooksCatalog.Parse(feed);
        var items = books
            .Select(book => ToProviderItem(book, includeAssets: false))
            .ToList();

        return new ProviderSearchPage(
            Items: items,
            HasMore: books.Count >= pageSize);
    }

    public async Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct)
    {
        var book = await LoadBookAsync(externalId, ct);
        return book is null ? null : ToProviderItem(book, includeAssets: true);
    }

    public async Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
        ProviderAcquisitionRequest request,
        CancellationToken ct)
    {
        var book = await LoadBookAsync(request.ExternalId, ct);
        if (book is null)
            return null;

        var asset = request.AssetId is null
            ? book.Assets.FirstOrDefault(item => item.IsPreferred) ?? book.Assets.FirstOrDefault()
            : book.Assets.FirstOrDefault(item =>
                string.Equals(item.Id, request.AssetId, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            throw ProviderException.AssetUnavailableFor(
                Id,
                book.ExternalId,
                request.AssetId ?? "ebook");
        }

        return new ProviderAcquisitionPlan(
            ProviderId: Id,
            ExternalId: book.ExternalId,
            Asset: new ProviderAsset(
                Id: asset.Id,
                Kind: ProviderMediaKind.Ebook,
                Label: asset.Label,
                SourceFormat: asset.SourceFormat,
                SizeBytes: asset.SizeBytes,
                IsPreferred: asset.IsPreferred),
            Metadata: MetadataFor(book),
            Parts:
            [
                new ProviderDownloadPart(
                    Url: asset.Url,
                    FileExtension: ".epub",
                    ExpectedBytes: asset.SizeBytes,
                    Label: asset.Label),
            ],
            Output: new ProviderOutput(
                FileExtension: ".epub",
                ContentType: "application/epub+zip",
                Label: "EPUB"),
            Cover: book.Cover,
            Source: SourceFor(book));
    }

    private async Task<StandardEbooksBook?> LoadBookAsync(
        string externalId,
        CancellationToken ct)
    {
        var id = externalId?.Trim() ?? string.Empty;
        var searchTerms = StandardEbooksCatalog.SearchTermsForExternalId(id);
        if (searchTerms is null)
            return null;

        var feed = await LoadFeedAsync(
            BuildSearchPath(searchTerms, DetailSearchPageSize, page: 1),
            ct);

        if (feed is null)
            return null;

        return StandardEbooksCatalog.FindByExternalId(feed, id);
    }

    private async Task<XDocument?> LoadFeedAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw ProviderException.UnavailableFor(
                Id,
                "Standard Ebooks rejected the approved Nostos OPDS User-Agent");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ProviderException.UnavailableFor(
                Id,
                $"the OPDS feed answered HTTP {(int)response.StatusCode}");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);

            if (!StandardEbooksCatalog.IsFeed(document))
            {
                throw ProviderException.InvalidResponse(
                    Id,
                    "the response was not an OPDS Atom feed");
            }

            return document;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (XmlException ex)
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    private static string BuildSearchPath(string query, int pageSize, int page) =>
        $"/feeds/opds/all?query={Uri.EscapeDataString(query)}&per-page={pageSize}&page={page}";

    private ProviderItem ToProviderItem(StandardEbooksBook book, bool includeAssets) => new(
        ProviderId: Id,
        ExternalId: book.ExternalId,
        MediaKind: ProviderMediaKind.Ebook,
        Metadata: MetadataFor(book),
        Assets: includeAssets
            ? book.Assets.Select(asset => new ProviderAsset(
                Id: asset.Id,
                Kind: ProviderMediaKind.Ebook,
                Label: asset.Label,
                SourceFormat: asset.SourceFormat,
                SizeBytes: asset.SizeBytes,
                IsPreferred: asset.IsPreferred)).ToList()
            : [],
        Cover: book.Cover,
        Source: SourceFor(book),
        PartCount: null);

    private static ProviderMetadata MetadataFor(StandardEbooksBook book) => new(
        Title: book.Title,
        Author: book.Author,
        Description: book.Description,
        Language: book.Language,
        Publisher: book.Publisher,
        PublishedDate: book.PublishedDate,
        Categories: book.Categories);

    private static ProviderSourceInfo SourceFor(StandardEbooksBook book) => new(
        ItemUrl: book.ItemUrl?.ToString() ?? book.Identifier,
        RightsStatement: book.Rights,
        RightsUrl: null);
}

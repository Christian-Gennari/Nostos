using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Product.Storage;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Project Gutenberg as a Nostos content source.
///
/// Everything Gutenberg-shaped stops at this boundary. Downstream only ever sees
/// the normalized contracts: an imported book is an ordinary local ebook, and
/// nothing in the library, the reader, notes, work grouping or backups knows
/// this provider exists.
///
/// Uses Gutenberg's machine-readable catalogue rather than the human-facing
/// website: the OPDS/Atom feeds an e-reader would use for live search and
/// detail, and the daily RDF archive for the bulk snapshot a host maintains its
/// own synchronized discovery index from.
///
/// This type is the provider contract only, and delegates each concern to the
/// collaborator that owns it: <see cref="GutenbergCatalogClient"/> makes the
/// requests, <see cref="GutenbergCatalog"/> parses a feed,
/// <see cref="GutenbergItemMapper"/> maps a parsed book to the provider
/// contracts, <see cref="GutenbergSnapshotDownloader"/> transfers the bulk
/// archive and <see cref="GutenbergSnapshotReader"/> enumerates it.
/// </summary>
public sealed partial class GutenbergProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy,
    IProviderSnapshotSource
{
    public const string ProviderIdentifier = "gutenberg";

    /// <summary>Named client for the catalogue, so timeouts and headers live in DI.</summary>
    public const string HttpClientName = "gutenberg";

    /// <summary>
    /// Named client for the daily bulk catalogue, with its own long-lived
    /// timeout: the 177 MB archive must not be held to the 20 s request budget
    /// the search client uses.
    /// </summary>
    public const string SnapshotHttpClientName = "gutenberg-snapshot";

    private const string SnapshotTempFilePrefix = "nostos-gutenberg";

    /// <summary>
    /// Gutenberg's ebook ids are numeric. Validated before the id is used to
    /// build a request path, so a client cannot walk out of the catalogue with a
    /// crafted id.
    /// </summary>
    [GeneratedRegex(@"^\d{1,7}$")]
    private static partial Regex EbookId();

    private const long MaxEbookBytes = 96L * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly GutenbergCatalogClient _catalog;
    private readonly GutenbergItemMapper _mapper;
    private readonly GutenbergSnapshotDownloader _snapshotDownloader;
    private readonly GutenbergSnapshotReader _snapshotReader;
    private readonly GutenbergSnapshotDownloadOptions _snapshotDownloadOptions;
    private readonly IProductScratchStorage _scratch;

    public GutenbergProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<GutenbergProvider> logger,
        GutenbergSnapshotLimits? snapshotLimits = null)
        : this(httpClientFactory, logger, snapshotLimits, null)
    {
    }

    public GutenbergProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<GutenbergProvider> logger,
        GutenbergSnapshotLimits? snapshotLimits,
        GutenbergSnapshotDownloadOptions? snapshotDownloadOptions,
        IProductScratchStorage? scratchStorage = null)
    {
        _logger = logger;
        _scratch = scratchStorage ?? ProductScratchStorage.TempPath;
        _snapshotDownloadOptions = snapshotDownloadOptions ?? new GutenbergSnapshotDownloadOptions();
        _snapshotDownloadOptions.Validate();

        var limits = snapshotLimits ?? new GutenbergSnapshotLimits();
        _catalog = new GutenbergCatalogClient(httpClientFactory, logger);
        _mapper = new GutenbergItemMapper(ProviderIdentifier);
        _snapshotDownloader = new GutenbergSnapshotDownloader(
            httpClientFactory,
            limits,
            _snapshotDownloadOptions,
            logger,
            ProviderIdentifier);
        _snapshotReader = new GutenbergSnapshotReader(limits, ProviderIdentifier);
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

    public bool EnabledByDefault => true;

    public string? Description => "Public-domain ebooks in many languages.";

    // --- IProviderDownloadPolicy ----------------------------------------
    // Suffix-matched (see ProviderHostPolicy), so www.gutenberg.org and any
    // mirror on the same domain are covered by one entry. The second entry is
    // Project Gutenberg's own mirror (see MirrorFor), named in full so nothing
    // else on pglaf.org is reachable. A single ebook is at most a couple of
    // dozen megabytes, even illustrated; the headroom is there so a genuinely
    // large volume is not rejected, not because we expect it.
    public IReadOnlyList<string> AllowedHosts => ["gutenberg.org", GutenbergCatalog.MirrorHost];

    /// <summary>
    /// gutenberg.org goes down for hours at a time while its mirror keeps
    /// serving the same generated files under the same path. Only
    /// <c>/cache/epub/</c> is mirrored that way — the catalogue pages are not.
    /// </summary>
    public Uri? MirrorFor(Uri url)
    {
        if (!ProviderHostPolicy.IsAllowed(url.Host, ["gutenberg.org"])
            || !url.AbsolutePath.StartsWith("/cache/epub/", StringComparison.Ordinal))
        {
            return null;
        }

        return new UriBuilder(url) { Host = GutenbergCatalog.MirrorHost, Port = -1 }.Uri;
    }

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

        var feed = await _catalog.LoadAsync(path, retryDelays: null, ct);
        if (feed is null)
            return new ProviderSearchPage([], HasMore: false);

        var books = GutenbergCatalog.ParseSearch(feed);
        var perPage = GutenbergCatalog.ItemsPerPage(feed);

        var items = books.Select(book => _mapper.ToProviderItem(book, includeAssets: false)).ToList();

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
        return book is null ? null : _mapper.ToProviderItem(book, includeAssets: true);
    }

    // --- IProviderSnapshotSource ------------------------------------------

    /// <summary>
    /// Reads the whole catalogue from Gutenberg's daily machine-readable
    /// archive (a zipped tar of one RDF file per ebook). The probe is a HEAD,
    /// so a host's conditional re-check never transfers the 177 MB body; the
    /// archive is downloaded and parsed lazily on the first enumeration, and
    /// its temp file is deleted when enumeration ends, whatever ended it.
    ///
    /// The archive changes once a day and carries Last-Modified, so a
    /// not-modified probe is the normal outcome of the daily re-check. The live
    /// search cache is deliberately not used: this must see the source's own
    /// validators and the full catalogue rather than search-shaped pages.
    /// </summary>
    public async Task<ProviderSnapshot> ReadAsync(ProviderSnapshotRequest request, CancellationToken ct)
    {
        using var response = await _catalog.ProbeSnapshotAsync(
            request,
            _snapshotDownloadOptions.MaxDuration,
            ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return ProviderSnapshot.NotModified(
                etag: request.ETag ?? response.Headers.ETag?.ToString(),
                lastModified: request.IfModifiedSince ?? response.Content.Headers.LastModified);
        }

        if (!response.IsSuccessStatusCode)
            throw ProviderException.UnavailableFor(
                Id,
                "the catalogue snapshot answered HTTP " + (int)response.StatusCode);

        return new ProviderSnapshot(
            ProviderSnapshotStatus.Updated,
            ReadSnapshotItemsAsync(
                response.Content.Headers.ContentLength,
                GutenbergSnapshotValidator.FromResponse(response)),
            ETag: response.Headers.ETag?.ToString(),
            LastModified: response.Content.Headers.LastModified);
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
        return book is null ? null : _mapper.PlanAcquisition(book, request.AssetId);
    }

    // --- internals -------------------------------------------------------

    private async Task<GutenbergBook?> LoadBookAsync(string externalId, CancellationToken ct)
    {
        var id = externalId?.Trim() ?? string.Empty;

        // A non-numeric id cannot address an ebook, and is rejected before it
        // reaches a request path rather than after.
        if (!EbookId().IsMatch(id))
            return null;

        // The item lookup is the first step of every import, and gutenberg.org
        // answers it with a passing 502/503/504 often enough that failing the
        // whole import on one of them is the wrong default. Search keeps a
        // single attempt: the user is looking at it and can simply search again.
        var feed = await _catalog.LoadAsync($"/ebooks/{id}.opds", CatalogueRetryDelays, ct);
        return feed is null ? null : GutenbergCatalog.ParseDetail(feed, id);
    }

    /// <summary>
    /// The waits between attempts at an item lookup that met a transient
    /// gateway error; one retry per entry. Settable so a test does not sleep.
    /// </summary>
    internal IReadOnlyList<TimeSpan> CatalogueRetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// The snapshot items. Nothing exists until the first enumeration: the
    /// archive is downloaded on the first <c>MoveNextAsync</c> and the temp file
    /// is removed in the <c>finally</c>, which runs when the consumer breaks,
    /// throws or cancels as well as on normal completion.
    /// </summary>
    private async IAsyncEnumerable<ProviderItem> ReadSnapshotItemsAsync(
        long? probedContentLength,
        GutenbergSnapshotValidator probedValidator,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = _scratch.CreateFilePath(SnapshotTempFilePrefix, ".zip");

        try
        {
            await _snapshotDownloader.DownloadAsync(path, probedContentLength, probedValidator, ct);

            await foreach (var book in _snapshotReader.ReadAsync(path, ct))
                yield return _mapper.ToProviderItem(book, includeAssets: false);
        }
        finally
        {
            TryDeleteSnapshotFile(path);
        }
    }

    private void TryDeleteSnapshotFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover 177 MB temp file is worth knowing about; it must not
            // mask the enumeration result that brought us here.
            _logger.LogWarning(ex, "Could not delete the Gutenberg snapshot file {Path}.", path);
        }
    }
}

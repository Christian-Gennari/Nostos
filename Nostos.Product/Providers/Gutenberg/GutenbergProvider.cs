using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
/// Uses Gutenberg's machine-readable catalogue rather than the human-facing
/// website: the OPDS/Atom feeds an e-reader would use for live search and
/// detail, and the daily RDF archive for the bulk snapshot a host maintains its
/// own synchronized discovery index from.
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

    private const string SnapshotTempFilePrefix = "nostos-gutenberg-";

    /// <summary>
    /// Gutenberg's ebook ids are numeric. Validated before the id is used to
    /// build a request path, so a client cannot walk out of the catalogue with a
    /// crafted id.
    /// </summary>
    [GeneratedRegex(@"^\d{1,7}$")]
    private static partial Regex EbookId();

    private const long MaxEbookBytes = 96L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly HttpClient _snapshotHttp;
    private readonly ILogger<GutenbergProvider> _logger;

    public GutenbergProvider(IHttpClientFactory httpClientFactory, ILogger<GutenbergProvider> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _snapshotHttp = httpClientFactory.CreateClient(SnapshotHttpClientName);
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
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Head, GutenbergCatalog.SnapshotPath);
            ApplyValidators(probe, request);

            using var response = await _snapshotHttp.SendAsync(
                probe,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return ProviderSnapshot.NotModified(
                    etag: request.ETag ?? response.Headers.ETag?.ToString(),
                    lastModified: request.IfModifiedSince ?? response.Content.Headers.LastModified);
            }

            if (!response.IsSuccessStatusCode)
                throw ProviderException.UnavailableFor(Id, "the catalogue snapshot answered HTTP " + (int)response.StatusCode);

            return new ProviderSnapshot(
                ProviderSnapshotStatus.Updated,
                ReadSnapshotItemsAsync(),
                ETag: response.Headers.ETag?.ToString(),
                LastModified: response.Content.Headers.LastModified);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
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

    /// <summary>
    /// The snapshot items. Nothing exists until the first enumeration: the
    /// archive is downloaded on the first <c>MoveNextAsync</c> and the temp file
    /// is removed in the <c>finally</c>, which runs when the consumer breaks,
    /// throws or cancels as well as on normal completion.
    /// </summary>
    private async IAsyncEnumerable<ProviderItem> ReadSnapshotItemsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            SnapshotTempFilePrefix + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await DownloadSnapshotAsync(path, ct);

            await using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var zip = OpenSnapshotArchive(file);

            var tarEntry = zip.Entries.FirstOrDefault(entry =>
                entry.FullName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                ?? throw ProviderException.InvalidResponse(Id, "the snapshot archive has no tar catalog");

            await using var tarStream = tarEntry.Open();
            using var tar = new TarReader(tarStream);

            while (await ReadSnapshotItemAsync(tar, ct) is { } item)
                yield return item;
        }
        finally
        {
            TryDeleteSnapshotFile(path);
        }
    }

    private async Task DownloadSnapshotAsync(string path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GutenbergCatalog.SnapshotPath);
            using var response = await _snapshotHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (!response.IsSuccessStatusCode)
                throw ProviderException.UnavailableFor(Id, "the catalogue snapshot answered HTTP " + (int)response.StatusCode);

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);

            await source.CopyToAsync(destination, ct);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw ProviderException.UnavailableFor(Id, ex.Message);
        }
    }

    /// <summary>
    /// One item per call, skipping anything in the tar that is not an ebook RDF
    /// file and stopping the enumeration at the end of the archive. Parse
    /// failures are translated here because an iterator cannot catch and
    /// <c>yield</c> in the same block.
    /// </summary>
    private async Task<ProviderItem?> ReadSnapshotItemAsync(TarReader tar, CancellationToken ct)
    {
        while (true)
        {
            TarEntry? entry;
            try
            {
                entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct);
            }
            catch (InvalidDataException ex)
            {
                throw ProviderException.InvalidResponse(Id, ex.Message);
            }

            if (entry is null)
                return null;

            ct.ThrowIfCancellationRequested();

            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null)
                continue;

            if (GutenbergCatalog.SnapshotIdFromEntryName(entry.Name) is not { } id)
                continue;

            GutenbergBook? book;
            try
            {
                var document = await XDocument.LoadAsync(entry.DataStream, LoadOptions.None, ct);
                book = GutenbergCatalog.ParseSnapshot(id, document);
            }
            catch (XmlException ex)
            {
                throw ProviderException.InvalidResponse(Id, $"snapshot entry '{entry.Name}': {ex.Message}");
            }

            if (book is not null)
                return ToProviderItem(book, includeAssets: false);
        }
    }

    private ZipArchive OpenSnapshotArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
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

    private static void ApplyValidators(HttpRequestMessage request, ProviderSnapshotRequest snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ETag)
            && EntityTagHeaderValue.TryParse(snapshot.ETag, out var entityTag))
        {
            request.Headers.IfNoneMatch.Add(entityTag);
        }

        if (snapshot.IfModifiedSince is { } ifModifiedSince)
            request.Headers.IfModifiedSince = ifModifiedSince;
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

using System.Net;
using System.Text.Json;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.LibriVox;

/// <summary>
/// LibriVox as a Nostos content source.
///
/// LibriVox publishes a recording as a numbered list of MP3 sections. Nostos
/// models a book as ONE local file, so this provider declares
/// <see cref="ProviderCapabilities.RequiresAssembly"/> and the acquisition
/// pipeline combines the sections into a single chaptered M4B through
/// <see cref="LibriVoxM4bAssembler"/>. The multi-track shape is absorbed at
/// acquisition and never becomes a concept the library, the audio reader, the
/// progress model, downloads or backups have to learn.
///
/// Everything LibriVox-shaped stays inside this namespace.
/// </summary>
public sealed class LibriVoxProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy,
    IAcquisitionAssembler
{
    public const string ProviderIdentifier = "librivox";

    /// <summary>Named client for the catalogue, so timeouts and headers live in DI.</summary>
    public const string HttpClientName = "librivox";

    /// <summary>
    /// A single LibriVox section is at most a couple of hours of 64 kbps mono
    /// speech (well under 100 MB). The ceiling is set generously so a long
    /// section is not rejected, not because one is expected.
    /// </summary>
    private const long MaxBytesPerSection = 256L * 1024 * 1024;

    /// <summary>
    /// The longest LibriVox recordings run to roughly sixty hours, which is about
    /// 1.7 GB of 64 kbps mono audio. The cap exists to bound a runaway download,
    /// not to turn away a genuinely long book.
    /// </summary>
    private const long MaxTotalBytesForRecording = 3L * 1024 * 1024 * 1024;

    /// <summary>
    /// A recording's section count. LibriVox's longest multi-part works sit well
    /// below this; the limit bounds the number of files one import can fetch.
    /// </summary>
    private const int MaxSectionsPerRecording = 300;

    private const int PageSize = 20;
    private const int MaxPageSize = 100;

    private readonly HttpClient _http;
    private readonly ILogger<LibriVoxProvider> _logger;
    private readonly LibriVoxM4bAssembler _assembler;

    public LibriVoxProvider(
        IHttpClientFactory httpClientFactory,
        LibriVoxM4bAssembler assembler,
        ILogger<LibriVoxProvider> logger)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _assembler = assembler;
        _logger = logger;
    }

    public string Id => ProviderIdentifier;

    public string DisplayName => "LibriVox";

    /// <summary>
    /// <see cref="ProviderCapabilities.RequiresAssembly"/> is the honest
    /// declaration here: what LibriVox serves is a track set, and nothing
    /// downstream may treat a downloaded section as the book.
    /// </summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Search
        | ProviderCapabilities.ItemRetrieval
        | ProviderCapabilities.AudiobookAcquisition
        | ProviderCapabilities.CoverArt
        | ProviderCapabilities.RightsInformation
        | ProviderCapabilities.RequiresAssembly;

    public string? RightsNotice => LibriVoxCatalog.RightsStatement;

    // --- IProviderDownloadPolicy ----------------------------------------
    // Suffix-matched, so the dn*.ca.archive.org host that archive.org redirects
    // downloads to is covered by the archive.org entry along with any mirror.
    public IReadOnlyList<string> AllowedHosts => ["archive.org", "librivox.org"];

    public long MaxBytesPerPart => MaxBytesPerSection;

    public long MaxTotalBytes => MaxTotalBytesForRecording;

    /// <summary>One part per section: the concatenation order IS the recording.</summary>
    public int MaxParts => MaxSectionsPerRecording;

    // --- IAcquisitionAssembler -------------------------------------------
    // The registry resolves an assembler from the registered provider object, so
    // the provider IS the assembler and forwards to the class that owns the
    // media pipeline. Declaring RequiresAssembly without implementing this
    // interface would stop the app at startup, which is the point: a track set
    // must never reach the library as if it were the book.

    public Task<AcquisitionArtifact> AssembleAsync(AcquisitionAssemblyContext context, CancellationToken ct) =>
        _assembler.AssembleAsync(context, ct);

    // --- IProviderSearch -------------------------------------------------

    /// <summary>
    /// LibriVox's feed has no free-text search: a title query matches only
    /// titles that START WITH the text, and there is no substring matching at
    /// all. So the provider searches titles first and falls back to authors when
    /// that finds nothing, and says so in the notice rather than letting a thin
    /// result set read as a broken catalogue.
    /// </summary>
    public async Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct)
    {
        var text = query.Query?.Trim() ?? string.Empty;
        if (text.Length < 2)
        {
            // Too short to be a meaningful prefix; asking the catalogue would
            // only return an arbitrary slice of it.
            return new ProviderSearchPage([], HasMore: false,
                Notice: "Type at least two characters to search LibriVox.");
        }

        var limit = Math.Clamp(query.Limit <= 0 ? PageSize : query.Limit, 1, MaxPageSize);
        var offset = Math.Max(0, query.Offset);

        var byTitle = await SearchFieldAsync("title", text, limit, offset, ct);

        if (byTitle.Count > 0)
        {
            var more = byTitle.Count >= limit;
            return new ProviderSearchPage(
                Items: byTitle.Select(book => ToProviderItem(book, includeAssets: false)).ToList(),
                HasMore: more,
                Notice: "LibriVox matches titles that start with your text. Add more words to narrow it down.");
        }

        // Only worth a second request on the first page: the fallback is a
        // different query, so its "next page" would not continue this one.
        if (offset > 0)
            return new ProviderSearchPage([], HasMore: false, Notice: NoMatchesNotice(text));

        var byAuthor = await SearchFieldAsync("author", text, limit, offset, ct);
        if (byAuthor.Count > 0)
        {
            return new ProviderSearchPage(
                Items: byAuthor.Select(book => ToProviderItem(book, includeAssets: false)).ToList(),
                HasMore: byAuthor.Count >= limit,
                Notice: $"No title matched “{text}”, so these are recordings by a reader or author matching it. LibriVox cannot search inside titles.");
        }

        return new ProviderSearchPage([], HasMore: false, Notice: NoMatchesNotice(text));
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
        // Re-read from the source rather than trusting whatever the UI last saw:
        // the plan is what actually gets downloaded, so a section list that has
        // changed is picked up, and an item that has gone is a clean "not found"
        // instead of a set of dead URLs.
        var book = await LoadBookAsync(request.ExternalId, ct);
        if (book is null)
            return null;

        // There is exactly one asset. A caller naming a different one is asking
        // for something this source does not offer, and that fails loudly rather
        // than silently importing the M4B anyway.
        var assetId = request.AssetId?.Trim();
        if (!string.IsNullOrEmpty(assetId) &&
            !string.Equals(assetId, LibriVoxCatalog.AudiobookAssetId, StringComparison.OrdinalIgnoreCase))
            throw ProviderException.AssetUnavailableFor(Id, book.Id, assetId);

        if (book.Sections.Count == 0)
            throw ProviderException.InvalidResponse(
                Id, $"recording {book.Id} lists no downloadable sections");

        if (book.Sections.Count > MaxSectionsPerRecording)
            throw ProviderException.UnavailableFor(
                Id, $"recording {book.Id} has {book.Sections.Count} sections, more than the {MaxSectionsPerRecording} this server will import");

        return new ProviderAcquisitionPlan(
            ProviderId: Id,
            ExternalId: book.Id,
            Asset: AudioAsset,
            Metadata: MetadataFor(book),
            // One part per section, in playing order. The DOWNLOAD ORDER here is
            // the concatenation order, which is why the catalogue sorts by
            // section number rather than trusting the array.
            Parts: book.Sections
                .Select(section => new ProviderDownloadPart(
                    Url: section.ListenUrl,
                    FileExtension: ".mp3",
                    // The feed publishes no sizes; the downloader streams and
                    // enforces its own cap instead of trusting a declared length.
                    ExpectedBytes: null,
                    Label: section.Title))
                .ToList(),
            Output: new ProviderOutput(".m4b", "audio/mp4", "M4B audiobook"),
            Cover: book.Cover,
            Source: new ProviderSourceInfo(
                ItemUrl: book.Url,
                RightsStatement: LibriVoxCatalog.RightsStatement,
                RightsUrl: LibriVoxCatalog.RightsUrl),
            // Chapters are deliberately NOT guessed here: the assembler measures
            // them from the files that were actually downloaded, which is the
            // only source of chapter boundaries that does not drift.
            Chapters: null);
    }

    // --- internals -------------------------------------------------------

    private static ProviderAsset AudioAsset => new(
        Id: LibriVoxCatalog.AudiobookAssetId,
        Kind: ProviderMediaKind.Audiobook,
        Label: "M4B audiobook (single file)",
        SourceFormat: "librivox-mp3-sections",
        SizeBytes: null,
        IsPreferred: true);

    private static ProviderMetadata MetadataFor(LibriVoxCatalog.Book book) => new(
        Title: book.Title,
        Author: book.Author,
        Description: book.Description,
        Language: book.Language,
        PublishedDate: book.PublishedDate,
        Categories: book.Categories,
        Narrator: book.Narrator,
        Duration: book.Duration);

    private ProviderItem ToProviderItem(LibriVoxCatalog.Book book, bool includeAssets) => new(
        ProviderId: Id,
        ExternalId: book.Id,
        MediaKind: ProviderMediaKind.Audiobook,
        Metadata: MetadataFor(book),
        // Discovery remains thin. Detail exposes the one normalized M4B asset;
        // raw MP3 sections never become user-selectable formats.
        Assets: includeAssets ? [AudioAsset] : [],
        Cover: book.Cover,
        Source: new ProviderSourceInfo(
            ItemUrl: book.Url,
            RightsStatement: LibriVoxCatalog.RightsStatement,
            RightsUrl: LibriVoxCatalog.RightsUrl),
        PartCount: book.Sections.Count == 0 ? null : book.Sections.Count);

    private static string NoMatchesNotice(string text) =>
        $"No LibriVox recording matched “{text}”. Its catalogue only matches a title that starts with your text, so try fewer — or exactly the opening — words, or a reader's name.";

    private async Task<List<LibriVoxCatalog.Book>> SearchFieldAsync(
        string field,
        string text,
        int limit,
        int offset,
        CancellationToken ct)
    {
        // '^' is the feed's own prefix operator; escaping it is harmless and
        // keeps the whole value uniformly encoded.
        var value = Uri.EscapeDataString($"^{text}");
        var path = $"{LibriVoxCatalog.ApiPath}?{field}={value}&format=json&extended=1&limit={limit}&offset={offset}";

        using var document = await LoadJsonAsync(path, ct);
        if (document is null)
            return [];

        if (!document.RootElement.TryGetProperty("books", out var books) ||
            books.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<LibriVoxCatalog.Book>();
        foreach (var element in books.EnumerateArray())
        {
            var book = LibriVoxCatalog.ParseBook(element);
            if (book is not null)
                results.Add(book);
        }

        return results;
    }

    private async Task<LibriVoxCatalog.Book?> LoadBookAsync(string externalId, CancellationToken ct)
    {
        var id = externalId?.Trim() ?? string.Empty;

        // A non-numeric id cannot address a recording, and is rejected before it
        // reaches a request rather than after.
        if (!LibriVoxCatalog.IsValidId(id))
            return null;

        using var document = await LoadJsonAsync($"{LibriVoxCatalog.ApiPath}?id={id}&format=json&extended=1", ct);
        if (document is null)
            return null;

        if (!document.RootElement.TryGetProperty("books", out var books) ||
            books.ValueKind != JsonValueKind.Array ||
            books.GetArrayLength() == 0)
            return null;

        return LibriVoxCatalog.ParseBook(books[0]);
    }

    /// <summary>
    /// Fetches and parses a feed document. A 404 is the catalogue's way of
    /// saying "nothing matched" — an ordinary empty result, not a failure. A
    /// body that is not the expected JSON is a source problem and is reported as
    /// one, never passed off as an empty catalogue.
    /// </summary>
    private async Task<JsonDocument?> LoadJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return null;

        if (!response.IsSuccessStatusCode)
            throw ProviderException.UnavailableFor(Id, $"the catalogue answered HTTP {(int)response.StatusCode}");

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }
}

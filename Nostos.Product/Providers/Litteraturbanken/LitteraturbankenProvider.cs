using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Litteraturbanken;

/// <summary>
/// Litteraturbanken's public catalog, exposing only listed EPUB/PDF files whose
/// exact per-work license is CC0, public domain, or CC BY.
///
/// This type is the provider contract and its paging orchestration only. The
/// catalog query, row parsing and cursor live in
/// <see cref="LitteraturbankenCatalog"/>, HTTP in
/// <see cref="LitteraturbankenCatalogClient"/>, the work/asset mapping in
/// <see cref="LitteraturbankenItemMapper"/> and the license/provenance rendering
/// in <see cref="LitteraturbankenRightsCatalog"/>.
/// </summary>
public sealed class LitteraturbankenProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy,
    IProviderSnapshotSource
{
    public const string ProviderIdentifier = "litteraturbanken";
    public const string HttpClientName = "litteraturbanken";
    public const string BaseUrl = "https://litteraturbanken.se";

    private const long MaxFileBytes = 128L * 1024 * 1024;

    private readonly LitteraturbankenCatalogClient _catalogClient;
    private readonly LitteraturbankenItemMapper _mapper;

    public LitteraturbankenProvider(IHttpClientFactory httpClientFactory)
    {
        _catalogClient = new LitteraturbankenCatalogClient(httpClientFactory, ProviderIdentifier);
        _mapper = new LitteraturbankenItemMapper(ProviderIdentifier, MaxFileBytes);
    }

    public string Id => ProviderIdentifier;
    public string DisplayName => "Litteraturbanken";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Search
        | ProviderCapabilities.ItemRetrieval
        | ProviderCapabilities.EbookAcquisition
        | ProviderCapabilities.RightsInformation;

    public string? RightsNotice =>
        "Automated access is not confirmed by Litteraturbanken; only source-reported CC0, public-domain, or CC BY items are offered.";

    public bool EnabledByDefault => false;

    /// <summary>
    /// Automated access has not been confirmed by Litteraturbanken, so the
    /// source is withheld from every install. Removing this override restores
    /// the provider.
    /// </summary>
    public bool IsWithdrawn => true;

    /// <summary>Delay between catalog pages to keep a full sync courteous.</summary>
    public static readonly TimeSpan SnapshotPageDelay = TimeSpan.FromSeconds(1);

    public string? Description =>
        "Swedish texts with source-reported CC0, public-domain, or CC BY rights.";

    public IReadOnlyList<string> AllowedHosts => ["litteraturbanken.se"];
    public long MaxBytesPerPart => MaxFileBytes;
    public long MaxTotalBytes => MaxFileBytes;
    public int MaxParts => 1;

    public async Task<ProviderSearchPage> SearchAsync(
        ProviderSearchQuery query,
        CancellationToken ct)
    {
        if (query.Kind is not null && query.Kind != ProviderMediaKind.Ebook)
            return new ProviderSearchPage([], HasMore: false);

        var searchText = query.Query.Trim();
        if (searchText.Length < 2)
            return new ProviderSearchPage([], HasMore: false);

        var limit = Math.Clamp(query.Limit, 1, LitteraturbankenCatalog.MaxSearchLimit);
        var offset = Math.Max(0, query.Offset);
        if (offset >= LitteraturbankenCatalog.MaxSearchRows / 2)
        {
            return new ProviderSearchPage(
                [],
                HasMore: false,
                Notice: "Use a more specific search to narrow Litteraturbanken results.");
        }

        // One bounded request covers both source media types. Rows for a work
        // are combined only after each file and its rights have been checked.
        var rowsToRead = (int)Math.Min(
            LitteraturbankenCatalog.MaxSearchRows,
            Math.Max(2L, (offset + (long)limit + 1) * 2));
        var response = await _catalogClient.LoadPageAsync(
            LitteraturbankenCatalog.BuildSearchQuery(searchText),
            from: 0,
            to: rowsToRead,
            ct);

        var records = response.Data
            .Select(LitteraturbankenCatalog.ToRecord)
            .Where(record => record is not null)
            .Select(record => record!)
            .Where(LitteraturbankenCatalog.IsRightsAllowed)
            .ToList();

        if (records.Count == 0)
            return new ProviderSearchPage([], HasMore: response.Hits > response.Data.Count);

        var rights = await _catalogClient.GetRightsCatalogAsync(ct);
        var items = records
            .GroupBy(record => record.ExternalId, StringComparer.Ordinal)
            .Select(group => _mapper.BuildWork(group, rights))
            .Where(work => work is not null)
            .Select(work => work!)
            .ToList();

        var page = items.Skip(offset).Take(limit)
            .Select(work => work.Item with { Assets = [] })
            .ToList();
        var hasMore = items.Count > offset + limit || response.Hits > response.Data.Count;

        return new ProviderSearchPage(page, HasMore: hasMore);
    }

    /// <summary>
    /// Reads a complete, rights-filtered catalog for hosts that maintain a
    /// synchronized discovery index. Litteraturbanken exposes no incremental
    /// feed or reliable catalog-wide validator, so each sync is a full scan.
    /// Pages contain metadata only; downloads are resolved against the live
    /// catalog during acquisition.
    /// </summary>
    public async Task<ProviderSnapshot> ReadAsync(
        ProviderSnapshotRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cursor = request.Cursor is { Length: > 0 } encoded
            ? SnapshotCursor.Decode(encoded, Id)
            : SnapshotCursor.Start;

        if (cursor.PagesRead > 0)
            await Task.Delay(SnapshotPageDelay, ct);

        if (cursor.PagesRead >= LitteraturbankenCatalog.MaxSnapshotPages)
        {
            throw ProviderException.InvalidResponse(
                Id,
                $"the catalog exceeded {LitteraturbankenCatalog.MaxSnapshotPages} sequential pages");
        }

        // The catalog cannot be paged past 10,000 rows by offset (it answers
        // HTTP 500), and its default sort scatters the rows of one work across
        // the whole result. Page by key instead: sort by work id, start every
        // request at offset 0, and resume from the first row of the work that
        // straddled the previous page.
        var response = await _catalogClient.LoadPageAsync(
            LitteraturbankenCatalog.BuildSnapshotQuery(cursor.AfterWorkId),
            from: 0,
            to: LitteraturbankenCatalog.SnapshotPageSize,
            ct,
            LitteraturbankenCatalog.SnapshotSortField);
        if (response.Hits < response.Data.Count)
            throw ProviderException.InvalidResponse(Id, "the catalog total was smaller than its returned page");

        var hasMore = response.Hits > response.Data.Count;

        if (hasMore && response.Data.Count == 0)
            throw ProviderException.InvalidResponse(Id, "the catalog page was empty before its reported end");

        // Keep a work whose rows straddle this page together. The following
        // request restarts from the first row of that work, so the host stores
        // one metadata item with the full set of eligible formats.
        var trailingWorkId = hasMore
            ? LitteraturbankenCatalog.NormalizeWorkKey(response.Data[^1].ExternalId)
            : null;
        if (hasMore && trailingWorkId is null)
            throw ProviderException.InvalidResponse(Id, "the catalog returned an unusable work id");

        var trailingRows = trailingWorkId is null
            ? 0
            : response.Data
                .AsEnumerable()
                .Reverse()
                .TakeWhile(row => string.Equals(
                    LitteraturbankenCatalog.NormalizeWorkKey(row.ExternalId),
                    trailingWorkId,
                    StringComparison.Ordinal))
                .Count();
        var usableCount = response.Data.Count - trailingRows;
        var records = response.Data
            .Take(usableCount)
            .Select(LitteraturbankenCatalog.ToRecord)
            .Where(record => record is not null)
            .Select(record => record!)
            .Where(LitteraturbankenCatalog.IsRightsAllowed)
            .ToList();

        var rights = await _catalogClient.GetRightsCatalogAsync(ct);
        var items = records
            .GroupBy(record => record.ExternalId, StringComparer.Ordinal)
            .Select(group => _mapper.BuildWork(group, rights))
            .Where(work => work is not null)
            .Select(work => work!.Item with { Assets = [] })
            .ToList();

        string? nextCursor = null;
        if (hasMore)
        {
            // Rows are sorted by work id, so the restart key must move forward.
            if (usableCount == 0
                || (cursor.AfterWorkId is not null
                    && string.CompareOrdinal(trailingWorkId, cursor.AfterWorkId) <= 0))
            {
                throw ProviderException.InvalidResponse(
                    Id,
                    "one work exceeded the catalog page size");
            }

            if (cursor.PagesRead + 1 >= LitteraturbankenCatalog.MaxSnapshotPages)
            {
                throw ProviderException.InvalidResponse(
                    Id,
                    $"the catalog exceeded {LitteraturbankenCatalog.MaxSnapshotPages} sequential pages");
            }

            nextCursor = cursor.Next(trailingWorkId!).Encode();
        }

        return new ProviderSnapshot(
            ProviderSnapshotStatus.Updated,
            items.ToAsyncEnumerable(),
            NextCursor: nextCursor);
    }

    public async Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct)
    {
        var work = await LoadWorkAsync(externalId, ct);
        return work?.Item;
    }

    public async Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
        ProviderAcquisitionRequest request,
        CancellationToken ct)
    {
        var work = await LoadWorkAsync(request.ExternalId, ct);
        return work is null ? null : _mapper.PlanAcquisition(work, request.AssetId, request.ExternalId);
    }

    private async Task<WorkBundle?> LoadWorkAsync(string externalId, CancellationToken ct)
    {
        var id = LitteraturbankenCatalog.NormalizeExternalId(externalId);
        if (id is null)
            return null;

        var response = await _catalogClient.LoadPageAsync(
            LitteraturbankenCatalog.BuildItemQuery(id),
            from: 0,
            to: 10,
            ct);
        var records = response.Data
            .Select(LitteraturbankenCatalog.ToRecord)
            .Where(record => record is not null)
            .Select(record => record!)
            .Where(record =>
                string.Equals(record.ExternalId, id, StringComparison.Ordinal)
                && LitteraturbankenCatalog.IsRightsAllowed(record))
            .ToList();

        if (records.Count == 0)
            return null;

        var rights = await _catalogClient.GetRightsCatalogAsync(ct);
        return _mapper.BuildWork(records, rights);
    }
}

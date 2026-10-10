using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Litteraturbanken;

/// <summary>
/// Litteraturbanken's public catalog, exposing only listed EPUB/PDF files whose
/// exact per-work license is CC0, public domain, or CC BY.
/// </summary>
public sealed class LitteraturbankenProvider : IContentProvider,
    IProviderSearch,
    IProviderCatalog,
    IProviderAcquisitionPlanner,
    IProviderDownloadPolicy
{
    public const string ProviderIdentifier = "litteraturbanken";
    public const string HttpClientName = "litteraturbanken";
    public const string BaseUrl = "https://litteraturbanken.se";

    private const string CatalogPath = "/api/query_string/etext,faksimil";
    private const string LicensePath = "/red/etc/license/license.json";
    private const string ProvenancePath = "/red/etc/provenance/provenance.json";
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const int MaxSearchRows = 100;
    private const int MaxSearchLimit = 24;
    private const string IncludeFields =
        "lbworkid,titlepath,title,titleid,work_titleid,shorttitle,mediatype," +
        "authors.authorid,authors.full_name,export.type,export.size,license," +
        "provenance.library,provenance.signum,provenance.text2,printed," +
        "sort_date_imprint.plain";
    private const string ExcludeFields = "text,parts,sourcedesc,pages,errata";

    private static readonly string[] AllowedLicenseCodes = ["cc-0", "pd", "cc-by"];
    private static readonly HashSet<string> AllowedLicenses =
        new(AllowedLicenseCodes, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _rightsGate = new(1, 1);
    private RightsCatalog? _rightsCatalog;

    public LitteraturbankenProvider(IHttpClientFactory httpClientFactory)
    {
        _http = httpClientFactory.CreateClient(HttpClientName);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
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

        var limit = Math.Clamp(query.Limit, 1, MaxSearchLimit);
        var offset = Math.Max(0, query.Offset);
        if (offset >= MaxSearchRows / 2)
        {
            return new ProviderSearchPage(
                [],
                HasMore: false,
                Notice: "Use a more specific search to narrow Litteraturbanken results.");
        }

        // One bounded request covers both source media types. Rows for a work
        // are combined only after each file and its rights have been checked.
        var rowsToRead = (int)Math.Min(
            MaxSearchRows,
            Math.Max(2L, (offset + (long)limit + 1) * 2));
        var response = await LoadCatalogAsync(
            BuildSearchQuery(searchText),
            from: 0,
            to: rowsToRead,
            ct);

        var records = response.Data
            .Select(ToCatalogRecord)
            .Where(record => record is not null)
            .Select(record => record!)
            .Where(IsRightsAllowed)
            .ToList();

        if (records.Count == 0)
            return new ProviderSearchPage([], HasMore: response.Hits > response.Data.Count);

        var rights = await GetRightsCatalogAsync(ct);
        var items = records
            .GroupBy(record => record.ExternalId, StringComparer.Ordinal)
            .Select(group => BuildWork(group, rights))
            .Where(work => work is not null)
            .Select(work => work!)
            .ToList();

        var page = items.Skip(offset).Take(limit)
            .Select(work => work.Item with { Assets = [] })
            .ToList();
        var hasMore = items.Count > offset + limit || response.Hits > response.Data.Count;

        return new ProviderSearchPage(page, HasMore: hasMore);
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
        if (work is null)
            return null;

        var selected = request.AssetId is null
            ? work.Assets.FirstOrDefault(asset => asset.Asset.IsPreferred) ?? work.Assets.FirstOrDefault()
            : work.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Asset.Id, request.AssetId, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            throw ProviderException.AssetUnavailableFor(
                Id,
                request.ExternalId,
                request.AssetId ?? "ebook");
        }

        return new ProviderAcquisitionPlan(
            ProviderId: Id,
            ExternalId: work.Item.ExternalId,
            Asset: selected.Asset,
            Metadata: work.Item.Metadata,
            Parts:
            [
                new ProviderDownloadPart(
                    selected.Url,
                    selected.Output.FileExtension,
                    selected.Asset.SizeBytes,
                    selected.Output.Label),
            ],
            Output: selected.Output,
            Source: ToSourceInfo(selected.Record, work.Rights));
    }

    private async Task<WorkBundle?> LoadWorkAsync(string externalId, CancellationToken ct)
    {
        var id = NormalizeExternalId(externalId);
        if (id is null)
            return null;

        var response = await LoadCatalogAsync(
            BuildItemQuery(id),
            from: 0,
            to: 10,
            ct);
        var records = response.Data
            .Select(ToCatalogRecord)
            .Where(record => record is not null)
            .Select(record => record!)
            .Where(record =>
                string.Equals(record.ExternalId, id, StringComparison.Ordinal)
                && IsRightsAllowed(record))
            .ToList();

        if (records.Count == 0)
            return null;

        var rights = await GetRightsCatalogAsync(ct);
        return BuildWork(records, rights);
    }

    private async Task<CatalogPage> LoadCatalogAsync(
        string query,
        int from,
        int to,
        CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = from.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["to"] = to.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["include"] = IncludeFields,
            ["exclude"] = ExcludeFields,
            ["sort_field"] = "sortkey|asc",
            ["q"] = query,
        });
        var queryString = await form.ReadAsStringAsync(ct);

        using var response = await SendGetAsync(CatalogPath + "?" + queryString, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw ProviderException.UnavailableFor(
                Id,
                "the public catalog answered HTTP " + (int)response.StatusCode);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var result = await JsonSerializer.DeserializeAsync<CatalogPage>(
                stream,
                JsonOptions,
                ct);

            if (result?.Data is null)
                throw ProviderException.InvalidResponse(Id, "the catalog omitted its data list");

            return result;
        }
        catch (JsonException ex)
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    private async Task<HttpResponseMessage> SendGetAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private async Task<RightsCatalog> GetRightsCatalogAsync(CancellationToken ct)
    {
        if (_rightsCatalog is not null)
            return _rightsCatalog;

        await _rightsGate.WaitAsync(ct);
        try
        {
            if (_rightsCatalog is not null)
                return _rightsCatalog;

            var licenses = await GetJsonAsync<Dictionary<string, string>>(LicensePath, ct);
            var provenance = await GetJsonElementAsync(ProvenancePath, ct);
            _rightsCatalog = RightsCatalog.Parse(licenses, provenance);
            return _rightsCatalog;
        }
        finally
        {
            _rightsGate.Release();
        }
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendGetAsync(path, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw ProviderException.UnavailableFor(
                Id,
                "rights metadata answered HTTP " + (int)response.StatusCode);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct)
                ?? throw ProviderException.InvalidResponse(Id, "rights metadata was empty");
        }
        catch (JsonException ex)
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    private async Task<JsonElement> GetJsonElementAsync(string path, CancellationToken ct)
    {
        using var response = await SendGetAsync(path, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw ProviderException.UnavailableFor(
                Id,
                "provenance metadata answered HTTP " + (int)response.StatusCode);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw ProviderException.InvalidResponse(Id, ex.Message);
        }
    }

    private WorkBundle? BuildWork(IEnumerable<CatalogRecord> sourceRecords, RightsCatalog rights)
    {
        var records = sourceRecords
            .OrderBy(record => record.MediaType == "etext" ? 0 : 1)
            .ToList();
        if (records.Count == 0)
            return null;

        var sourcedAssets = records
            .SelectMany(record => BuildAssets(record, rights))
            .GroupBy(asset => asset.Asset.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var variants = group.ToList();
                var signatures = variants
                    .Select(variant => variant.Record.License + "|" + RightsSignature(variant.Record))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return signatures.Count == 1 ? variants[0] : null;
            })
            .Where(asset => asset is not null)
            .Select(asset => asset!)
            .OrderBy(asset => asset.Asset.Id == "epub" ? 0 : 1)
            .ToList();

        if (sourcedAssets.Count == 0)
            return null;

        var preferred = sourcedAssets.FirstOrDefault(asset => asset.Asset.Id == "epub")
            ?? sourcedAssets[0];
        var item = new ProviderItem(
            ProviderId: Id,
            ExternalId: preferred.Record.ExternalId,
            MediaKind: ProviderMediaKind.Ebook,
            Metadata: ToMetadata(preferred.Record),
            Assets: sourcedAssets.Select(asset => asset.Asset).ToList(),
            Source: ToWorkSourceInfo(preferred.Record, sourcedAssets, rights));

        return new WorkBundle(item, sourcedAssets, rights);
    }

    private static IEnumerable<SourcedAsset> BuildAssets(CatalogRecord record, RightsCatalog rights)
    {
        if (!IsRightsAllowed(record)
            || record.SourceItemUrl is null
            || !rights.TryBuildStatement(record, out _, out _))
        {
            yield break;
        }

        if (record.MediaType == "etext"
            && TryGetExportSize(record, "epub", out var epubBytes)
            && epubBytes <= MaxFileBytes)
        {
            var author = record.Authors.FirstOrDefault()?.AuthorId;
            var title = record.WorkTitleId ?? record.TitleId;
            if (!string.IsNullOrWhiteSpace(author) && !string.IsNullOrWhiteSpace(title))
            {
                var file = Uri.EscapeDataString(author + "_" + title) + ".epub";
                yield return new SourcedAsset(
                    new ProviderAsset(
                        "epub",
                        ProviderMediaKind.Ebook,
                        "EPUB",
                        "application/epub+zip",
                        epubBytes,
                        IsPreferred: true),
                    new Uri(BaseUrl + "/txt/epub/" + file),
                    new ProviderOutput(".epub", "application/epub+zip", "EPUB"),
                    record);
            }
        }

        if (record.MediaType == "faksimil"
            && TryGetExportSize(record, "pdf", out var pdfBytes)
            && pdfBytes <= MaxFileBytes)
        {
            yield return new SourcedAsset(
                new ProviderAsset(
                    "pdf",
                    ProviderMediaKind.Ebook,
                    "PDF",
                    "application/pdf",
                    pdfBytes),
                new Uri(BaseUrl + "/export/faksimil/" + record.ExternalId + ".pdf"),
                new ProviderOutput(".pdf", "application/pdf", "PDF"),
                record);
        }
    }

    private static ProviderMetadata ToMetadata(CatalogRecord record)
    {
        var title = string.IsNullOrWhiteSpace(record.ShortTitle)
            ? record.Title
            : record.ShortTitle.Trim();
        var authors = record.Authors
            .Select(author => author.FullName?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

        return new ProviderMetadata(
            Title: title,
            Subtitle: !string.Equals(title, record.Title, StringComparison.Ordinal) ? record.Title : null,
            Author: authors.Count == 0 ? null : string.Join("; ", authors),
            Language: "Swedish",
            Publisher: "Litteraturbanken",
            PublishedDate: record.SortDateImprint?.Plain);
    }

    private static ProviderSourceInfo ToWorkSourceInfo(
        CatalogRecord preferred,
        IReadOnlyList<SourcedAsset> assets,
        RightsCatalog rights)
    {
        var statements = new List<string>();
        var rightsUrls = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asset in assets)
        {
            if (!rights.TryBuildStatement(asset.Record, out var statement, out var rightsUrl)
                || string.IsNullOrWhiteSpace(statement))
            {
                continue;
            }

            var link = string.IsNullOrWhiteSpace(rightsUrl) ? string.Empty : " (" + rightsUrl + ")";
            statements.Add(asset.Asset.Label + ": " + statement + link);
            if (!string.IsNullOrWhiteSpace(rightsUrl))
                rightsUrls.Add(rightsUrl);
        }

        return new ProviderSourceInfo(
            ItemUrl: preferred.SourceItemUrl?.ToString(),
            RightsStatement: statements.Count == 0 ? null : string.Join(" ", statements),
            RightsUrl: rightsUrls.Count == 1 ? rightsUrls.Single() : null);
    }

    private static ProviderSourceInfo ToSourceInfo(CatalogRecord record, RightsCatalog rights)
    {
        rights.TryBuildStatement(record, out var statement, out var rightsUrl);
        return new ProviderSourceInfo(
            ItemUrl: record.SourceItemUrl?.ToString(),
            RightsStatement: statement,
            RightsUrl: rightsUrl);
    }

    private static bool TryGetExportSize(CatalogRecord record, string format, out long size)
    {
        var export = record.Exports.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, format, StringComparison.Ordinal));
        size = export?.Size ?? 0;
        return size > 0;
    }

    private static bool IsRightsAllowed(CatalogRecord record) =>
        AllowedLicenses.Contains(record.License ?? string.Empty)
        && record.SourceItemUrl is not null;

    private static CatalogRecord? ToCatalogRecord(CatalogRow row)
    {
        var externalId = NormalizeExternalId(row.ExternalId);
        var title = string.IsNullOrWhiteSpace(row.Title) ? null : row.Title.Trim();
        if (externalId is null || title is null || string.IsNullOrWhiteSpace(row.MediaType))
            return null;

        var authors = row.Authors ?? [];
        var author = authors.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.AuthorId));
        var titleId = row.WorkTitleId ?? row.TitleId;
        if (author is null || string.IsNullOrWhiteSpace(titleId))
            return null;

        var itemUrl = new Uri(
            BaseUrl
            + "/författare/"
            + Uri.EscapeDataString(author.AuthorId!)
            + "/titlar/"
            + Uri.EscapeDataString(titleId)
            + "/info");

        return new CatalogRecord(
            externalId,
            title,
            row.ShortTitle?.Trim(),
            titleId,
            row.WorkTitleId,
            row.MediaType,
            row.License,
            row.Printed,
            authors,
            row.Exports ?? [],
            row.Provenance ?? [],
            row.SortDateImprint,
            itemUrl);
    }

    private static string? NormalizeExternalId(string? value)
    {
        var id = value?.Trim();
        if (id is null || id.Length > 32 || id.Length < 3
            || !id.StartsWith("lb", StringComparison.Ordinal))
        {
            return null;
        }

        for (var index = 2; index < id.Length; index++)
        {
            if (!char.IsAsciiDigit(id[index]))
                return null;
        }

        return id;
    }

    private static string RightsSignature(CatalogRecord record) =>
        string.Join(
            "|",
            record.Provenance
                .Select(provenance => (provenance.Library ?? string.Empty) + ":" + (provenance.Signum ?? string.Empty))
                .OrderBy(value => value, StringComparer.Ordinal));

    private static string BuildSearchQuery(string searchText)
    {
        var escaped = EscapeQuery(searchText);
        return "@type=cross_fields @default_operator=AND "
            + "@fields=autocomplete.scandinavian "
            + escaped
            + " AND searchable:true AND show:true AND "
            + "(license:cc-0 OR license:pd OR license:cc-by)";
    }

    private static string BuildItemQuery(string externalId) =>
        "lbworkid:" + externalId
        + " AND searchable:true AND show:true AND "
        + "(license:cc-0 OR license:pd OR license:cc-by)";

    private static string EscapeQuery(string value)
    {
        const string special = "\\+-=&|><!(){}[]^\"~*?:/";
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (special.Contains(character))
                builder.Append('\\');
            builder.Append(character);
        }

        return builder.ToString();
    }

    private sealed record WorkBundle(
        ProviderItem Item,
        IReadOnlyList<SourcedAsset> Assets,
        RightsCatalog Rights);

    private sealed record SourcedAsset(
        ProviderAsset Asset,
        Uri Url,
        ProviderOutput Output,
        CatalogRecord Record);

    private sealed record CatalogRecord(
        string ExternalId,
        string Title,
        string? ShortTitle,
        string TitleId,
        string? WorkTitleId,
        string MediaType,
        string? License,
        bool Printed,
        List<CatalogAuthor> Authors,
        List<CatalogExport> Exports,
        List<CatalogProvenance> Provenance,
        CatalogDate? SortDateImprint,
        Uri? SourceItemUrl);

    private sealed class CatalogPage
    {
        [JsonPropertyName("hits")]
        public int Hits { get; init; }

        [JsonPropertyName("data")]
        public List<CatalogRow> Data { get; init; } = [];
    }

    private sealed class CatalogRow
    {
        [JsonPropertyName("lbworkid")]
        public string? ExternalId { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("shorttitle")]
        public string? ShortTitle { get; init; }

        [JsonPropertyName("titleid")]
        public string? TitleId { get; init; }

        [JsonPropertyName("work_titleid")]
        public string? WorkTitleId { get; init; }

        [JsonPropertyName("mediatype")]
        public string? MediaType { get; init; }

        [JsonPropertyName("license")]
        public string? License { get; init; }

        [JsonPropertyName("printed")]
        public bool Printed { get; init; }

        [JsonPropertyName("authors")]
        public List<CatalogAuthor>? Authors { get; init; }

        [JsonPropertyName("export")]
        public List<CatalogExport>? Exports { get; init; }

        [JsonPropertyName("provenance")]
        public List<CatalogProvenance>? Provenance { get; init; }

        [JsonPropertyName("sort_date_imprint")]
        public CatalogDate? SortDateImprint { get; init; }
    }

    private sealed class CatalogAuthor
    {
        [JsonPropertyName("authorid")]
        public string? AuthorId { get; init; }

        [JsonPropertyName("full_name")]
        public string? FullName { get; init; }
    }

    private sealed class CatalogExport
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }

    private sealed class CatalogProvenance
    {
        [JsonPropertyName("library")]
        public string? Library { get; init; }

        [JsonPropertyName("signum")]
        public string? Signum { get; init; }

        [JsonPropertyName("text2")]
        public bool Text2 { get; init; }
    }

    private sealed class CatalogDate
    {
        [JsonPropertyName("plain")]
        public string? Plain { get; init; }
    }

    private sealed record LicenseNotice(string Statement, string? RightsUrl);

    private sealed record ProvenanceNotice(
        IReadOnlyDictionary<string, string> Text,
        IReadOnlyDictionary<string, string> Text2);

    private sealed class RightsCatalog
    {
        private readonly IReadOnlyDictionary<string, LicenseNotice> _licenses;
        private readonly IReadOnlyDictionary<string, ProvenanceNotice> _provenance;

        private RightsCatalog(
            IReadOnlyDictionary<string, LicenseNotice> licenses,
            IReadOnlyDictionary<string, ProvenanceNotice> provenance)
        {
            _licenses = licenses;
            _provenance = provenance;
        }

        public static RightsCatalog Parse(
            IReadOnlyDictionary<string, string> licenseMarkup,
            JsonElement provenanceRoot)
        {
            var licenses = new Dictionary<string, LicenseNotice>(StringComparer.Ordinal);
            foreach (var code in AllowedLicenseCodes)
            {
                if (!licenseMarkup.TryGetValue(code, out var markup))
                    continue;

                try
                {
                    var document = XDocument.Parse(markup, LoadOptions.None);
                    var statement = NormalizeWhitespace(document.Root?.Value ?? string.Empty);
                    var licenseLink = document
                        .Descendants()
                        .FirstOrDefault(element =>
                            element.Name.LocalName == "a"
                            && string.Equals(
                                element.Attribute("rel")?.Value,
                                "license",
                                StringComparison.OrdinalIgnoreCase))
                        ?.Attribute("href")
                        ?.Value;

                    if (statement.Length > 0)
                    {
                        var safeLink = Uri.TryCreate(licenseLink, UriKind.Absolute, out var uri)
                            && uri.Scheme == Uri.UriSchemeHttps
                            && uri.Host == "creativecommons.org"
                            ? uri.ToString()
                            : null;
                        licenses[code] = new LicenseNotice(statement, safeLink);
                    }
                }
                catch (System.Xml.XmlException)
                {
                    // A malformed source license is not permission to expose a work.
                }
            }

            var provenance = new Dictionary<string, ProvenanceNotice>(StringComparer.Ordinal);
            if (provenanceRoot.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in provenanceRoot.EnumerateObject())
                {
                    provenance[entry.Name] = new ProvenanceNotice(
                        ReadTextMap(entry.Value, "text"),
                        ReadTextMap(entry.Value, "text2"));
                }
            }

            return new RightsCatalog(licenses, provenance);
        }

        public bool TryBuildStatement(
            CatalogRecord record,
            out string? statement,
            out string? rightsUrl)
        {
            statement = null;
            rightsUrl = null;
            if (record.License is null
                || !_licenses.TryGetValue(record.License, out var notice))
            {
                return false;
            }

            var provenanceText = BuildProvenanceText(record);
            var rendered = notice.Statement;
            if (rendered.Contains("{{provenance}}", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(provenanceText))
                    return false;

                rendered = rendered.Replace(
                    "{{provenance}}",
                    provenanceText,
                    StringComparison.Ordinal);
            }
            else if (!string.IsNullOrWhiteSpace(provenanceText))
            {
                rendered += " Provenance: " + provenanceText;
            }

            statement = rendered;
            rightsUrl = notice.RightsUrl;
            return true;
        }

        private string? BuildProvenanceText(CatalogRecord record)
        {
            if (record.Provenance.Count == 0)
                return null;

            var mediaType = record.MediaType == "faksimil"
                ? record.Printed ? "faksimilprint" : "faksimilnoprint"
                : "etext";
            var statements = new List<string>();

            for (var index = 0; index < record.Provenance.Count; index++)
            {
                var item = record.Provenance[index];
                if (string.IsNullOrWhiteSpace(item.Library)
                    || !_provenance.TryGetValue(item.Library, out var source))
                {
                    return null;
                }

                var sourceText = index > 0 && item.Text2
                    ? source.Text2.GetValueOrDefault(mediaType)
                    : null;
                if (string.IsNullOrWhiteSpace(sourceText))
                    sourceText = source.Text.GetValueOrDefault(mediaType);

                if (string.IsNullOrWhiteSpace(sourceText))
                    return null;

                var signum = string.IsNullOrWhiteSpace(item.Signum)
                    ? string.Empty
                    : " (" + item.Signum.Trim() + ")";
                statements.Add(sourceText.Replace(
                    "{{signum}}",
                    signum,
                    StringComparison.Ordinal));
            }

            return string.Join(" ", statements);
        }

        private static IReadOnlyDictionary<string, string> ReadTextMap(
            JsonElement source,
            string propertyName)
        {
            if (!source.TryGetProperty(propertyName, out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return map.EnumerateObject()
                .Where(entry => entry.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(
                    entry => entry.Name,
                    entry => entry.Value.GetString() ?? string.Empty,
                    StringComparer.Ordinal);
        }

        private static string NormalizeWhitespace(string value) =>
            string.Join(
                " ",
                WebUtility.HtmlDecode(value)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

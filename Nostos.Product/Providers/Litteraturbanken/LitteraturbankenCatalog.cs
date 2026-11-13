using System.Text;
using System.Text.Json.Serialization;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Litteraturbanken;

/// <summary>
/// Litteraturbanken's own vocabulary: the Lucene queries its catalog accepts,
/// the cursor that walks the catalog a page at a time, the JSON rows it answers
/// with, and the parsing of one row into a <see cref="CatalogRecord"/>.
///
/// Everything here is a pure function of its arguments, which is what lets the
/// query shape, the paging codec and the row filter be tested without HTTP.
/// </summary>
internal static class LitteraturbankenCatalog
{
    /// <summary>Rows a search may ask for, across both source media types.</summary>
    public const int MaxSearchRows = 100;

    /// <summary>Largest page a caller may request from one search.</summary>
    public const int MaxSearchLimit = 24;

    /// <summary>Rows per bulk-snapshot request.</summary>
    public const int SnapshotPageSize = 100;

    /// <summary>Bulk pages one snapshot may walk before the catalog is refused.</summary>
    public const int MaxSnapshotPages = 1_000;

    /// <summary>
    /// The catalog cannot be paged past 10,000 rows by offset (it answers HTTP
    /// 500), and its default sort scatters the rows of one work across the whole
    /// result, so a bulk sync pages by work id instead.
    /// </summary>
    public const string SnapshotSortField = "lbworkid|asc";

    public static string BuildSearchQuery(string searchText)
    {
        var escaped = EscapeQuery(searchText);
        return "@type=cross_fields @default_operator=AND "
            + "@fields=autocomplete.scandinavian "
            + escaped
            + " AND searchable:true AND show:true AND "
            + "(license:cc-0 OR license:pd OR license:cc-by)";
    }

    public static string BuildSnapshotQuery(string? afterWorkId) =>
        "searchable:true AND show:true AND "
        + "(license:cc-0 OR license:pd OR license:cc-by)"
        + (afterWorkId is null ? string.Empty : " AND lbworkid:>=" + afterWorkId);

    public static string BuildItemQuery(string externalId) =>
        "lbworkid:" + externalId
        + " AND searchable:true AND show:true AND "
        + "(license:cc-0 OR license:pd OR license:cc-by)";

    public static string EscapeQuery(string value)
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

    /// <summary>
    /// An id that can address an item: "lb" followed by 1–30 ASCII digits. A
    /// value that fails here is rejected before it reaches a request, so a
    /// crafted id cannot rewrite the catalog query.
    /// </summary>
    public static string? NormalizeExternalId(string? value)
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

    /// <summary>
    /// Validates a raw catalogue work id for use as a paging key. Wider than
    /// <see cref="NormalizeExternalId"/>: ids that are not offered as items
    /// still take part in the sort order the cursor walks.
    /// </summary>
    public static string? NormalizeWorkKey(string? value)
    {
        var id = value?.Trim();
        if (id is null || id.Length > 32 || id.Length < 3
            || !id.StartsWith("lb", StringComparison.Ordinal))
        {
            return null;
        }

        for (var index = 2; index < id.Length; index++)
        {
            if (!char.IsAsciiDigit(id[index]) && !char.IsAsciiLetterLower(id[index]))
                return null;
        }

        return id;
    }

    /// <summary>
    /// One catalog row as an item candidate, or null when the row cannot
    /// address an item at all (no usable id, title, media type, author or work
    /// title id).
    /// </summary>
    public static CatalogRecord? ToRecord(CatalogRow row)
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
            LitteraturbankenProvider.BaseUrl
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

    /// <summary>
    /// Only a listed license on a row that has an item page may be offered;
    /// anything else is not a license Nostos can redistribute.
    /// </summary>
    public static bool IsRightsAllowed(CatalogRecord record) =>
        LitteraturbankenRightsCatalog.AllowsLicense(record.License)
        && record.SourceItemUrl is not null;
}

/// <summary>
/// The bulk paging checkpoint. Rows are sorted by work id and a work's rows can
/// straddle a page boundary, so the cursor names the last work seen: the next
/// request restarts from its first row and the whole work is published once.
/// </summary>
internal readonly record struct SnapshotCursor(string? AfterWorkId, int PagesRead)
{
    private const string Version = "v2";

    public static SnapshotCursor Start => new(null, 0);

    public SnapshotCursor Next(string afterWorkId) => new(afterWorkId, PagesRead + 1);

    public string Encode() => string.Join(
        '|',
        Version,
        AfterWorkId ?? string.Empty,
        PagesRead.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static SnapshotCursor Decode(string value, string providerId)
    {
        var parts = value.Split('|');
        if (parts.Length != 3
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || LitteraturbankenCatalog.NormalizeWorkKey(parts[1]) is not { } afterWorkId
            || !int.TryParse(
                parts[2],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pagesRead)
            || pagesRead <= 0
            || pagesRead >= LitteraturbankenCatalog.MaxSnapshotPages)
        {
            throw ProviderException.InvalidResponse(providerId, "the catalog cursor was invalid");
        }

        return new SnapshotCursor(afterWorkId, pagesRead);
    }
}

internal sealed record SourcedAsset(
    ProviderAsset Asset,
    Uri Url,
    ProviderOutput Output,
    CatalogRecord Record);

/// <summary>One catalog row after parsing: the id and rights are known to be usable.</summary>
internal sealed record CatalogRecord(
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

/// <summary>One page of the catalog JSON.</summary>
internal sealed class CatalogPage
{
    [JsonPropertyName("hits")]
    public int Hits { get; init; }

    [JsonPropertyName("data")]
    public List<CatalogRow> Data { get; init; } = [];
}

internal sealed class CatalogRow
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

internal sealed class CatalogAuthor
{
    [JsonPropertyName("authorid")]
    public string? AuthorId { get; init; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }
}

internal sealed class CatalogExport
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

internal sealed class CatalogProvenance
{
    [JsonPropertyName("library")]
    public string? Library { get; init; }

    [JsonPropertyName("signum")]
    public string? Signum { get; init; }

    [JsonPropertyName("text2")]
    public bool Text2 { get; init; }
}

internal sealed class CatalogDate
{
    [JsonPropertyName("plain")]
    public string? Plain { get; init; }
}

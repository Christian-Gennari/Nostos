using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Litteraturbanken;

/// <summary>
/// Turns a work's catalog rows — one per advertised format — into the single
/// provider item Nostos shows, and resolves an acquisition request into the one
/// file it will download.
///
/// A work is only offered when at least one format is both listed and
/// rights-demonstrable, and every asset it exposes carries its own statement:
/// the EPUB and the facsimile can have different licenses and provenance.
/// </summary>
internal sealed class LitteraturbankenItemMapper
{
    private readonly string _providerId;
    private readonly long _maxFileBytes;

    public LitteraturbankenItemMapper(string providerId, long maxFileBytes)
    {
        _providerId = providerId;
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>
    /// The item for all rows of one work, or null when none of them yields an
    /// allowed, sized asset. Format ordering is deterministic: EPUB first,
    /// remaining formats by id, so the preferred asset is stable.
    /// </summary>
    public WorkBundle? BuildWork(
        IEnumerable<CatalogRecord> sourceRecords,
        LitteraturbankenRightsCatalog rights)
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
            ProviderId: _providerId,
            ExternalId: preferred.Record.ExternalId,
            MediaKind: ProviderMediaKind.Ebook,
            Metadata: ToMetadata(preferred.Record),
            Assets: sourcedAssets.Select(asset => asset.Asset).ToList(),
            Source: ToWorkSourceInfo(preferred.Record, sourcedAssets, rights));

        return new WorkBundle(item, sourcedAssets, rights);
    }

    /// <summary>
    /// The acquisition plan for <paramref name="assetId"/>, or the work's
    /// preferred asset when none is named. A named asset the work does not offer
    /// is a hard <c>provider_asset_unavailable</c>.
    /// </summary>
    public ProviderAcquisitionPlan PlanAcquisition(WorkBundle work, string? assetId, string externalId)
    {
        var selected = assetId is null
            ? work.Assets.FirstOrDefault(asset => asset.Asset.IsPreferred) ?? work.Assets.FirstOrDefault()
            : work.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Asset.Id, assetId, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
            throw ProviderException.AssetUnavailableFor(_providerId, externalId, assetId ?? "ebook");

        return new ProviderAcquisitionPlan(
            ProviderId: _providerId,
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

    private IEnumerable<SourcedAsset> BuildAssets(
        CatalogRecord record,
        LitteraturbankenRightsCatalog rights)
    {
        if (!LitteraturbankenCatalog.IsRightsAllowed(record)
            || record.SourceItemUrl is null
            || !rights.TryBuildStatement(record, out _, out _))
        {
            yield break;
        }

        if (record.MediaType == "etext"
            && TryGetExportSize(record, "epub", out var epubBytes)
            && epubBytes <= _maxFileBytes)
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
                    new Uri(LitteraturbankenProvider.BaseUrl + "/txt/epub/" + file),
                    new ProviderOutput(".epub", "application/epub+zip", "EPUB"),
                    record);
            }
        }

        if (record.MediaType == "faksimil"
            && TryGetExportSize(record, "pdf", out var pdfBytes)
            && pdfBytes <= _maxFileBytes)
        {
            yield return new SourcedAsset(
                new ProviderAsset(
                    "pdf",
                    ProviderMediaKind.Ebook,
                    "PDF",
                    "application/pdf",
                    pdfBytes),
                new Uri(LitteraturbankenProvider.BaseUrl + "/export/faksimil/" + record.ExternalId + ".pdf"),
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
        LitteraturbankenRightsCatalog rights)
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

    private static ProviderSourceInfo ToSourceInfo(
        CatalogRecord record,
        LitteraturbankenRightsCatalog rights)
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

    private static string RightsSignature(CatalogRecord record) =>
        string.Join(
            "|",
            record.Provenance
                .Select(provenance => (provenance.Library ?? string.Empty) + ":" + (provenance.Signum ?? string.Empty))
                .OrderBy(value => value, StringComparer.Ordinal));
}

/// <summary>One work, its item, and the per-format sources the plan needs.</summary>
internal sealed record WorkBundle(
    ProviderItem Item,
    IReadOnlyList<SourcedAsset> Assets,
    LitteraturbankenRightsCatalog Rights);

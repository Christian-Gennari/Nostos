using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Turns a parsed <see cref="GutenbergBook"/> into the provider contracts, and
/// resolves an acquisition request — an asset id, or the source's preferred
/// representation — into the exact file Nostos will download.
///
/// This is the only place Gutenberg's book model becomes a
/// <see cref="ProviderItem"/> or a <see cref="ProviderAcquisitionPlan"/>, so the
/// live feed, the detail feed and the bulk snapshot all map identically.
/// </summary>
internal sealed class GutenbergItemMapper
{
    private readonly string _providerId;

    public GutenbergItemMapper(string providerId)
    {
        _providerId = providerId;
    }

    public ProviderItem ToProviderItem(GutenbergBook book, bool includeAssets) => new(
        ProviderId: _providerId,
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

    /// <summary>
    /// The plan for <paramref name="assetId"/>, or for the book's preferred
    /// representation (then its first) when none is named. A named asset the
    /// book does not offer is a hard <c>provider_asset_unavailable</c>, never a
    /// silent fallback to a different file.
    /// </summary>
    public ProviderAcquisitionPlan PlanAcquisition(GutenbergBook book, string? assetId)
    {
        var asset = assetId is null
            ? book.Assets.FirstOrDefault(a => a.IsPreferred) ?? book.Assets.FirstOrDefault()
            : book.Assets.FirstOrDefault(a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
            throw ProviderException.AssetUnavailableFor(_providerId, book.Id, assetId ?? "ebook");

        return new ProviderAcquisitionPlan(
            ProviderId: _providerId,
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
}

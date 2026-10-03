using Nostos.Shared.Dtos;

namespace Nostos.Backend.Providers.Contracts;

/// <summary>
/// Nostos-owned normalization of whatever an external catalog returns. Nothing
/// provider-specific crosses this boundary: a Gutenberg Atom entry, a LibriVox
/// JSON record and a future OPDS entry all become these types, and the library,
/// reader, storage and UI layers only ever see these.
/// </summary>
public sealed record ProviderMetadata(
    string Title,
    string? Subtitle = null,
    string? Author = null,
    string? Description = null,
    string? Language = null,
    string? Publisher = null,
    string? PublishedDate = null,
    string? Categories = null,
    /// <summary>Audiobooks only: narrators/readers of the recording.</summary>
    string? Narrator = null,
    /// <summary>Audiobooks only: total running time, as a display string (e.g. "13:06:44").</summary>
    string? Duration = null,
    int? PageCount = null);

/// <summary>
/// A user's corrections to a source's metadata, applied at import time.
///
/// Mirrors <see cref="ProviderMetadata"/> with every field optional, so a caller
/// can send only what the user changed: `null` keeps the source's value, a value
/// replaces it, and an empty string clears it.
/// </summary>
public sealed record ProviderMetadataOverrides(
    string? Title = null,
    string? Subtitle = null,
    string? Author = null,
    string? Description = null,
    string? Language = null,
    string? Publisher = null,
    string? PublishedDate = null,
    string? Categories = null,
    string? Narrator = null,
    string? Duration = null,
    int? PageCount = null);

/// <summary>
/// One downloadable representation of an item. Carries no URL: the client only
/// ever names this by <see cref="Id"/>, and the provider resolves the actual
/// location in <see cref="IProviderAcquisitionPlanner"/>.
/// </summary>
public sealed record ProviderAsset(
    /// <summary>Stable asset key within the provider (e.g. "epub3-images"). Persisted in provenance.</summary>
    string Id,
    ProviderMediaKind Kind,
    /// <summary>Short label shown to the user (e.g. "EPUB3 (E-readers incl. Send-to-Kindle)").</summary>
    string Label,
    /// <summary>
    /// Normalized content format when known (for example application/epub+zip
    /// or application/pdf); provider-specific source formats remain allowed
    /// for representations such as assembled audiobook inputs.
    /// </summary>
    string SourceFormat,
    long? SizeBytes = null,
    /// <summary>The provider's default choice when the caller names no asset.</summary>
    bool IsPreferred = false);

public sealed record ProviderCover(Uri Url, string ContentType, string FileExtension);

/// <summary>
/// Where an item came from and what the source says about its rights.
/// <see cref="RightsStatement"/> quotes the source; Nostos never upgrades that
/// into a universal claim.
/// </summary>
public sealed record ProviderSourceInfo(
    string? ItemUrl = null,
    string? RightsStatement = null,
    string? RightsUrl = null);

/// <summary>
/// A normalized remote item. The same shape is used for search results and for
/// full item detail; search results simply carry no assets.
/// </summary>
public sealed record ProviderItem(
    string ProviderId,
    string ExternalId,
    /// <summary>
    /// Normalized content kind carried even by thin search results. Assets are
    /// intentionally absent from search results, so callers must not infer the
    /// item's kind from a provider id or from detail-only assets.
    /// </summary>
    ProviderMediaKind MediaKind,
    ProviderMetadata Metadata,
    IReadOnlyList<ProviderAsset> Assets,
    ProviderCover? Cover = null,
    ProviderSourceInfo? Source = null,
    /// <summary>Informational: number of source parts/tracks (e.g. LibriVox sections).</summary>
    int? PartCount = null)
{
    /// <summary>The provider's default asset, or the first one offered.</summary>
    public ProviderAsset? PreferredAsset =>
        Assets.FirstOrDefault(a => a.IsPreferred) ?? Assets.FirstOrDefault();

    public ProviderAsset? FindAsset(string assetId) =>
        Assets.FirstOrDefault(a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase));
}

public sealed record ProviderSearchQuery(
    string Query,
    int Limit = 20,
    int Offset = 0,
    ProviderMediaKind? Kind = null);

/// <summary>
/// <paramref name="Notice"/> carries a human-readable note from the provider
/// about a query its catalog cannot express natively (e.g. LibriVox only
/// matches a whole title or an author surname), so the UI can explain a thin
/// result set instead of looking broken.
/// </summary>
public sealed record ProviderSearchPage(
    IReadOnlyList<ProviderItem> Items,
    bool HasMore = false,
    string? Notice = null);

/// <summary>
/// One file to fetch. <see cref="FileExtension"/> is what the part is written to
/// disk as and must be a plain extension (".epub", ".mp3"): the provider never
/// supplies a filename, so a hostile or simply wrong source string cannot
/// influence a path.
///
/// <paramref name="Label"/> is the source's own name for this part ("Chapters
/// 1-3") where it has one. It is display text only — an assembler may use it to
/// label a chapter, and it is never used to build a path.
/// </summary>
public sealed record ProviderDownloadPart(
    Uri Url,
    string FileExtension,
    long? ExpectedBytes = null,
    string? Label = null);

/// <summary>The format of the finished local file.</summary>
public sealed record ProviderOutput(string FileExtension, string ContentType, string Label);

/// <summary>
/// Everything needed to turn one chosen remote item into one local book.
/// <see cref="Chapters"/> (when present) are canonical Nostos chapters, so the
/// audio reader gets usable navigation without knowing the source was
/// multi-track.
/// </summary>
public sealed record ProviderAcquisitionPlan(
    string ProviderId,
    string ExternalId,
    ProviderAsset Asset,
    ProviderMetadata Metadata,
    IReadOnlyList<ProviderDownloadPart> Parts,
    ProviderOutput Output,
    ProviderCover? Cover = null,
    ProviderSourceInfo? Source = null,
    IReadOnlyList<BookChapterDto>? Chapters = null);

/// <summary>
/// A provider-side failure. Carries a stable code so the acquisition layer —
/// and through it the UI — can distinguish "the source is down" from "the item
/// is gone" from "that asset is not offered", without matching on messages.
/// </summary>
public sealed class ProviderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public const string Unavailable = "provider_unavailable";
    public const string ResponseInvalid = "provider_response_invalid";
    public const string ItemNotFound = "provider_item_not_found";
    public const string AssetUnavailable = "provider_asset_unavailable";

    public static ProviderException UnavailableFor(string providerId, string detail) =>
        new(Unavailable, $"The {providerId} source is unavailable: {detail}");

    public static ProviderException InvalidResponse(string providerId, string detail) =>
        new(ResponseInvalid, $"The {providerId} source returned an unexpected response: {detail}");

    public static ProviderException AssetUnavailableFor(string providerId, string externalId, string assetId) =>
        new(AssetUnavailable, $"{providerId} item {externalId} does not offer asset '{assetId}'.");
}

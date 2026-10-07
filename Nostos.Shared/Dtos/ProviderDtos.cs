using System;
using System.Collections.Generic;

namespace Nostos.Shared.Dtos;

// --- EXTERNAL CONTENT PROVIDERS (issue #166) ---
// The wire shape of the provider/acquisition boundary. Deliberately its own
// small vocabulary rather than a reuse of the library DTOs: these describe
// something that is NOT yet a Nostos book, and keeping them separate is what
// stops provider concepts from spreading into the library contract.
//
// No provider-specific field appears here (no GutenbergId, no LibriVoxId) — a
// provider normalizes its catalogue into exactly these shapes.

/// <summary>One selectable source in the "add from a source" surface.</summary>
public sealed record ProviderSummaryDto(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Capabilities,
    /// <summary>The source's own rights wording, shown as-is. Never a Nostos claim.</summary>
    string? RightsNotice);

public sealed record ProviderAssetDto(
    string Id,
    /// <summary>"ebook" or "audiobook".</summary>
    string Kind,
    string Label,
    string? SourceFormat,
    long? SizeBytes,
    bool IsPreferred);

public sealed record ProviderItemDto(
    string ProviderId,
    string ExternalId,
    /// <summary>"ebook" or "audiobook", available on thin search results.</summary>
    string MediaKind,
    string Title,
    string? Subtitle,
    string? Author,
    string? Description,
    string? Language,
    string? Publisher,
    string? PublishedDate,
    string? Categories,
    string? Narrator,
    string? Duration,
    int? PageCount,
    IReadOnlyList<ProviderAssetDto> Assets,
    /// <summary>Nostos-relative URL that proxies the artwork, or null when the source has none.</summary>
    string? CoverUrl,
    string? SourceUrl,
    string? RightsStatement,
    /// <summary>Source parts/tracks, e.g. the number of LibriVox sections.</summary>
    int? PartCount);

public sealed record ProviderSearchResultDto(
    IReadOnlyList<ProviderItemDto> Items,
    bool HasMore,
    /// <summary>Provider-supplied note about a thin result set (e.g. a catalogue that only matches whole titles).</summary>
    string? Notice);

/// <summary>Per-provider outcome of one aggregate discovery request.</summary>
public sealed record ProviderDiscoverySourceStatusDto(
    string ProviderId,
    string DisplayName,
    bool Succeeded,
    string? Notice,
    string? ErrorCode);

/// <summary>
/// Provider-neutral discovery response. Successful results remain useful when
/// one source is temporarily unavailable.
/// </summary>
public sealed record ProviderDiscoverySearchResultDto(
    IReadOnlyList<ProviderItemDto> Items,
    bool HasMore,
    IReadOnlyList<ProviderDiscoverySourceStatusDto> Sources);

/// <summary>
/// The user's own edits to the metadata an import would otherwise take from the
/// source verbatim, for an import started from a prefilled form.
///
/// Every field means the same thing: `null` (or absent) keeps what the source
/// says, and a value replaces it. An empty string clears the field — the user
/// emptied that box, and putting the source's text back would be worse than
/// dropping it. `Title` is the exception: it falls back to the source, because a
/// book with no title is not a book.
///
/// Send only what the user actually changed. A field left null still follows the
/// source, so a provider-side correction is never overwritten by a stale value
/// the client echoed back.
/// </summary>
public sealed record ProviderMetadataOverridesDto(
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
/// Start an import. Note there is no URL field: a client names a provider, an
/// item and optionally an asset, and the server resolves the rest.
/// </summary>
public sealed record ProviderAcquireRequestDto(
    string ExternalId,
    string? AssetId = null,
    IReadOnlyList<Guid>? CollectionIds = null,
    bool IncludeCover = true,
    /// <summary>
    /// Optional. Present only when the client showed the user the metadata first
    /// and they were allowed to change it.
    /// </summary>
    ProviderMetadataOverridesDto? MetadataOverrides = null);

public sealed record ProviderAcquisitionDto(
    string JobId,
    /// <summary>queued | running | succeeded | failed | cancelled</summary>
    string State,
    string Stage,
    int Percent,
    string? Detail,
    string ProviderId,
    string ExternalId,
    string? AssetId,
    Guid? BookId,
    string? ErrorCode,
    string? Message,
    DateTime CreatedAt,
    DateTime UpdatedAt);

// --- PROVIDER ENABLEMENT (issue #774) ---
// The Settings surface for choosing which free sources participate. Kept
// separate from ProviderSummaryDto, which remains the enabled-only Add Book
// consumer view; this is the management view and deliberately lists disabled
// sources too.

/// <summary>One source in the Settings "Book providers" list, enabled or not.</summary>
public sealed record ProviderSettingsItemDto(
    string Id,
    string DisplayName,
    /// <summary>Short coverage copy, or null when the source offers none.</summary>
    string? Description,
    IReadOnlyList<string> Capabilities,
    string? RightsNotice,
    bool Enabled,
    /// <summary>The source's own declaration, for wording "off by default".</summary>
    bool EnabledByDefault);

public sealed record ProviderSettingsResponseDto(IReadOnlyList<ProviderSettingsItemDto> Providers);

/// <summary>
/// One enable/disable choice. Null (or absent) is a client error and is never
/// treated as an implicit disable, so an empty body cannot silently turn a
/// source off.
/// </summary>
public sealed record ProviderPreferenceUpdateDto(bool? Enabled = null);

using Nostos.Backend.Providers.Contracts;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// Ask for one external item to be imported.
///
/// Note what the caller cannot supply: a URL. Only a provider id, that
/// provider's own item id, and optionally which asset of it to take. The
/// provider resolves the actual locations server-side, which is what keeps this
/// endpoint from becoming a general-purpose URL fetcher.
/// </summary>
public sealed record AcquisitionRequest(
    string ProviderId,
    string ExternalId,
    string? AssetId = null,
    IReadOnlyList<Guid>? CollectionIds = null,
    bool IncludeCover = true,
    /// <summary>
    /// Optional user edits, when the import was started from a prefilled form.
    /// They replace the created book's fields; they never take part in identity
    /// matching, which stays on what the source says.
    /// </summary>
    ProviderMetadataOverrides? MetadataOverrides = null);

public enum AcquisitionOutcome
{
    /// <summary>The file is in storage and its provenance is on the book.</summary>
    Acquired,

    /// <summary>This exact provider item+asset was already imported; nothing was downloaded.</summary>
    AlreadyAcquired,

    /// <summary>The library already holds a local file for this work; nothing was overwritten.</summary>
    AlreadyInLibrary,

    Failed,
}

public sealed record AcquisitionResult(
    AcquisitionOutcome Outcome,
    Guid? BookId = null,
    BookDto? Book = null,
    string Reply = "",
    string? ErrorCode = null)
{
    public bool Succeeded => Outcome is AcquisitionOutcome.Acquired;

    public static AcquisitionResult Acquired(Guid bookId, BookDto? book, string reply) =>
        new(AcquisitionOutcome.Acquired, bookId, book, reply);

    public static AcquisitionResult AlreadyAcquired(Guid bookId, BookDto? book, string reply) =>
        new(AcquisitionOutcome.AlreadyAcquired, bookId, book, reply);

    public static AcquisitionResult AlreadyInLibrary(Guid bookId, BookDto? book, string reply) =>
        new(AcquisitionOutcome.AlreadyInLibrary, bookId, book, reply);

    public static AcquisitionResult Failed(string code, string reply) =>
        new(AcquisitionOutcome.Failed, Reply: reply, ErrorCode: code);
}

/// <summary>
/// Coarse progress for the UI. Stages are deliberately few and stable so the
/// client can label them without knowing which provider is running.
/// </summary>
public sealed record AcquisitionProgress(
    string Stage,
    int Percent,
    string? Detail = null,
    Guid? BookId = null);

/// <summary>
/// An expected, reportable failure of the acquisition pipeline itself (as
/// opposed to a provider or download failure, which carry their own codes).
/// </summary>
public sealed class AcquisitionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public const string ProviderUnknown = "provider_unknown";
    public const string ProviderCannotAcquire = "provider_cannot_acquire";
    public const string InvalidRequest = "invalid_acquisition_request";
    public const string ItemNotFound = "provider_item_not_found";
    public const string NoAssets = "provider_no_downloadable_assets";
    public const string TooManyParts = "download_too_many_parts";
    public const string PartMissing = "download_part_missing";
    public const string AssemblyFailed = "assembly_failed";
    public const string InsufficientSpace = "insufficient_free_space";
    public const string Conflict = "acquisition_conflict";
}

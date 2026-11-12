using System.Text.Json;
using Nostos.Backend.Services.BookUpload;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Where a zipped Calibre library may be staged, and what it may contain
/// (issue #1572). The default cap is deliberately generous — a library with
/// audiobooks is much larger than one with text EPUBs — while still bounded:
/// an uploaded library is copied twice on disk (the zip and its extraction)
/// before the import copies chosen files into the library.
/// </summary>
public sealed class CalibreUploadOptions
{
    public const string SectionName = "CalibreUpload";

    public const long DefaultMaxLibraryBytes = 8L * 1024L * 1024L * 1024L;
    public const long DefaultMaxExtractedBytes = 16L * 1024L * 1024L * 1024L;
    public const int DefaultMaxFiles = 100_000;

    /// <summary>Absolute path of the scratch directory, supplied by the host.</summary>
    public string? RootPath { get; set; }

    /// <summary>Largest accepted zip. Overridable per plan by a host.</summary>
    public long MaxLibraryBytes { get; set; } = DefaultMaxLibraryBytes;

    /// <summary>Largest total extracted size. A zip of text compresses well, so this is larger than the zip.</summary>
    public long MaxExtractedBytes { get; set; } = DefaultMaxExtractedBytes;

    /// <summary>Most files an extracted library may contain.</summary>
    public int MaxFiles { get; set; } = DefaultMaxFiles;

    /// <summary>How long an unfinished upload stays resumable, and a staged library stays readable.</summary>
    public int SessionLifetimeHours { get; set; } = 24;

    public static void Validate(CalibreUploadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxLibraryBytes <= 0)
            throw new InvalidOperationException(
                $"'{SectionName}:MaxLibraryBytes' must be positive.");
        if (options.MaxExtractedBytes <= 0)
            throw new InvalidOperationException(
                $"'{SectionName}:MaxExtractedBytes' must be positive.");
        if (options.MaxFiles <= 0)
            throw new InvalidOperationException(
                $"'{SectionName}:MaxFiles' must be positive.");
        if (options.SessionLifetimeHours is < 1 or > 24 * 14)
            throw new InvalidOperationException(
                $"'{SectionName}:SessionLifetimeHours' must be between 1 and 336.");
    }

    public CalibreUploadLimits ToLimits() => new(
        MaxLibraryBytes,
        MaxExtractedBytes,
        MaxFiles,
        TimeSpan.FromHours(SessionLifetimeHours));
}

/// <summary>The effective limits for one owner. A Cloud host can vary these by plan.</summary>
public sealed record CalibreUploadLimits(
    long MaxLibraryBytes,
    long MaxExtractedBytes,
    int MaxFiles,
    TimeSpan SessionLifetime)
{
    public int PartBytes => BookUploadLimits.PartBytes;

    public int PartCountFor(long totalBytes) => BookUploadLimits.PartCountFor(totalBytes);

    public int PartLengthFor(long totalBytes, int partIndex) =>
        BookUploadLimits.PartLength(totalBytes, partIndex);
}

/// <summary>
/// Supplies the effective limits for an owner. SelfHosted binds one set from
/// configuration; a hosted deployment can return a plan's cap without changing
/// the upload, extraction or cleanup code.
/// </summary>
public interface ICalibreUploadLimitsProvider
{
    CalibreUploadLimits For(string ownerKey);
}

/// <summary>Establishes whose scratch area and staged library a request may see.</summary>
public interface ICalibreUploadOwnerProvider
{
    string GetOwnerKey();
}

/// <summary>
/// What a host can advertise about the uploaded-library path. A host registers
/// this only with the store and service behind it, so the capability can never
/// promise an upload the server cannot receive.
/// </summary>
public interface ICalibreUploadAvailability
{
    bool IsSupported { get; }
}

public static class CalibreUploadStates
{
    public const string Receiving = "receiving";
    public const string Staged = "staged";
    public const string Failed = "failed";
}

/// <summary>Stable transport and validation codes. The words are added by the host.</summary>
public sealed class CalibreUploadException : Exception
{
    public const string NotFound = "calibre_upload_not_found";
    public const string InvalidRequest = "calibre_upload_invalid_request";
    public const string TooLarge = "calibre_upload_too_large";
    public const string PartConflict = "calibre_upload_part_conflict";
    public const string PartHashMismatch = "calibre_upload_part_hash_mismatch";
    public const string PartRangeInvalid = "calibre_upload_part_range_invalid";
    public const string FileHashMismatch = "calibre_upload_file_hash_mismatch";
    public const string Incomplete = "calibre_upload_incomplete";
    public const string Expired = "calibre_upload_expired";
    public const string StorageExhausted = "calibre_upload_storage_exhausted";
    public const string InvalidArchive = "calibre_upload_invalid_archive";
    public const string UnsafeEntry = "calibre_upload_unsafe_entry";
    public const string TooManyFiles = "calibre_upload_too_many_files";
    public const string ExtractedTooLarge = "calibre_upload_extracted_too_large";
    public const string MetadataMissing = "calibre_upload_metadata_missing";
    public const string NotCalibre = "calibre_upload_not_calibre";
    public const string Failed = "calibre_upload_failed";

    public CalibreUploadException(string code, string? message = null, IReadOnlyList<int>? missingParts = null)
        : base(message ?? code)
    {
        Code = code;
        MissingParts = missingParts;
    }

    public string Code { get; }

    /// <summary>For <see cref="Incomplete"/>: the part indexes the server still needs.</summary>
    public IReadOnlyList<int>? MissingParts { get; }

    public static CalibreUploadException Error(string code, string? message = null) => new(code, message);
}

public sealed record CalibreUploadCreateRequest(long TotalBytes, string FileName, string Sha256);

public sealed record CalibreUploadPartReceipt(Guid SessionId, int PartIndex, bool AlreadyPresent);

/// <summary>
/// What the review screen and the import source need to know about one
/// uploaded library. <see cref="RootPath"/> never leaves the server.
/// </summary>
public sealed record CalibreStagedLibrary(
    Guid SessionId,
    string RootPath,
    long StagedBytes,
    int Books);

public sealed record CalibreUploadStatus(
    Guid SessionId,
    string State,
    string FileName,
    long TotalBytes,
    int PartBytes,
    int TotalParts,
    IReadOnlyList<int> ReceivedParts,
    long ReceivedBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long MaxLibraryBytes,
    long MaxExtractedBytes,
    int MaxFiles,
    int? Books = null,
    long? StagedBytes = null,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>
/// The persisted scratch session. It is a file beside the bytes, not a
/// database row: the staged library is disposable and never part of the
/// customer's library, so a backup restore or a migration activation must not
/// need to preserve it.
/// </summary>
public sealed class CalibreUploadManifest
{
    public Guid Id { get; set; }
    public string OwnerKey { get; set; } = string.Empty;
    public string State { get; set; } = CalibreUploadStates.Receiving;
    public string FileName { get; set; } = "library.zip";
    public long TotalBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public List<int> ReceivedParts { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? StagedRoot { get; set; }
    public long? StagedBytes { get; set; }
    public int? Books { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
}

/// <summary>
/// Owns the staging bytes and the manifest for one uploaded Calibre library.
/// SelfHosted implements it on the transfer volume; a hosted deployment can
/// put the same contract over its own scratch volume without changing the
/// service, the extraction checks, the endpoints or the review flow.
/// </summary>
public interface ICalibreUploadStore
{
    Task<CalibreUploadManifest?> FindActiveAsync(string ownerKey, CancellationToken ct);

    Task<CalibreUploadManifest?> FindAsync(Guid sessionId, string ownerKey, CancellationToken ct);

    Task<CalibreUploadManifest?> FindLatestStagedAsync(string ownerKey, CancellationToken ct);

    Task<CalibreUploadManifest> CreateAsync(
        string ownerKey,
        CalibreUploadCreateRequest request,
        CalibreUploadLimits limits,
        DateTime nowUtc,
        CancellationToken ct);

    Task<CalibreUploadPartReceipt> ReceivePartAsync(
        CalibreUploadManifest session,
        int partIndex,
        long start,
        long end,
        long total,
        string expectedSha256,
        Stream content,
        CalibreUploadLimits limits,
        DateTime nowUtc,
        CancellationToken ct);

    Task<CalibreStagedLibrary?> ReadStagedAsync(CalibreUploadManifest session, CancellationToken ct);

    Task<CalibreStagedLibrary> StageAsync(
        CalibreUploadManifest session,
        CalibreUploadLimits limits,
        DateTime nowUtc,
        CancellationToken ct);

    Task DiscardAsync(CalibreUploadManifest session, CancellationToken ct);

    Task<int> ExpireAsync(DateTime cutoffUtc, CancellationToken ct);
}

internal static class CalibreUploadJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

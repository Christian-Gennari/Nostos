using System.Text.Json.Serialization;

namespace Nostos.Backend.Services.BookUpload;

/// <summary>
/// Lifecycle of one resumable book-file upload (#1266). <see cref="Paused"/>
/// is resumable: the client may continue the same session later.
/// <see cref="Completing"/> exists so a crash between publishing the bytes and
/// recording the row update is retryable rather than ambiguous.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookUploadState
{
    Created = 0,
    Receiving = 1,
    Paused = 2,
    Completing = 3,
    Complete = 4,
    Failed = 5,
    Discarded = 6,
    Expired = 7,
}

/// <summary>Server-authoritative limits for the book upload contract.</summary>
public static class BookUploadLimits
{
    /// <summary>Largest single book file this contract accepts.</summary>
    public const long MaxBookBytes = 4L * 1024L * 1024L * 1024L;

    /// <summary>
    /// Fixed part size. The server owns it so a resumed upload never depends on
    /// a client's remembered chunk size.
    /// </summary>
    public const int PartBytes = Services.Portability.MigrationContractLimits.DefaultChunkBytes;

    /// <summary>How long an idle session stays resumable after its last accepted part.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

    public static int PartCountFor(long totalBytes) =>
        totalBytes <= 0 ? 0 : (int)((totalBytes + PartBytes - 1) / PartBytes);

    public static int PartLength(long totalBytes, int partIndex)
    {
        if (partIndex < 0 || partIndex >= PartCountFor(totalBytes)) return 0;
        var offset = (long)partIndex * PartBytes;
        return (int)Math.Min(PartBytes, totalBytes - offset);
    }
}

/// <summary>Stable transport error codes for book uploads.</summary>
public sealed class BookUploadException : Exception
{
    public const string NotFound = "book_upload_not_found";
    public const string InvalidRequest = "book_upload_invalid_request";
    public const string UnsupportedType = "book_upload_unsupported_type";
    public const string InvalidState = "book_upload_invalid_state";
    public const string InProgress = "book_upload_in_progress";
    public const string PartConflict = "book_upload_part_conflict";
    public const string PartHashMismatch = "book_upload_part_hash_mismatch";
    public const string PartRangeInvalid = "book_upload_part_range_invalid";
    public const string FileHashMismatch = "book_upload_file_hash_mismatch";
    public const string Expired = "book_upload_expired";
    public const string StorageExhausted = "book_upload_storage_exhausted";
    public const string BookNotFound = "book_upload_book_not_found";

    public BookUploadException(string code, string? message = null)
        : base(message ?? code) => Code = code;

    public string Code { get; }

    public static BookUploadException Error(string code) => new(code);
}

/// <summary>Establishes whose uploads this request may see or change.</summary>
public interface IBookUploadOwnerProvider
{
    string GetOwnerKey();
}

/// <summary>Public, host-local description of one upload session.</summary>
public sealed record BookUploadStatus(
    Guid SessionId,
    Guid BookId,
    BookUploadState State,
    long TotalBytes,
    int ChunkSize,
    int TotalChunks,
    string FileName,
    string FileSha256,
    IReadOnlyList<int> ReceivedParts,
    long ReceivedBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? CompletedAtUtc = null,
    string? FailureCode = null);

/// <summary>Creation payload: the file identity the client must keep stable while resuming.</summary>
public sealed record BookUploadCreateRequest(
    long TotalBytes,
    string FileName,
    string? ContentType,
    string Sha256,
    string? ClientFingerprint = null);

public sealed record BookUploadPartResult(
    Guid SessionId,
    int PartIndex,
    bool AlreadyPresent);

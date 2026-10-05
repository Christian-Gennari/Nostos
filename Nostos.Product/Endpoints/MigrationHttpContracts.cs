using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Endpoints;

/// <summary>Inclusive chunk-index range used to compress received chunk receipts.</summary>
public sealed record MigrationChunkRange(int StartIndex, int EndIndex);

/// <summary>
/// Transport DTOs for the migration HTTP API (issue #679, plan section 4.2).
/// Kept separate from EF records and from the provider-neutral contract records
/// they wrap; enum values serialize as the contract member names because the
/// shared frontend models them as string unions.
/// </summary>
public sealed record MigrationPreflightResponse(
    MigrationPreflightResult Evaluation,
    Guid? ReservationId,
    DateTimeOffset? ReservationExpiresAtUtc,
    int ChunkSizeBytes);

public sealed record MigrationCreateJobRequest(
    MigrationDirection Direction,
    string? IdempotencyKey,
    Guid? ReservationId = null);

public sealed record MigrationJobStatusResponse(
    MigrationJob Job,
    MigrationProgress Progress,
    MigrationSessionStatus? Session,
    bool DownloadAvailable,
    DateTimeOffset? ArtifactExpiresAtUtc,
    PreparedPortableImportMetadata? PreparedImport);

public sealed record MigrationUploadSessionResponse(
    MigrationSessionStatus Session,
    IReadOnlyList<MigrationChunkRange> ReceivedRanges);

public sealed record MigrationErrorResponse(string Error, string Message);

/// <summary>
/// Explicit request-body shapes for the migration routes. Every required
/// member is nullable so a missing JSON property is distinguishable from a
/// default value and is rejected with the migration 400 error model instead of
/// silently defaulting.
/// </summary>
public sealed record MigrationPreflightBody(
    MigrationArchiveCounts? IncomingCounts = null,
    long? DeclaredArchiveBytes = null,
    long? DeclaredMediaBytes = null,
    long? MaxSingleEntryBytes = null,
    int? DeclaredFormatVersion = null,
    int? DeclaredDataVersion = null,
    string? DeclaredFormatName = null,
    string? ClientDestinationRevision = null,
    bool? IsOperationalBackup = null);

public sealed record MigrationCreateJobBody(
    MigrationDirection? Direction = null,
    string? IdempotencyKey = null,
    Guid? ReservationId = null);

public sealed record MigrationFileIdentityBody(
    long? TotalSizeBytes = null,
    string? Sha256Checksum = null,
    string? ClientFingerprint = null);

public sealed record MigrationSessionBody(
    MigrationSessionPurpose? Purpose = null,
    long? TotalBytes = null,
    int? ChunkSize = null,
    int? TotalChunks = null,
    MigrationFileIdentityBody? FileIdentity = null,
    string? IdempotencyKey = null);

public sealed record MigrationCancelBody(string? Reason = null);

public sealed record MigrationRetryBody(string? IdempotencyKey = null);

/// <summary>
/// Explicit replacement-activation body (#681, Slice 8). The destination
/// revision is the opaque token the user reviewed at preflight; replacement
/// confirmation is required only when the destination is populated.
/// </summary>
public sealed record MigrationActivateBody(
    string? DestinationRevision = null,
    bool ConfirmReplacement = false);

/// <summary>
/// Replacement-conflict body: the stable migration error members followed by
/// the destination facts the browser must show before asking again.
/// </summary>
public sealed record MigrationActivationConflictBody(
    string Error,
    string Message,
    string? DestinationRevision = null,
    MigrationDestinationStatus? DestinationStatus = null,
    MigrationExistingCounts? ExistingCounts = null);

/// <summary>
/// Explicit, uniform JSON parsing for the migration routes. Model binding
/// failures (malformed JSON, wrong value type, invalid enum string, numeric
/// overflow, missing body) never escape as generic Problem Details; callers
/// answer 400 with the migration error body. A non-JSON content type is
/// rejected the same way.
/// </summary>
public static class MigrationHttpBodies
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task<(bool Ok, T? Value)> TryReadAsync<T>(
        HttpRequest request,
        CancellationToken ct) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentLength is null or 0) return (true, null);
        if (!IsJsonContentType(request.ContentType)) return (false, null);

        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(request.Body, Options, ct);
            return (value is not null, value);
        }
        catch (JsonException)
        {
            return (false, null);
        }
        catch (BadHttpRequestException)
        {
            return (false, null);
        }
    }

    private static bool IsJsonContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType)
        || contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Compresses sorted chunk indexes into inclusive ranges for HTTP efficiency.
/// The shared session contract still carries the exact index list.
/// </summary>
public static class MigrationChunkRanges
{
    public static IReadOnlyList<MigrationChunkRange> Compress(IReadOnlyList<int> receivedChunks)
    {
        ArgumentNullException.ThrowIfNull(receivedChunks);
        if (receivedChunks.Count == 0) return [];

        var sorted = receivedChunks.Distinct().Order().ToList();
        var ranges = new List<MigrationChunkRange>();
        var start = sorted[0];
        var end = start;
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == end + 1)
            {
                end = sorted[i];
                continue;
            }

            ranges.Add(new MigrationChunkRange(start, end));
            start = sorted[i];
            end = start;
        }

        ranges.Add(new MigrationChunkRange(start, end));
        return ranges;
    }
}

/// <summary>
/// Parses the mandatory chunk transport metadata (`Content-Range` and
/// `X-Nostos-Chunk-SHA256`). Malformed metadata is rejected before any request
/// bytes are read or written.
/// </summary>
public static class MigrationChunkHeaders
{
    public const string ChunkHashHeaderName = "X-Nostos-Chunk-SHA256";

    public static bool TryParseContentRange(string? header, out long start, out long end, out long total)
    {
        start = 0;
        end = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(header)) return false;

        var value = header.Trim();
        const string prefix = "bytes ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        value = value[prefix.Length..].Trim();

        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1) return false;
        var rangePart = value[..slash];
        var totalPart = value[(slash + 1)..];
        if (totalPart.StartsWith('*')) return false;
        if (!long.TryParse(totalPart, System.Globalization.NumberStyles.None, null, out total) || total <= 0)
            return false;

        var dash = rangePart.IndexOf('-');
        if (dash <= 0 || dash == rangePart.Length - 1) return false;
        if (!long.TryParse(rangePart[..dash], System.Globalization.NumberStyles.None, null, out start)
            || !long.TryParse(rangePart[(dash + 1)..], System.Globalization.NumberStyles.None, null, out end))
            return false;

        return start >= 0 && end >= start && end < total;
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>
/// Single transport mapping for every typed store/engine/capacity failure.
/// Messages are fixed safe strings: no exception text, paths, storage keys or
/// stack traces can reach a response body.
/// </summary>
public static class MigrationHttpErrors
{
    public const string NotFound = "migration_not_found";
    public const string IdempotencyConflict = "migration_idempotency_conflict";
    public const string InvalidState = "migration_invalid_state";
    public const string LeaseConflict = "migration_lease_conflict";
    public const string ReservationRequired = "migration_reservation_required";
    public const string FileIdentityMismatch = "migration_file_identity_mismatch";
    public const string ChunkConflict = "migration_chunk_conflict";
    public const string ChunkHashMismatch = "migration_chunk_hash_mismatch";
    public const string ChunkRangeInvalid = "migration_chunk_range_invalid";
    public const string SessionExpired = "migration_session_expired";
    public const string StorageExhausted = "migration_storage_exhausted";
    public const string CannotCancel = "migration_cannot_cancel";
    public const string NotRetryable = "migration_not_retryable";
    public const string InvalidRequest = "migration_invalid_request";
    public const string StorageContended = "migration_storage_contended";
    public const string ImportPreparationUnavailable = "migration_import_preparation_unavailable";
    public const string ExportArtifactUnavailable = "migration_export_artifact_unavailable";
    public const string ExportNotAvailable = "migration_export_not_available";
    public const string ExportExpired = "migration_export_expired";
    public const string TooManyJobs = "migration_too_many_jobs";
    public const string Unexpected = "unexpected_error";

    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        [NotFound] = "The migration job was not found.",
        [IdempotencyConflict] = "The idempotency key is already bound to a different request.",
        [InvalidState] = "The migration job is not in a state that allows this operation.",
        [LeaseConflict] = "The migration job is being processed concurrently. Try again.",
        [ReservationRequired] = "A valid preflight reservation is required for an import job.",
        [FileIdentityMismatch] = "The supplied file identity does not match the session.",
        [ChunkConflict] = "A different chunk was already accepted for this index.",
        [ChunkHashMismatch] = "The chunk checksum did not match the received bytes.",
        [ChunkRangeInvalid] = "The chunk range does not match the session contract.",
        [SessionExpired] = "The upload session has expired.",
        [StorageExhausted] = "The host ran out of storage during the transfer.",
        [CannotCancel] = "The job can no longer be cancelled.",
        [NotRetryable] = "Only failed, cancelled or expired jobs can be retried.",
        [InvalidRequest] = "The migration request is malformed.",
        [StorageContended] = "Transfer capacity is busy. Retry shortly.",
        [ImportPreparationUnavailable] = "Import preparation is not available on this deployment yet.",
        [ExportArtifactUnavailable] = "Export preparation is not available on this deployment yet.",
        [ExportNotAvailable] = "This export job has no downloadable artifact.",
        [ExportExpired] = "The export artifact has expired.",
        [MigrationActivationErrorCodes.ConfirmationRequired] =
            "Replacing an existing library requires explicit confirmation.",
        [MigrationActivationErrorCodes.DestinationConflict] =
            "The destination changed. Review replacement again.",
        [MigrationActivationErrorCodes.Busy] =
            "The library is in maintenance. Try again later.",
        [MigrationActivationErrorCodes.Failed] =
            "The activation failed before the library switch; the original library is unchanged.",
        [MigrationActivationErrorCodes.RecoveryFailed] =
            "Activation could not be completed or rolled back in-process. "
            + "The host stays in maintenance until a restart reconciles it.",
        [TooManyJobs] = "The installation has too many outstanding migration jobs. Finish or cancel one and retry.",
        [Unexpected] = "The migration request failed unexpectedly.",
    };

    public static IResult From(Exception exception) => exception switch
    {
        MigrationJobStoreException store => FromStore(store),
        MigrationTransferException transfer => FromTransfer(transfer),
        MigrationActivationException activation => FromActivation(activation),
        TransferReservationException reservation => FromReservation(reservation),
        _ => Result(Unexpected, StatusCodes.Status500InternalServerError),
    };

    /// <summary>
    /// Typed activation failures. Busy is transient (503 + Retry-After, the
    /// frontend coordinator's documented wait-and-repeat path); every other
    /// admission failure is the documented conflict shape. Recovery-failed is a
    /// 409 so the client can report the fail-closed host rather than retrying.
    /// </summary>
    public static IResult FromActivation(MigrationActivationException exception) => exception.Code switch
    {
        MigrationActivationErrorCodes.ConfirmationRequired => Result(
            MigrationActivationErrorCodes.ConfirmationRequired,
            StatusCodes.Status409Conflict),
        MigrationActivationErrorCodes.DestinationConflict => Result(
            MigrationActivationErrorCodes.DestinationConflict,
            StatusCodes.Status409Conflict),
        MigrationActivationErrorCodes.Busy => Retryable(MigrationActivationErrorCodes.Busy, retryAfterSeconds: 5),
        MigrationActivationErrorCodes.Failed => Result(
            MigrationActivationErrorCodes.Failed,
            StatusCodes.Status409Conflict),
        MigrationActivationErrorCodes.RecoveryFailed => Result(
            MigrationActivationErrorCodes.RecoveryFailed,
            StatusCodes.Status409Conflict),
        MigrationActivationErrorCodes.StorageExhausted => Result(
            StorageExhausted,
            StatusCodes.Status507InsufficientStorage),
        _ => Result(Unexpected, StatusCodes.Status500InternalServerError),
    };

    /// <summary>
    /// Admission contention is transient, not a missing reservation: it answers
    /// 503 + Retry-After so the client retries instead of treating it as a
    /// permanent conflict. Every other reservation failure is the documented
    /// 409 reservation-required outcome.
    /// </summary>
    public static IResult FromReservation(TransferReservationException exception) =>
        exception.Kind == TransferReservationConflictKind.Contended
            ? Retryable(StorageContended)
            : Result(ReservationRequired, StatusCodes.Status409Conflict);

    public static IResult FromStore(MigrationJobStoreException exception) => exception.Code switch
    {
        MigrationJobStoreErrorCodes.NotFound => Result(NotFound, StatusCodes.Status404NotFound),
        MigrationJobStoreErrorCodes.InvalidState => Result(InvalidState, StatusCodes.Status409Conflict),
        MigrationJobStoreErrorCodes.LeaseConflict => Result(
            LeaseConflict,
            StatusCodes.Status409Conflict),
        MigrationJobStoreErrorCodes.CannotCancel => Result(CannotCancel, StatusCodes.Status409Conflict),
        MigrationJobStoreErrorCodes.NotRetryable => Result(NotRetryable, StatusCodes.Status409Conflict),
        _ => Result(Unexpected, StatusCodes.Status500InternalServerError),
    };

    public static IResult FromTransfer(MigrationTransferException exception) => exception.Code switch
    {
        MigrationTransferException.InvalidRequest => Result(InvalidRequest, StatusCodes.Status400BadRequest),
        MigrationTransferException.MetadataRequired => Result(InvalidRequest, StatusCodes.Status400BadRequest),
        MigrationTransferException.InvalidState => Result(InvalidState, StatusCodes.Status409Conflict),
        MigrationTransferException.ReservationRequired => Result(
            ReservationRequired,
            StatusCodes.Status409Conflict),
        MigrationTransferException.Expired => Result(SessionExpired, StatusCodes.Status410Gone),
        MigrationTransferException.IdentityMismatch => Result(
            FileIdentityMismatch,
            StatusCodes.Status409Conflict),
        MigrationTransferException.RangeInvalid => Result(
            ChunkRangeInvalid,
            StatusCodes.Status416RangeNotSatisfiable),
        MigrationTransferException.HashMismatch => Result(
            ChunkHashMismatch,
            StatusCodes.Status422UnprocessableEntity),
        MigrationTransferException.ChunkConflict => Result(ChunkConflict, StatusCodes.Status409Conflict),
        // Missing receipts are a state problem for completion, not a distinct
        // transport code in the plan's table; the frontend models it the same
        // way the mock does.
        MigrationTransferException.MissingChunks => Result(InvalidState, StatusCodes.Status409Conflict),
        MigrationTransferException.StorageExhausted => Result(
            StorageExhausted,
            StatusCodes.Status507InsufficientStorage),
        MigrationTransferException.ImportPreparationUnavailable => Result(
            ImportPreparationUnavailable,
            StatusCodes.Status409Conflict),
        MigrationTransferException.ExportArtifactUnavailable => Result(
            ExportArtifactUnavailable,
            StatusCodes.Status409Conflict),
        MigrationTransferException.TooManyJobs => Result(
            TooManyJobs,
            StatusCodes.Status409Conflict),
        _ => Result(Unexpected, StatusCodes.Status500InternalServerError),
    };

    /// <summary>The fixed safe message for a stable migration code, or the code itself.</summary>
    public static string MessageFor(string code) => Messages.GetValueOrDefault(code, code);

    public static IResult Result(string code, int statusCode) =>
        Results.Json(new MigrationErrorResponse(code, MessageFor(code)), statusCode: statusCode);

    /// <summary>Transient outcome with Retry-After, still using the migration error body.</summary>
    public static IResult Retryable(string code, int retryAfterSeconds = 1) =>
        new RetryAfterJsonResult(
            code,
            Messages.GetValueOrDefault(code, code),
            StatusCodes.Status503ServiceUnavailable,
            retryAfterSeconds);

    private sealed class RetryAfterJsonResult(
        string code,
        string message,
        int statusCode,
        int retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return httpContext.Response.WriteAsJsonAsync(
                new MigrationErrorResponse(code, message),
                httpContext.RequestAborted);
        }
    }
}

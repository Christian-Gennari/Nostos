// Nostos.Product/Services/Portability/MigrationContracts.cs

using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Normative limits for the provider-neutral migration contract.
/// </summary>
public static class MigrationContractLimits
{
    public const long MaxArchiveBytes = 512L * 1024L * 1024L * 1024L;
    public const long MaxMediaBytes = 512L * 1024L * 1024L * 1024L;
    public const long MaxSingleEntryBytes = 16L * 1024L * 1024L * 1024L;
    public const long MaxDataBytes = 64L * 1024L * 1024L;
    public const long MaxManifestBytes = 4L * 1024L * 1024L;
    public const int MaxArchiveEntries = 20_000;

    public const int MinChunkBytes = 4 * 1024 * 1024;
    public const int DefaultChunkBytes = 16 * 1024 * 1024;
    public const int MaxChunkBytes = 64 * 1024 * 1024;

    public const int WorkerLeaseDurationMinutes = 5;
    public const int SessionExpiryHours = 24;
    public const int RecoveryRetentionDays = 7;

    /// <summary>
    /// Validates the nominal chunk size declared by a transfer session
    /// (<see cref="MigrationSessionRequest.ChunkSize"/>). It must be within
    /// <see cref="MinChunkBytes"/> and <see cref="MaxChunkBytes"/>.
    /// </summary>
    public static bool IsValidChunkSize(int chunkSize) =>
        chunkSize is >= MinChunkBytes and <= MaxChunkBytes;

    /// <summary>
    /// Validates the actual byte length of one uploaded chunk for a session whose
    /// nominal chunk size is <paramref name="chunkSize"/>.
    /// A non-final chunk must have exactly the session chunk size. The final chunk
    /// must have between 1 and <paramref name="chunkSize"/> bytes, so a short final
    /// chunk - including a whole file smaller than <see cref="MinChunkBytes"/> - is
    /// legal. A zero-length chunk is never legal.
    /// </summary>
    public static bool IsValidChunkBytes(int chunkBytes, int chunkSize, bool isFinalChunk) =>
        IsValidChunkSize(chunkSize) &&
        chunkBytes > 0 &&
        (isFinalChunk ? chunkBytes <= chunkSize : chunkBytes == chunkSize);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationDirection
{
    Import = 0,
    Export = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationJobState
{
    Pending = 0,
    Preparing = 1,
    Transferring = 2,
    Validating = 3,
    ReadyToActivate = 4,
    Activating = 5,
    Completed = 6,
    Failed = 7,
    Cancelled = 8,
    Expired = 9,
}

public static class MigrationJobTransitions
{
    private static readonly FrozenSet<MigrationJobState> Empty =
        FrozenSet<MigrationJobState>.Empty;

    private static readonly FrozenDictionary<MigrationJobState, FrozenSet<MigrationJobState>>
        ImportTransitions = CreateTransitions(
            validating: Set(
                MigrationJobState.ReadyToActivate,
                MigrationJobState.Cancelled,
                MigrationJobState.Expired,
                MigrationJobState.Failed),
            readyToActivate: Set(
                MigrationJobState.Activating,
                MigrationJobState.Cancelled,
                MigrationJobState.Expired,
                MigrationJobState.Failed),
            // Activation is the atomic point-of-no-return boundary.
            activating: Set(
                MigrationJobState.Completed,
                MigrationJobState.Failed));

    private static readonly FrozenDictionary<MigrationJobState, FrozenSet<MigrationJobState>>
        ExportTransitions = CreateTransitions(
            validating: Set(
                MigrationJobState.Completed,
                MigrationJobState.Cancelled,
                MigrationJobState.Expired,
                MigrationJobState.Failed),
            // Exports have no activation step and never enter the import activation path.
            readyToActivate: Empty,
            activating: Empty);

    /// <summary>
    /// Returns the complete, immutable transition table for the given migration
    /// direction. Both the dictionary and its value sets are frozen and cannot be
    /// mutated or down-cast to a mutable collection.
    /// </summary>
    public static FrozenDictionary<MigrationJobState, FrozenSet<MigrationJobState>>
        AllowedTransitions(MigrationDirection direction) =>
        direction switch
        {
            MigrationDirection.Import => ImportTransitions,
            MigrationDirection.Export => ExportTransitions,
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unknown migration direction."),
        };

    public static bool CanTransition(
        MigrationDirection direction,
        MigrationJobState current,
        MigrationJobState target) =>
        AllowedTransitions(direction).TryGetValue(current, out var allowed) &&
        allowed.Contains(target);

    public static void ValidateTransition(
        MigrationDirection direction,
        MigrationJobState current,
        MigrationJobState target)
    {
        if (!CanTransition(direction, current, target))
        {
            throw new InvalidOperationException(
                $"Illegal {direction} migration job transition from {current} to {target}.");
        }
    }

    public static bool IsTerminal(MigrationJobState state) =>
        state is MigrationJobState.Completed
            or MigrationJobState.Failed
            or MigrationJobState.Cancelled
            or MigrationJobState.Expired;

    public static bool IsRetryable(MigrationJobState state) =>
        state is MigrationJobState.Failed
            or MigrationJobState.Cancelled
            or MigrationJobState.Expired;

    private static FrozenDictionary<MigrationJobState, FrozenSet<MigrationJobState>>
        CreateTransitions(
            FrozenSet<MigrationJobState> validating,
            FrozenSet<MigrationJobState> readyToActivate,
            FrozenSet<MigrationJobState> activating) =>
        new Dictionary<MigrationJobState, FrozenSet<MigrationJobState>>
            {
                [MigrationJobState.Pending] = Set(
                    MigrationJobState.Preparing,
                    MigrationJobState.Cancelled,
                    MigrationJobState.Expired,
                    MigrationJobState.Failed),

                [MigrationJobState.Preparing] = Set(
                    MigrationJobState.Transferring,
                    MigrationJobState.Cancelled,
                    MigrationJobState.Expired,
                    MigrationJobState.Failed),

                [MigrationJobState.Transferring] = Set(
                    MigrationJobState.Validating,
                    MigrationJobState.Cancelled,
                    MigrationJobState.Expired,
                    MigrationJobState.Failed),

                [MigrationJobState.Validating] = validating,
                [MigrationJobState.ReadyToActivate] = readyToActivate,
                [MigrationJobState.Activating] = activating,

                [MigrationJobState.Completed] = Empty,
                [MigrationJobState.Failed] = Empty,
                [MigrationJobState.Cancelled] = Empty,
                [MigrationJobState.Expired] = Empty,
            }
            .ToFrozenDictionary();

    private static FrozenSet<MigrationJobState> Set(params MigrationJobState[] states) =>
        states.ToFrozenSet();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationPreflightDecision
{
    AllowedEmpty = 0,
    AllowedReplacementRequired = 1,
    RejectedIncompatible = 2,
    RejectedInsufficientStorage = 3,
    RejectedDestinationConflict = 4,
    RejectedOperationalBackupNotPortable = 5,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationDestinationStatus
{
    Empty = 0,
    Populated = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationSessionPurpose
{
    Import = 0,
    Export = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationSessionState
{
    Created = 0,
    Receiving = 1,
    Complete = 2,
    Expired = 3,
    Cancelled = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationProgressPhase
{
    Pending = 0,
    Preparing = 1,
    Transferring = 2,
    Validating = 3,
    PreparingActivation = 4,
    Activating = 5,
    Completed = 6,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationRecoveryStatus
{
    NotRequired = 0,
    Pending = 1,
    Creating = 2,
    Available = 3,
    Restoring = 4,
    Restored = 5,
    Failed = 6,
    Expired = 7,
}

public sealed record MigrationArchiveCounts(
    long Works = 0,
    long Books = 0,
    long Notes = 0,
    long Topics = 0,
    long NoteTopics = 0,
    long Writings = 0,
    long WritingNotes = 0,
    long Collections = 0,
    long CollectionMemberships = 0,
    long Acquisitions = 0,
    long AssistantSettings = 0,
    long NoteImportBookLinks = 0,
    long MediaEntries = 0)
{
    public static MigrationArchiveCounts Empty { get; } = new();

    public long TotalRows =>
        Works +
        Books +
        Notes +
        Topics +
        NoteTopics +
        Writings +
        WritingNotes +
        Collections +
        CollectionMemberships +
        Acquisitions +
        AssistantSettings +
        NoteImportBookLinks;
}

public sealed record MigrationExistingCounts(
    long Works = 0,
    long Books = 0,
    long Notes = 0,
    long Topics = 0,
    long NoteTopics = 0,
    long Writings = 0,
    long WritingNotes = 0,
    long Collections = 0,
    long BookCollections = 0,
    long Acquisitions = 0,
    long NoteImportBookLinks = 0,
    long AssistantSettings = 0)
{
    public long TotalRows =>
        Works +
        Books +
        Notes +
        Topics +
        NoteTopics +
        Writings +
        WritingNotes +
        Collections +
        BookCollections +
        Acquisitions +
        NoteImportBookLinks +
        AssistantSettings;
}

public sealed record MigrationPreflightRequest(
    MigrationArchiveCounts IncomingCounts,
    long DeclaredArchiveBytes,
    long DeclaredMediaBytes,
    long MaxSingleEntryBytes,
    int DeclaredFormatVersion,
    int DeclaredDataVersion,
    string? DeclaredFormatName = null,
    string? ClientDestinationRevision = null,
    bool IsOperationalBackup = false);

public sealed record MigrationPreflightEvaluationInput(
    MigrationPreflightRequest Request,
    MigrationDestinationStatus DestinationStatus,
    MigrationExistingCounts ExistingCounts,
    long AvailableStorageBytes,
    string DestinationRevision,
    IReadOnlySet<int>? SupportedFormatVersions = null,
    IReadOnlySet<int>? SupportedDataVersions = null);

public sealed record MigrationPreflightResult(
    MigrationPreflightDecision Decision,
    bool IsCompatible,
    MigrationArchiveCounts IncomingCounts,
    MigrationExistingCounts ExistingCounts,
    long DeclaredArchiveBytes,
    long DeclaredMediaBytes,
    long EstimatedRecoveryBytes,
    long RequiredStorageBytes,
    long AvailableStorageBytes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string DestinationRevision)
{
    public bool IsAllowed =>
        Decision is MigrationPreflightDecision.AllowedEmpty
            or MigrationPreflightDecision.AllowedReplacementRequired;
}

public static class MigrationPreflightEvaluator
{
    private const long EstimatedRecoveryBytesPerExistingBook = 50_000_000L;

    public static MigrationPreflightResult Evaluate(MigrationPreflightEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Request);
        ArgumentNullException.ThrowIfNull(input.Request.IncomingCounts);
        ArgumentNullException.ThrowIfNull(input.ExistingCounts);

        var request = input.Request;
        var errors = new List<string>();
        var warnings = new List<string>();

        var supportedFormatVersions =
            input.SupportedFormatVersions ?? new HashSet<int> { 1 };

        var supportedDataVersions =
            input.SupportedDataVersions ?? new HashSet<int> { 1, 2, 3 };

        if (request.IsOperationalBackup
            || (!string.IsNullOrWhiteSpace(request.DeclaredFormatName)
                && (string.Equals(request.DeclaredFormatName, "nostos-operational-backup", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(request.DeclaredFormatName, "operational-backup", StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add(
                "Operational backup archives are not portable migration archives.");

            return Result(
                MigrationPreflightDecision.RejectedOperationalBackupNotPortable,
                isCompatible: false);
        }

        if (!supportedFormatVersions.Contains(request.DeclaredFormatVersion))
        {
            errors.Add(
                $"Archive format version {request.DeclaredFormatVersion} is not supported.");
        }

        if (!supportedDataVersions.Contains(request.DeclaredDataVersion))
        {
            errors.Add(
                $"Archive data version {request.DeclaredDataVersion} is not supported.");
        }

        if (request.DeclaredArchiveBytes < 0)
        {
            errors.Add("Declared archive bytes cannot be negative.");
        }
        else if (request.DeclaredArchiveBytes > MigrationContractLimits.MaxArchiveBytes)
        {
            errors.Add(
                $"Declared archive size exceeds the migration contract limit of " +
                $"{MigrationContractLimits.MaxArchiveBytes} bytes.");
        }

        if (request.DeclaredMediaBytes < 0)
        {
            errors.Add("Declared media bytes cannot be negative.");
        }
        else if (request.DeclaredMediaBytes > MigrationContractLimits.MaxMediaBytes)
        {
            errors.Add(
                $"Declared media size exceeds the migration contract limit of " +
                $"{MigrationContractLimits.MaxMediaBytes} bytes.");
        }

        if (request.MaxSingleEntryBytes < 0)
        {
            errors.Add("Maximum single-entry size cannot be negative.");
        }
        else if (request.MaxSingleEntryBytes > MigrationContractLimits.MaxSingleEntryBytes)
        {
            errors.Add(
                $"Archive contains or declares an entry exceeding the " +
                $"{MigrationContractLimits.MaxSingleEntryBytes}-byte single-entry limit.");
        }

        if (errors.Count > 0)
        {
            return Result(
                MigrationPreflightDecision.RejectedIncompatible,
                isCompatible: false);
        }

        if (request.ClientDestinationRevision is not null &&
            !string.Equals(
                request.ClientDestinationRevision,
                input.DestinationRevision,
                StringComparison.Ordinal))
        {
            errors.Add(
                "Destination contents changed since the client observed the destination revision.");

            return Result(
                MigrationPreflightDecision.RejectedDestinationConflict,
                isCompatible: true);
        }

        var estimatedRecoveryBytes =
            input.DestinationStatus == MigrationDestinationStatus.Populated
                ? checked((input.ExistingCounts.Books * EstimatedRecoveryBytesPerExistingBook) + (input.ExistingCounts.TotalRows * 1024L))
                : 0L;

        long requiredStorageBytes;
        try
        {
            requiredStorageBytes =
                input.DestinationStatus == MigrationDestinationStatus.Empty
                    ? checked(request.DeclaredArchiveBytes + request.DeclaredMediaBytes)
                    : checked(
                        request.DeclaredArchiveBytes +
                        request.DeclaredMediaBytes +
                        estimatedRecoveryBytes);
        }
        catch (OverflowException)
        {
            errors.Add("Required migration storage exceeds the supported numeric range.");

            return new MigrationPreflightResult(
                MigrationPreflightDecision.RejectedInsufficientStorage,
                true,
                request.IncomingCounts,
                input.ExistingCounts,
                request.DeclaredArchiveBytes,
                request.DeclaredMediaBytes,
                estimatedRecoveryBytes,
                long.MaxValue,
                input.AvailableStorageBytes,
                errors.AsReadOnly(),
                warnings.AsReadOnly(),
                input.DestinationRevision);
        }

        if (input.AvailableStorageBytes < requiredStorageBytes)
        {
            errors.Add(
                $"Migration requires {requiredStorageBytes} bytes of available storage, " +
                $"but only {input.AvailableStorageBytes} bytes are available.");

            return new MigrationPreflightResult(
                MigrationPreflightDecision.RejectedInsufficientStorage,
                true,
                request.IncomingCounts,
                input.ExistingCounts,
                request.DeclaredArchiveBytes,
                request.DeclaredMediaBytes,
                estimatedRecoveryBytes,
                requiredStorageBytes,
                input.AvailableStorageBytes,
                errors.AsReadOnly(),
                warnings.AsReadOnly(),
                input.DestinationRevision);
        }

        if (input.DestinationStatus == MigrationDestinationStatus.Populated)
        {
            warnings.Add(
                "Destination contains existing portable data. Activation requires replacement " +
                "and a mandatory recovery snapshot.");

            return new MigrationPreflightResult(
                MigrationPreflightDecision.AllowedReplacementRequired,
                true,
                request.IncomingCounts,
                input.ExistingCounts,
                request.DeclaredArchiveBytes,
                request.DeclaredMediaBytes,
                estimatedRecoveryBytes,
                requiredStorageBytes,
                input.AvailableStorageBytes,
                errors.AsReadOnly(),
                warnings.AsReadOnly(),
                input.DestinationRevision);
        }

        return new MigrationPreflightResult(
            MigrationPreflightDecision.AllowedEmpty,
            true,
            request.IncomingCounts,
            input.ExistingCounts,
            request.DeclaredArchiveBytes,
            request.DeclaredMediaBytes,
            estimatedRecoveryBytes,
            requiredStorageBytes,
            input.AvailableStorageBytes,
            errors.AsReadOnly(),
            warnings.AsReadOnly(),
            input.DestinationRevision);

        MigrationPreflightResult Result(
            MigrationPreflightDecision decision,
            bool isCompatible)
        {
            var estimatedRecoveryBytes =
                input.DestinationStatus == MigrationDestinationStatus.Populated
                    ? checked((input.ExistingCounts.Books * EstimatedRecoveryBytesPerExistingBook) + (input.ExistingCounts.TotalRows * 1024L))
                    : 0L;

            long requiredStorageBytes;
            try
            {
                requiredStorageBytes =
                    input.DestinationStatus == MigrationDestinationStatus.Empty
                        ? checked(request.DeclaredArchiveBytes + request.DeclaredMediaBytes)
                        : checked(
                            request.DeclaredArchiveBytes +
                            request.DeclaredMediaBytes +
                            estimatedRecoveryBytes);
            }
            catch (OverflowException)
            {
                requiredStorageBytes = long.MaxValue;
            }

            return new MigrationPreflightResult(
                decision,
                isCompatible,
                request.IncomingCounts,
                input.ExistingCounts,
                request.DeclaredArchiveBytes,
                request.DeclaredMediaBytes,
                estimatedRecoveryBytes,
                requiredStorageBytes,
                input.AvailableStorageBytes,
                errors.AsReadOnly(),
                warnings.AsReadOnly(),
                input.DestinationRevision);
        }
    }
}

/// <summary>
/// Stable identity for a transferred migration file.
/// TotalSizeBytes must match exactly, and Sha256Checksum is the full file digest.
/// </summary>
public sealed record MigrationFileIdentity(
    long TotalSizeBytes,
    string Sha256Checksum,
    string? ClientFingerprint = null);

/// <summary>
/// Identifies a conflict caused by reusing an idempotency key with a different
/// creation payload.
/// </summary>
public enum MigrationIdempotencyConflictKind
{
    KeyReusedWithDifferentPayload = 0,
}

/// <summary>
/// Typed conflict returned when an idempotency key is already bound to another
/// creation payload.
/// </summary>
public sealed record MigrationIdempotencyConflict(
    MigrationIdempotencyConflictKind Kind);

/// <summary>
/// Result of an idempotent create operation. Successful results carry the
/// original resource; conflict results carry a typed conflict and no resource.
/// </summary>
public sealed record MigrationIdempotencyResult<T> where T : class
{
    private MigrationIdempotencyResult(
        T? resource,
        bool wasReplay,
        MigrationIdempotencyConflict? conflict)
    {
        Resource = resource;
        WasReplay = wasReplay;
        Conflict = conflict;
    }

    public T? Resource { get; }

    public bool WasReplay { get; }

    public MigrationIdempotencyConflict? Conflict { get; }

    public bool IsConflict => Conflict is not null;

    public static MigrationIdempotencyResult<T> Created(T resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new MigrationIdempotencyResult<T>(resource, wasReplay: false, conflict: null);
    }

    public static MigrationIdempotencyResult<T> Replayed(T resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new MigrationIdempotencyResult<T>(resource, wasReplay: true, conflict: null);
    }

    public static MigrationIdempotencyResult<T> Conflicted(MigrationIdempotencyConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return new MigrationIdempotencyResult<T>(resource: null, wasReplay: false, conflict);
    }
}

/// <summary>
/// Required creation payload for a resumable transfer session. The idempotency
/// key is non-empty and scoped to the authenticated owner and parent job.
/// Repeating the key with the same remaining fields returns the original session;
/// reusing it with different fields returns a typed idempotency conflict.
/// </summary>
public sealed record MigrationSessionRequest(
    MigrationSessionPurpose Purpose,
    long TotalBytes,
    int ChunkSize,
    int TotalChunks,
    MigrationFileIdentity FileIdentity,
    string IdempotencyKey);

public sealed record MigrationSessionStatus(
    Guid SessionId,
    MigrationSessionPurpose Purpose,
    MigrationSessionState State,
    long TotalBytes,
    int ChunkSize,
    int TotalChunks,
    MigrationFileIdentity FileIdentity,
    IReadOnlyList<int> ReceivedChunks,
    int ReceivedChunkCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? IdempotencyKey = null);

public sealed record MigrationChunkUploadResult(
    Guid SessionId,
    int ChunkIndex,
    bool AlreadyPresent);

public sealed record MigrationCancelRequest(
    string? Reason = null);

public sealed record MigrationRetryRequest(
    string? IdempotencyKey = null);

public sealed record MigrationProgress(
    MigrationProgressPhase Phase,
    long BytesProcessed,
    long? TotalBytes,
    int? CompletedChunks = null,
    int? TotalChunks = null,
    string? Message = null);

public sealed record MigrationJob(
    Guid Id,
    MigrationDirection Direction,
    MigrationJobState State,
    MigrationRecoveryStatus RecoveryStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LeaseToken = null,
    DateTimeOffset? LeaseExpiresAtUtc = null,
    string? FailureCode = null,
    string? FailureMessage = null);

public sealed record MigrationRecoverySnapshot(
    Guid JobId,
    MigrationRecoveryStatus Status,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record MigrationActivationPreparation(
    Guid JobId,
    string DestinationRevision,
    DateTimeOffset PreparedAtUtc);

/// <summary>
/// Persists migration jobs owned by the authenticated Nostos principal.
/// Implementations MUST scope every read and mutation to the authenticated owner
/// established by the hosting application. Job identifiers are opaque selectors
/// and MUST NOT permit cross-account discovery or access; unauthorized access yields
/// the identical not-found response as a missing identifier. Public models expose
/// no account IDs, tenant database names, or provider resources.
/// </summary>
public interface IMigrationJobStore
{
    Task<MigrationJob?> GetAsync(
        Guid jobId,
        CancellationToken ct);

    /// <summary>
    /// Discovers active, non-terminal migration jobs whose worker leases expired before <paramref name="cutoffUtc"/>.
    /// </summary>
    Task<IReadOnlyList<MigrationJob>> GetJobsNeedingRecoveryAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken ct);

    /// <summary>
    /// Creates a job using a required, non-empty idempotency key scoped to the
    /// authenticated owner and this operation. A repeated key with the same
    /// direction returns the original job as a replay result. Reusing that key
    /// with a different direction returns a typed
    /// <see cref="MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload"/>
    /// conflict result and creates no job.
    /// </summary>
    /// <param name="direction">The migration direction included in the creation payload.</param>
    /// <param name="idempotencyKey">A required, non-empty key for this owner-scoped create operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created or replayed original job, or a typed idempotency conflict result.</returns>
    Task<MigrationIdempotencyResult<MigrationJob>> CreateAsync(
        MigrationDirection direction,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// Transitions a job under the caller's active worker lease.
    /// <paramref name="leaseToken"/> is an opaque concurrency token and MUST match
    /// the currently active lease, which MUST NOT have expired according to the
    /// store's authoritative clock. A mismatched or expired lease fails with a
    /// typed concurrency conflict; a mutation never revives an expired lease.
    /// </summary>
    Task<MigrationJob> TransitionAsync(
        Guid jobId,
        MigrationJobState targetState,
        string leaseToken,
        CancellationToken ct);

    /// <summary>
    /// Updates progress under the caller's active worker lease.
    /// <paramref name="leaseToken"/> is an opaque concurrency token and MUST match
    /// the currently active lease, which MUST NOT have expired according to the
    /// store's authoritative clock. A mismatched or expired lease fails with a
    /// typed concurrency conflict; a mutation never revives an expired lease.
    /// </summary>
    Task UpdateProgressAsync(
        Guid jobId,
        MigrationProgress progress,
        string leaseToken,
        CancellationToken ct);

    /// <summary>
    /// Attempts to acquire the worker lease for a job. The store obtains its
    /// authoritative current time itself, atomically with the compare-and-set, and
    /// sets the lease expiry to that time plus <paramref name="leaseDuration"/>.
    /// Acquisition succeeds when the job is non-terminal and either no lease exists
    /// or the current lease already expired. The returned token is an opaque
    /// concurrency capability required by <see cref="TransitionAsync"/>,
    /// <see cref="UpdateProgressAsync"/>, <see cref="RenewLeaseAsync"/>, and
    /// <see cref="ReleaseLeaseAsync"/>.
    /// </summary>
    /// <param name="jobId">The job to lease.</param>
    /// <param name="leaseDuration">
    /// Requested lease duration, measured from the store's authoritative current
    /// time. Must be positive; implementations must reject a non-positive duration.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The opaque lease token on success, or <see langword="null"/> when another
    /// unexpired lease owns the job. This member is non-throwing for lease contention.
    /// </returns>
    Task<string?> TryAcquireLeaseAsync(
        Guid jobId,
        TimeSpan leaseDuration,
        CancellationToken ct);

    /// <summary>
    /// Renews the caller's active worker lease. The store obtains its authoritative
    /// current time itself and sets the new expiry to that time plus
    /// <paramref name="leaseDuration"/>.
    /// </summary>
    /// <param name="jobId">The leased job.</param>
    /// <param name="leaseToken">The opaque token of the caller's current lease.</param>
    /// <param name="leaseDuration">
    /// Requested renewal duration, measured from the store's authoritative current
    /// time. Must be positive.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the lease was renewed; <see langword="false"/>
    /// when the supplied token is superseded or the lease has already expired
    /// according to the store's authoritative clock. Renewal never revives an
    /// expired or superseded lease and is non-throwing for lease contention.
    /// </returns>
    Task<bool> RenewLeaseAsync(
        Guid jobId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken ct);

    /// <summary>
    /// Releases the caller's worker lease so another worker may acquire it.
    /// The lease token is opaque. Releasing with a superseded or already-expired
    /// token is a no-op rather than an error.
    /// </summary>
    Task ReleaseLeaseAsync(
        Guid jobId,
        string leaseToken,
        CancellationToken ct);

    /// <summary>
    /// Cancels a job from any active state before Activating.
    /// Discards uncommitted staged temporary data and marks session cancelled.
    /// </summary>
    Task CancelAsync(
        Guid jobId,
        MigrationCancelRequest request,
        CancellationToken ct);

    /// <summary>
    /// Retries a failed, cancelled, or expired job.
    /// Reuses completed valid transfer chunks when file fingerprint matches.
    /// </summary>
    Task<MigrationJob> RetryAsync(
        Guid jobId,
        MigrationRetryRequest request,
        CancellationToken ct);
}

/// <summary>
/// Creates and restores mandatory recovery material for migration jobs owned by
/// the authenticated Nostos principal. Implementations MUST resolve the job's
/// resources from authenticated ownership and MUST NOT accept caller-supplied
/// account IDs, database names, storage keys, or provider-specific resource IDs.
/// </summary>
public interface IMigrationRecoveryService
{
    Task<MigrationRecoverySnapshot> CreateRecoverySnapshotAsync(
        Guid jobId,
        CancellationToken ct);

    Task RestoreRecoverySnapshotAsync(
        Guid jobId,
        CancellationToken ct);

    Task DeleteExpiredRecoverySnapshotsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken ct);
}

/// <summary>
/// Performs migration transfer and activation preparation for jobs owned by the
/// authenticated Nostos principal. Implementations MUST authenticate and
/// authorize job ownership before exposing or mutating transfer state. Session
/// IDs, job IDs, and transfer tokens are opaque and MUST NOT expose provider
/// resource identifiers.
/// </summary>
public interface IMigrationTransferService
{
    /// <summary>
    /// Starts a transfer session. The required request idempotency key is scoped
    /// to the authenticated owner and parent job. A repeated key with the same
    /// session payload returns the original session as a replay result; the same
    /// key with a different payload returns a typed idempotency conflict result.
    /// </summary>
    /// <param name="jobId">The owning migration job.</param>
    /// <param name="request">Session size, chunking, file identity, purpose, and required idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created or replayed original session, or a typed idempotency conflict result.</returns>
    Task<MigrationIdempotencyResult<MigrationSessionStatus>> CreateSessionAsync(
        Guid jobId,
        MigrationSessionRequest request,
        CancellationToken ct);

    Task<MigrationSessionStatus> GetSessionAsync(
        Guid jobId,
        Guid sessionId,
        CancellationToken ct);

    Task<MigrationChunkUploadResult> UploadChunkAsync(
        Guid jobId,
        Guid sessionId,
        int chunkIndex,
        Stream content,
        CancellationToken ct);

    Task<MigrationSessionStatus> CompleteSessionAsync(
        Guid jobId,
        Guid sessionId,
        CancellationToken ct);

    Task<MigrationActivationPreparation> PrepareActivationAsync(
        Guid jobId,
        CancellationToken ct);
}

// Nostos.Product/Services/Portability/MigrationContracts.cs

using System.Collections.ObjectModel;

namespace Nostos.Backend.Services.Portability;

public enum MigrationDirection
{
    Export = 0,
    Import = 1,
}

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
    private static readonly IReadOnlySet<MigrationJobState> PendingTransitions =
        new HashSet<MigrationJobState>
        {
            MigrationJobState.Preparing,
            MigrationJobState.Cancelled,
            MigrationJobState.Expired,
            MigrationJobState.Failed,
        };

    private static readonly IReadOnlySet<MigrationJobState> PreparingTransitions =
        new HashSet<MigrationJobState>
        {
            MigrationJobState.Transferring,
            MigrationJobState.Cancelled,
            MigrationJobState.Expired,
            MigrationJobState.Failed,
        };

    private static readonly IReadOnlySet<MigrationJobState> TransferringTransitions =
        new HashSet<MigrationJobState>
        {
            MigrationJobState.Validating,
            MigrationJobState.Cancelled,
            MigrationJobState.Expired,
            MigrationJobState.Failed,
        };

    private static readonly IReadOnlySet<MigrationJobState> ValidatingTransitions =
        new HashSet<MigrationJobState>
        {
            MigrationJobState.ReadyToActivate,
            MigrationJobState.Cancelled,
            MigrationJobState.Expired,
            MigrationJobState.Failed,
        };

    private static readonly IReadOnlySet<MigrationJobState> ReadyToActivateTransitions =
        new HashSet<MigrationJobState>
        {
            MigrationJobState.Activating,
            MigrationJobState.Cancelled,
            MigrationJobState.Expired,
            MigrationJobState.Failed,
        };

    private static readonly IReadOnlySet<MigrationJobState> ActivatingTransitions =
        new HashSet<MigrationJobState>
        {
            MigrationJobState.Completed,
            MigrationJobState.Failed,
        };

    private static readonly IReadOnlySet<MigrationJobState> NoTransitions =
        new HashSet<MigrationJobState>();

    public static IReadOnlyDictionary<MigrationJobState, IReadOnlySet<MigrationJobState>> AllowedTransitions { get; } =
        new ReadOnlyDictionary<MigrationJobState, IReadOnlySet<MigrationJobState>>(
            new Dictionary<MigrationJobState, IReadOnlySet<MigrationJobState>>
            {
                [MigrationJobState.Pending] = PendingTransitions,
                [MigrationJobState.Preparing] = PreparingTransitions,
                [MigrationJobState.Transferring] = TransferringTransitions,
                [MigrationJobState.Validating] = ValidatingTransitions,
                [MigrationJobState.ReadyToActivate] = ReadyToActivateTransitions,
                [MigrationJobState.Activating] = ActivatingTransitions,
                [MigrationJobState.Completed] = NoTransitions,
                [MigrationJobState.Failed] = NoTransitions,
                [MigrationJobState.Cancelled] = NoTransitions,
                [MigrationJobState.Expired] = NoTransitions,
            });

    public static bool CanTransition(MigrationJobState current, MigrationJobState target)
    {
        return AllowedTransitions.TryGetValue(current, out var targets)
            && targets.Contains(target);
    }

    public static void ValidateTransition(MigrationJobState current, MigrationJobState target)
    {
        if (!CanTransition(current, target))
        {
            throw new InvalidOperationException(
                $"Illegal migration job state transition from '{current}' to '{target}'.");
        }
    }

    public static bool IsTerminal(MigrationJobState state)
    {
        return state is
            MigrationJobState.Completed or
            MigrationJobState.Failed or
            MigrationJobState.Cancelled or
            MigrationJobState.Expired;
    }

    public static bool IsRetryable(MigrationJobState state)
    {
        return state is
            MigrationJobState.Failed or
            MigrationJobState.Cancelled or
            MigrationJobState.Expired;
    }
}

public enum MigrationPreflightDecision
{
    AllowedEmpty = 0,
    AllowedReplacementRequired = 1,
    RejectedIncompatible = 2,
    RejectedInsufficientStorage = 3,
    RejectedDestinationConflict = 4,
    RejectedOperationalBackupNotPortable = 5,
}

public enum MigrationDestinationStatus
{
    Empty = 0,
    Populated = 1,
    RevisionMismatch = 2,
}

public enum MigrationSessionPurpose
{
    Export = 0,
    Import = 1,
}

public enum MigrationSessionState
{
    Pending = 0,
    Active = 1,
    Completed = 2,
    Cancelled = 3,
    Expired = 4,
    Failed = 5,
}

public enum MigrationProgressPhase
{
    Preparing = 0,
    Transferring = 1,
    Validating = 2,
    ReadyToActivate = 3,
    Activating = 4,
    Completed = 5,
}

public enum MigrationRecoveryStatus
{
    Retained = 0,
    Restored = 1,
    Expired = 2,
    Purged = 3,
}

public sealed record MigrationArchiveCounts(
    long Works = 0,
    long Books = 0,
    long Notes = 0,
    long Writings = 0,
    long Topics = 0,
    long Collections = 0,
    long CollectionMemberships = 0,
    long Acquisitions = 0,
    long AssistantSettings = 0,
    long NoteImportBookLinks = 0,
    long MediaEntries = 0)
{
    public static MigrationArchiveCounts Empty { get; } = new();
}

public sealed record MigrationExistingCounts(
    long Works = 0,
    long Books = 0,
    long Notes = 0,
    long Writings = 0,
    long Topics = 0,
    long Collections = 0,
    long BookCollections = 0,
    long Acquisitions = 0,
    long WritingNotes = 0,
    long NoteImportBookLinks = 0);

public sealed record MigrationPreflightRequest(
    MigrationDirection Direction,
    MigrationArchiveCounts IncomingCounts,
    long DeclaredArchiveBytes,
    long DeclaredMediaBytes,
    long MaxSingleEntryBytes,
    int DeclaredFormatVersion,
    int DeclaredDataVersion,
    string? DeclaredFormatName = null,
    string? ClientDestinationRevision = null);

public sealed record MigrationPreflightResult(
    MigrationPreflightDecision Decision,
    bool IsCompatible,
    MigrationDestinationStatus DestinationStatus,
    MigrationArchiveCounts IncomingCounts,
    MigrationExistingCounts ExistingCounts,
    long RequiredStorageBytes,
    long AvailableStorageBytes,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string DestinationRevision);

public sealed record MigrationPreflightEvaluationInput(
    MigrationPreflightRequest Request,
    string DestinationRevision,
    bool DestinationIsEmpty,
    MigrationExistingCounts ExistingCounts,
    long AvailableStorageBytes,
    IReadOnlySet<int>? SupportedFormatVersions = null,
    IReadOnlySet<int>? SupportedDataVersions = null,
    long? MaxArchiveBytes = null,
    long? MaxMediaBytes = null,
    long? MaxSingleEntryBytes = null);

public static class MigrationPreflightEvaluator
{
    public static MigrationPreflightResult Evaluate(MigrationPreflightEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Request);
        ArgumentNullException.ThrowIfNull(input.DestinationRevision);

        var request = input.Request;
        var errors = new List<string>();
        var warnings = new List<string>();

        ValidateNonNegative(request.DeclaredArchiveBytes, nameof(request.DeclaredArchiveBytes), errors);
        ValidateNonNegative(request.DeclaredMediaBytes, nameof(request.DeclaredMediaBytes), errors);
        ValidateNonNegative(request.MaxSingleEntryBytes, nameof(request.MaxSingleEntryBytes), errors);
        ValidateCounts(request.IncomingCounts, errors);

        // Operational backup detection: if format name indicates host operational backup, reject explicitly
        if (!string.IsNullOrWhiteSpace(request.DeclaredFormatName)
            && (string.Equals(request.DeclaredFormatName, "nostos-operational-backup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(request.DeclaredFormatName, "operational-backup", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("The selected file is a host operational backup, not a portable library archive.");
            return new MigrationPreflightResult(
                Decision: MigrationPreflightDecision.RejectedOperationalBackupNotPortable,
                IsCompatible: false,
                DestinationStatus: input.DestinationIsEmpty ? MigrationDestinationStatus.Empty : MigrationDestinationStatus.Populated,
                IncomingCounts: request.IncomingCounts,
                ExistingCounts: input.ExistingCounts,
                RequiredStorageBytes: 0,
                AvailableStorageBytes: input.AvailableStorageBytes,
                Errors: errors.AsReadOnly(),
                Warnings: warnings.AsReadOnly(),
                DestinationRevision: input.DestinationRevision);
        }

        var supportedFormatVersions = input.SupportedFormatVersions ?? new HashSet<int> { PortableArchiveFormat.Version };
        var supportedDataVersions = input.SupportedDataVersions ?? new HashSet<int> { 1, 2, 3 };

        var formatCompatible = supportedFormatVersions.Contains(request.DeclaredFormatVersion);
        if (!formatCompatible)
        {
            errors.Add($"Archive format version {request.DeclaredFormatVersion} is not supported.");
        }

        var dataCompatible = supportedDataVersions.Contains(request.DeclaredDataVersion);
        if (!dataCompatible)
        {
            errors.Add($"Archive data version {request.DeclaredDataVersion} is not supported.");
        }

        if (input.MaxArchiveBytes is { } maxArchiveBytes && request.DeclaredArchiveBytes > maxArchiveBytes)
        {
            errors.Add($"Declared archive size {request.DeclaredArchiveBytes} bytes exceeds maximum allowed size of {maxArchiveBytes} bytes.");
        }

        if (input.MaxMediaBytes is { } maxMediaBytes && request.DeclaredMediaBytes > maxMediaBytes)
        {
            errors.Add($"Declared media size {request.DeclaredMediaBytes} bytes exceeds maximum allowed size of {maxMediaBytes} bytes.");
        }

        if (input.MaxSingleEntryBytes is { } maxSingleEntryBytes && request.MaxSingleEntryBytes > maxSingleEntryBytes)
        {
            errors.Add($"Largest declared archive entry {request.MaxSingleEntryBytes} bytes exceeds maximum allowed entry size of {maxSingleEntryBytes} bytes.");
        }

        var destinationStatus = input.DestinationIsEmpty
            ? MigrationDestinationStatus.Empty
            : MigrationDestinationStatus.Populated;

        if (request.ClientDestinationRevision is not null
            && !string.Equals(request.ClientDestinationRevision, input.DestinationRevision, StringComparison.Ordinal))
        {
            destinationStatus = MigrationDestinationStatus.RevisionMismatch;
            errors.Add("The destination changed after preflight. Run preflight again before continuing.");
        }

        if (!input.DestinationIsEmpty)
        {
            warnings.Add("Destination library is populated. Replacing it requires explicit confirmation and creates a retained recovery snapshot.");
        }

        long requiredStorageBytes;
        try
        {
            // Combined staging and final capacity: archive staging + extracted media
            requiredStorageBytes = checked(request.DeclaredArchiveBytes + request.DeclaredMediaBytes);
        }
        catch (OverflowException)
        {
            requiredStorageBytes = long.MaxValue;
            errors.Add("Declared storage requirements exceed supported numeric limits.");
        }

        if (input.AvailableStorageBytes < 0)
        {
            errors.Add("Available storage bytes cannot be negative.");
        }
        else if (requiredStorageBytes > input.AvailableStorageBytes)
        {
            errors.Add($"Migration requires {requiredStorageBytes} bytes but only {input.AvailableStorageBytes} bytes are available.");
        }

        var isCompatible = formatCompatible && dataCompatible && errors.Count == 0;
        MigrationPreflightDecision decision;

        if (destinationStatus == MigrationDestinationStatus.RevisionMismatch)
        {
            decision = MigrationPreflightDecision.RejectedDestinationConflict;
        }
        else if (requiredStorageBytes > input.AvailableStorageBytes)
        {
            decision = MigrationPreflightDecision.RejectedInsufficientStorage;
        }
        else if (!isCompatible)
        {
            decision = MigrationPreflightDecision.RejectedIncompatible;
        }
        else if (input.DestinationIsEmpty)
        {
            decision = MigrationPreflightDecision.AllowedEmpty;
        }
        else
        {
            decision = MigrationPreflightDecision.AllowedReplacementRequired;
        }

        return new MigrationPreflightResult(
            Decision: decision,
            IsCompatible: isCompatible,
            DestinationStatus: destinationStatus,
            IncomingCounts: request.IncomingCounts,
            ExistingCounts: input.ExistingCounts,
            RequiredStorageBytes: requiredStorageBytes,
            AvailableStorageBytes: input.AvailableStorageBytes,
            Errors: errors.AsReadOnly(),
            Warnings: warnings.AsReadOnly(),
            DestinationRevision: input.DestinationRevision);
    }

    private static void ValidateNonNegative(long value, string name, ICollection<string> errors)
    {
        if (value < 0)
            errors.Add($"{name} cannot be negative.");
    }

    private static void ValidateCounts(MigrationArchiveCounts counts, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var values = new (string Name, long Value)[]
        {
            (nameof(counts.Works), counts.Works),
            (nameof(counts.Books), counts.Books),
            (nameof(counts.Notes), counts.Notes),
            (nameof(counts.Writings), counts.Writings),
            (nameof(counts.Topics), counts.Topics),
            (nameof(counts.Collections), counts.Collections),
            (nameof(counts.CollectionMemberships), counts.CollectionMemberships),
            (nameof(counts.Acquisitions), counts.Acquisitions),
            (nameof(counts.AssistantSettings), counts.AssistantSettings),
            (nameof(counts.NoteImportBookLinks), counts.NoteImportBookLinks),
            (nameof(counts.MediaEntries), counts.MediaEntries),
        };

        foreach (var (name, value) in values)
        {
            if (value < 0)
                errors.Add($"Incoming archive count '{name}' cannot be negative.");
        }
    }
}

public sealed record MigrationSessionRequest(
    Guid JobId,
    MigrationSessionPurpose Purpose,
    long TotalBytes,
    int PreferredChunkBytes,
    string? IdempotencyKey = null,
    string? FileFingerprint = null);

public sealed record MigrationSession(
    Guid SessionId,
    Guid JobId,
    MigrationSessionPurpose Purpose,
    MigrationSessionState State,
    int ChunkBytes,
    long TotalBytes,
    long CompletedBytes,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    string? FileFingerprint = null);

public sealed record MigrationUploadChunkRequest(
    Guid SessionId,
    long Offset,
    ReadOnlyMemory<byte> Content,
    string? ChunkChecksum = null);

public sealed record MigrationUploadChunkResult(
    Guid SessionId,
    long Offset,
    int AcceptedBytes,
    long CompletedBytes,
    bool AlreadyPresent,
    bool IsComplete);

public sealed record MigrationDownloadChunkRequest(
    Guid SessionId,
    long Offset,
    int Length);

public sealed record MigrationDownloadChunkResult(
    Guid SessionId,
    long Offset,
    ReadOnlyMemory<byte> Content,
    long TotalBytes,
    bool IsComplete,
    string? ChunkChecksum = null);

public sealed record MigrationTransferProgress(
    Guid JobId,
    MigrationProgressPhase Phase,
    long CompletedBytes,
    long TotalBytes,
    long CompletedItems = 0,
    long TotalItems = 0);

public sealed record MigrationJobStatus(
    Guid JobId,
    MigrationDirection Direction,
    MigrationJobState State,
    MigrationProgressPhase? ProgressPhase,
    long CompletedBytes,
    long TotalBytes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? DestinationRevision = null);

public static class MigrationRecoveryConstants
{
    public const int DefaultRetentionDays = 7;
}

public sealed record MigrationRecoverySnapshot(
    string SnapshotId,
    Guid JobId,
    MigrationRecoveryStatus Status,
    long WorksCount,
    long BooksCount,
    long SizeBytes,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    string? DestinationRevision = null);

public sealed record MigrationActivationRequest(
    Guid JobId,
    string ExpectedDestinationRevision,
    bool ConfirmReplacement);

public sealed record MigrationActivationResult(
    bool Success,
    DateTime? ActivatedAtUtc,
    string? RetainedRecoverySnapshotId);

public sealed record MigrationCancelRequest(
    Guid JobId,
    string? Reason = null);

public sealed record MigrationRetryRequest(
    Guid JobId,
    string? IdempotencyKey = null);

public interface IMigrationRecoveryService
{
    Task<IReadOnlyList<MigrationRecoverySnapshot>> ListSnapshotsAsync(
        CancellationToken cancellationToken = default);

    Task<MigrationRecoverySnapshot?> GetSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task RestoreSnapshotAsync(
        string snapshotId,
        CancellationToken cancellationToken = default);

    Task PurgeExpiredSnapshotsAsync(
        CancellationToken cancellationToken = default);
}

public interface IMigrationJobStore
{
    Task<MigrationJobStatus?> GetAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task CreateAsync(
        MigrationJobStatus job,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<bool> TryAcquireLeaseAsync(
        Guid jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> TryRenewLeaseAsync(
        Guid jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task ReleaseLeaseAsync(
        Guid jobId,
        string leaseOwner,
        CancellationToken cancellationToken = default);

    Task<bool> TryTransitionAsync(
        Guid jobId,
        MigrationJobState expectedState,
        MigrationJobState targetState,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken = default);

    Task UpdateProgressAsync(
        Guid jobId,
        MigrationTransferProgress progress,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid jobId,
        string errorCode,
        string errorMessage,
        DateTime failedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IMigrationTransferService
{
    Task<MigrationPreflightResult> PreflightAsync(
        MigrationPreflightRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationSession> CreateSessionAsync(
        MigrationSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationSession?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<MigrationUploadChunkResult> UploadChunkAsync(
        MigrationUploadChunkRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationDownloadChunkResult> DownloadChunkAsync(
        MigrationDownloadChunkRequest request,
        CancellationToken cancellationToken = default);

    Task CompleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task CancelSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<MigrationActivationResult> ActivateAsync(
        MigrationActivationRequest request,
        CancellationToken cancellationToken = default);
}

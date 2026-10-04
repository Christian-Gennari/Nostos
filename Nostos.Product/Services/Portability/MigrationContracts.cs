// Nostos.Product/Services/Portability/MigrationContracts.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nostos.Backend.Services.Portability;

public enum MigrationDirection
{
    Export,
    Import
}

public enum MigrationPreflightDecision
{
    AllowedEmpty,
    AllowedReplacementRequired,
    RejectedIncompatible,
    RejectedInsufficientStorage,
    RejectedDestinationConflict,
    RejectedInvalidManifest,
    RejectedUnsupportedFormat,
    RejectedUnsupportedDataVersion
}

public enum MigrationDestinationStatus
{
    Empty,
    Populated
}

public enum MigrationJobState
{
    Pending,
    Preparing,
    Transferring,
    Validating,
    ReadyToActivate,
    Activating,
    Completed,
    Failed,
    Cancelled,
    Expired
}

public enum MigrationSessionPurpose
{
    Export,
    Import
}

public enum MigrationSessionState
{
    Pending,
    Active,
    Completed,
    Cancelled,
    Expired,
    Failed
}

public enum MigrationProgressPhase
{
    Preparing,
    Uploading,
    Downloading,
    Verifying,
    Activating
}

public sealed record MigrationArchiveMetadata(
    string? LibraryName,
    string? LibraryDescription,
    DateTimeOffset? ExportedAtUtc);

public sealed record MigrationManifestInfo(
    string ArchiveFormat,
    int FormatVersion,
    int DataVersion,
    long MediaCount,
    long MediaBytesTotal,
    long MaxSingleEntryBytes);

public sealed record MigrationPreflightRequest(
    MigrationDirection Direction,
    MigrationArchiveMetadata? ArchiveMetadata,
    MigrationManifestInfo? ExpectedManifest,
    string? ClientDestinationRevision);

public sealed record MigrationExistingCounts(
    long Works,
    long Books,
    long Notes,
    long Writings);

public sealed record MigrationPreflightResult(
    bool IsCompatible,
    MigrationPreflightDecision Decision,
    MigrationDestinationStatus DestinationStatus,
    string DestinationRevision,
    MigrationExistingCounts ExistingCounts,
    long RequiredStorageBytes,
    long AvailableStorageBytes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public sealed record MigrationSessionRequest(
    Guid JobId,
    MigrationSessionPurpose Purpose,
    long DeclaredSize,
    int ChunkSize,
    int TotalChunks,
    string Sha256Checksum,
    string? ClientRevisionToken);

public sealed record MigrationSessionStatus(
    string SessionId,
    MigrationSessionState State,
    IReadOnlyList<int> ReceivedChunks,
    int ReceivedChunkCount,
    long NextExpectedOffset,
    long BytesReceived,
    long TotalBytes,
    DateTimeOffset ExpiresAtUtc);

public sealed record MigrationChunkUploadResult(
    int ChunkIndex,
    long BytesReceived,
    bool IsComplete,
    int? NextExpectedChunkIndex,
    string? ETag,
    string? ChunkSha256);

public sealed record MigrationJobProgress(
    MigrationProgressPhase CurrentPhase,
    long ProcessedItems,
    long TotalItems,
    long ProcessedBytes,
    long TotalBytes,
    TimeSpan? EstimatedRemainingTime,
    string? PhaseDetailMessage);

public sealed record MigrationJobStatus(
    Guid JobId,
    MigrationDirection Direction,
    MigrationJobState State,
    MigrationJobProgress? Progress,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    bool CancellationRequested,
    string? FailureReason,
    string? ErrorCode);

public sealed record MigrationCancelRequest(
    Guid JobId,
    string? Reason);

public sealed record MigrationRetryRequest(
    Guid JobId);

public sealed record MigrationActivationRequest(
    Guid JobId,
    string ExpectedDestinationRevision,
    bool ConfirmReplacement,
    bool RetainRecoverySnapshot);

public sealed record MigrationActivationResult(
    bool Success,
    DateTimeOffset? ActivatedAtUtc,
    string? PreviousLibraryRecoveryId);

public interface IMigrationJobStore
{
    Task<MigrationJobStatus?> GetAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<MigrationJobStatus> CreateAsync(
        MigrationDirection direction,
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

    Task RequestCancellationAsync(
        MigrationCancelRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationJobStatus> RetryAsync(
        MigrationRetryRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMigrationTransferService
{
    Task<MigrationPreflightResult> PreflightAsync(
        MigrationPreflightRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationSessionStatus> StartSessionAsync(
        MigrationSessionRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationChunkUploadResult> WriteChunkAsync(
        string sessionId,
        int chunkIndex,
        long offset,
        ReadOnlyMemory<byte> content,
        string? chunkSha256 = null,
        CancellationToken cancellationToken = default);

    Task<MigrationSessionStatus?> GetSessionStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<MigrationJobStatus?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    Task<MigrationActivationResult> ActivateAsync(
        MigrationActivationRequest request,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        MigrationCancelRequest request,
        CancellationToken cancellationToken = default);

    Task<MigrationJobStatus> RetryAsync(
        MigrationRetryRequest request,
        CancellationToken cancellationToken = default);
}

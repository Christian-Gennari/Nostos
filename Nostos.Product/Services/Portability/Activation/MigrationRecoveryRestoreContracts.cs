namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Browser-facing status of one retained recovery copy and its restore run
/// (issue #681, Slice 9). The outcome and phase vocabulary is the activation
/// status vocabulary (<see cref="MigrationActivationOutcome"/>,
/// <see cref="MigrationActivationPhase"/>): <c>Idle</c> means the copy is
/// available and no restore has been accepted, <c>Accepted</c>/<c>Running</c>
/// mean the server owns the restore, <c>Completed</c> means the copy was
/// restored, <c>Failed</c> means the current library is unchanged and
/// <see cref="CanRestore"/> tells the browser whether a fresh confirmation may
/// be posted, and <c>RecoveryFailed</c> is fail-closed
/// (<see cref="MaintenanceRequired"/>) until a restart reconciles. Restore
/// specific fields (durable copy <see cref="Status"/>, expiry, size and counts)
/// are preserved. Never carries filesystem paths.
/// </summary>
public sealed record MigrationRecoveryRestoreStatusResponse(
    Guid RecoveryId,
    MigrationRecoveryStatus Status,
    MigrationActivationOutcome Outcome,
    string? ErrorCode = null,
    string? Message = null,
    bool MaintenanceRequired = false,
    DateTimeOffset CreatedAtUtc = default,
    DateTimeOffset ExpiresAtUtc = default,
    long SizeBytes = 0,
    MigrationExistingCounts? Counts = null,
    MigrationActivationPhase? Phase = null,
    bool Accepted = false,
    bool CanRestore = false);

/// <summary>
/// Customer-facing "restore previous library" surface (issue #681, Slice 9).
/// Implemented by the SelfHosted host; mapped by
/// <c>Nostos.Backend.Endpoints.MigrationRecoveryEndpoints</c> under the same
/// route group policies as the migration transfer routes. Listing and status
/// reads answer from durable filesystem manifests only (never SQLite), so they
/// remain usable while the exclusive maintenance window is active; a restore
/// request validates against the live library, durably claims the copy and is
/// queued on the host's single library-switch dispatcher, so it can never run
/// concurrently with an activation.
/// </summary>
public interface ISelfHostedRecoveryRestore
{
    Task<IReadOnlyList<MigrationRecoveryStatusResponse>> ListAsync(CancellationToken ct);

    Task<MigrationRecoveryRestoreStatusResponse> GetStatusAsync(Guid recoveryId, CancellationToken ct);

    Task<MigrationRecoveryRestoreStatusResponse> RequestRestoreAsync(
        Guid recoveryId,
        MigrationRecoveryRestoreRequest request,
        CancellationToken ct);
}

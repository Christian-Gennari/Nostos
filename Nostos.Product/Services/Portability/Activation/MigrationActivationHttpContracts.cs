using System.Text.Json.Serialization;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Browser-facing activation outcome of an owned import job. It is derived from
/// the durable job state plus the in-memory activation run and deliberately
/// distinguishes an ordinary <see cref="Failed"/> activation (the original
/// library is still active and the request can be repeated) from a
/// <see cref="RecoveryFailed"/> host (admission stays closed until a restart
/// reconciles the journal).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationActivationOutcome
{
    /// <summary>The job is prepared but no activation has been accepted.</summary>
    Idle = 0,

    /// <summary>Activation was accepted and is running in the background.</summary>
    Running = 1,

    /// <summary>The cutover committed and the job is completed.</summary>
    Completed = 2,

    /// <summary>Activation failed before or during rollback; the original library is active.</summary>
    Failed = 3,

    /// <summary>Fail-closed: the host stays in maintenance until a restart reconciles.</summary>
    RecoveryFailed = 4,
}

/// <summary>
/// Activation status that stays answerable while the exclusive maintenance
/// window has the live database closed. The same shape is returned by
/// <c>POST /jobs/{id}/activate</c> (202) and
/// <c>GET /jobs/{id}/activation</c> so the browser decodes one body.
/// Never carries filesystem paths, storage keys or provider exceptions.
/// </summary>
public sealed record MigrationActivationStatusResponse(
    Guid JobId,
    MigrationJobState State,
    MigrationActivationOutcome Outcome,
    string? ErrorCode = null,
    string? Message = null,
    bool MaintenanceRequired = false,
    string? DestinationRevision = null,
    MigrationExistingCounts? ExistingCounts = null,
    MigrationDestinationStatus? DestinationStatus = null,
    bool RecoveryAvailable = false,
    DateTimeOffset? RecoveryExpiresAtUtc = null,
    long? RecoverySizeBytes = null);

/// <summary>Expected transport outcomes of an activation request.</summary>
public enum MigrationActivationRequestOutcome
{
    /// <summary>Accepted (or already running/completed); the body is the current status.</summary>
    Accepted = 0,

    /// <summary>A run is active or the host is fail-closed; serve the in-memory status.</summary>
    Replayed = 1,

    /// <summary>The host is in exclusive maintenance and this job has no in-memory snapshot.</summary>
    Busy = 2,

    /// <summary>No such owned import job.</summary>
    NotFound = 3,

    /// <summary>The job direction or state cannot be activated.</summary>
    InvalidState = 4,

    /// <summary>A populated destination requires explicit replacement confirmation.</summary>
    ConfirmationRequired = 5,

    /// <summary>The confirmed destination revision no longer matches.</summary>
    DestinationConflict = 6,

    /// <summary>Activation cannot be admitted (for example a missing prepared staging).</summary>
    Failed = 7,
}

/// <summary>
/// Admission result of <see cref="IMigrationActivationDispatcher.RequestAsync"/>.
/// Conflicts carry the destination facts the browser must show for a re-review.
/// </summary>
public sealed record MigrationActivationRequestResult(
    MigrationActivationRequestOutcome Outcome,
    MigrationActivationStatusResponse? Status = null,
    string? Message = null,
    string? DestinationRevision = null,
    MigrationExistingCounts? ExistingCounts = null,
    MigrationDestinationStatus? DestinationStatus = null);

/// <summary>
/// Host seam behind the activation HTTP routes. The SelfHosted host implements
/// it with the activation coordinator: it admits one background run per job,
/// keeps an in-memory status snapshot that remains answerable while the live
/// database is closed, and never owns the durable cutover itself.
/// </summary>
public interface IMigrationActivationDispatcher
{
    /// <summary>
    /// Validates an explicit activation request against the durable job and the
    /// current destination, then starts (or observes) at most one background
    /// run for the job. Expected outcomes are returned as data so the route can
    /// answer the documented codes; unexpected infrastructure failures throw.
    /// </summary>
    Task<MigrationActivationRequestResult> RequestAsync(
        Guid jobId,
        MigrationActivateRequest request,
        CancellationToken ct);

    /// <summary>
    /// Lightweight activation status. When exclusive maintenance closes
    /// admission this is served purely from the in-memory run snapshot; when
    /// admission is open it may include the durable job row and the retained
    /// recovery manifest. Never opens the live database while it is swapped.
    /// </summary>
    Task<MigrationActivationStatusResponse> GetStatusAsync(
        Guid jobId,
        CancellationToken ct);
}

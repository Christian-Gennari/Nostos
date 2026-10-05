using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Services.Portability;

/// <summary>Confirmation applies only to the exact opaque revision observed at preflight.</summary>
public sealed record MigrationActivateRequest(string DestinationRevision, bool ConfirmReplacement);

/// <summary>Restoring previous portable state also replaces the current library and requires confirmation.</summary>
public sealed record MigrationRecoveryRestoreRequest(string DestinationRevision, bool ConfirmReplacement);

public sealed record MigrationRecoveryStatusResponse(
    Guid JobId,
    MigrationRecoveryStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long SizeBytes,
    MigrationExistingCounts Counts,
    string? RestoreError = null);

/// <summary>
/// Processes an owned import already durably admitted to Activating. The caller's
/// worker token is independent of the browser request. Completion requires durable
/// cutover commit; failure before commit requires the original generation to be safe.
/// </summary>
public interface IMigrationActivationService
{
    Task ActivateAsync(Guid jobId, CancellationToken ct);
}

public static class MigrationActivationErrorCodes
{
    public const string ConfirmationRequired = "migration_replacement_confirmation_required";
    public const string DestinationConflict = "migration_destination_conflict";
    public const string Busy = "migration_activation_busy";
    public const string Failed = "migration_activation_failed";
    public const string RecoveryFailed = "migration_activation_recovery_failed";
    public const string RecoveryNotFound = "migration_recovery_not_found";
    public const string RecoveryExpired = "migration_recovery_expired";
    public const string RecoveryCorrupt = "migration_recovery_corrupt";
    public const string RecoveryRestoreConflict = "migration_recovery_restore_conflict";

    /// <summary>
    /// Activation admission or retention could not reserve the physical bytes
    /// required to build the candidate and retain the previous library. No
    /// cutover may begin (plan section 16.1, error vocabulary of the same name).
    /// </summary>
    public const string StorageExhausted = "migration_storage_exhausted";
}

/// <summary>Safe, provider-neutral error; never carries paths or provider exceptions.</summary>
public sealed class MigrationActivationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Pure admission rules. Run before accepting activation and again under exclusive
/// maintenance with freshly read destination facts. This does not acquire a lease,
/// validate staging bytes, or mutate a job; orchestration must do those separately.
///
/// <para><b>Revision binding (issue #681).</b> A replacement is bound to the
/// revision the user actually reviewed, never to the job's import-start
/// <c>DestinationRevision</c>. The library revision advances on every portable
/// save (reading progress, note edits, metadata enrichment), so requiring the
/// stored baseline to still be current would make any in-use library
/// permanently unactivatable. Admission requires the request revision to equal
/// the current revision; the caller carries that confirmed revision into the
/// exclusive window for the authoritative recheck. A populated destination
/// additionally requires explicit confirmation; an empty one does not.</para>
/// </summary>
public static class MigrationActivationAdmission
{
    public static void Validate(
        MigrationDirection direction,
        MigrationJobState state,
        MigrationActivateRequest request,
        MigrationDestinationStatus destination,
        string currentRevision)
    {
        if (direction != MigrationDirection.Import || state != MigrationJobState.ReadyToActivate)
            throw new MigrationActivationException(MigrationJobStoreErrorCodes.InvalidState,
                "Only a prepared import can be admitted to activation.");

        ValidateReplacement(request.DestinationRevision, request.ConfirmReplacement,
            destination, currentRevision);
    }

    /// <summary>
    /// Admission and the authoritative post-drain recheck. The job-creation
    /// baseline takes no part in this decision: the request revision must equal
    /// the current revision, and a populated destination must be confirmed.
    /// A populated destination without confirmation is refused first so the
    /// client learns the current facts before it confirms them.
    /// </summary>
    public static void ValidateReplacement(
        string requestedRevision,
        bool confirmReplacement,
        MigrationDestinationStatus destination,
        string currentRevision)
    {
        if (destination is not (MigrationDestinationStatus.Empty or MigrationDestinationStatus.Populated))
            throw new MigrationActivationException(MigrationJobStoreErrorCodes.InvalidState,
                "The destination status is unknown.");

        if (destination == MigrationDestinationStatus.Populated && !confirmReplacement)
            throw new MigrationActivationException(MigrationActivationErrorCodes.ConfirmationRequired,
                "Replacing an existing library requires explicit confirmation.");

        if (string.IsNullOrWhiteSpace(requestedRevision)
            || !string.Equals(requestedRevision, currentRevision, StringComparison.Ordinal))
            throw new MigrationActivationException(MigrationActivationErrorCodes.DestinationConflict,
                "The destination changed. Review replacement again.");
    }

    public static void ValidateRecoveryRestore(
        MigrationRecoveryRestoreRequest request,
        string currentRevision)
    {
        // A recovery restore always requires confirmation, including an empty current library.
        ValidateReplacement(request.DestinationRevision, request.ConfirmReplacement,
            MigrationDestinationStatus.Populated, currentRevision);
    }
}

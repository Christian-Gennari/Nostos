namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Stable machine-readable error codes for <see cref="EfMigrationJobStore"/>.
/// The HTTP layer maps these to the transport error shape; the codes are part
/// of the operational contract and must not be renamed casually.
/// </summary>
public static class MigrationJobStoreErrorCodes
{
    public const string NotFound = "migration_not_found";
    public const string LeaseConflict = "migration_lease_conflict";
    public const string InvalidState = "migration_invalid_state";
    public const string CannotCancel = "migration_cannot_cancel";
    public const string NotRetryable = "migration_not_retryable";
}

/// <summary>
/// Typed failure raised by the durable migration job store. Every failure
/// carries one of the <see cref="MigrationJobStoreErrorCodes"/> so callers can
/// react to a stable code instead of parsing messages.
/// </summary>
public sealed class MigrationJobStoreException : Exception
{
    public MigrationJobStoreException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public static MigrationJobStoreException NotFound(Guid jobId) =>
        new(
            MigrationJobStoreErrorCodes.NotFound,
            $"Migration job {jobId} was not found.");

    public static MigrationJobStoreException LeaseConflict(Guid jobId) =>
        new(
            MigrationJobStoreErrorCodes.LeaseConflict,
            $"The worker lease for migration job {jobId} is missing, expired, superseded, " +
            "or the job changed concurrently.");

    public static MigrationJobStoreException InvalidState(Guid jobId, string message) =>
        new(MigrationJobStoreErrorCodes.InvalidState, message);

    public static MigrationJobStoreException CannotCancel(Guid jobId) =>
        new(
            MigrationJobStoreErrorCodes.CannotCancel,
            $"Migration job {jobId} has entered activation and can no longer be cancelled.");

    public static MigrationJobStoreException NotRetryable(Guid jobId, MigrationJobState state) =>
        new(
            MigrationJobStoreErrorCodes.NotRetryable,
            $"Migration job {jobId} is {state} and cannot be retried.");
}

namespace Nostos.Backend.Services.Recovery;

/// <summary>
/// Provider-neutral read-only view of the managed backups available to the
/// authenticated customer. Implementations must scope results to the caller's
/// server-side identity; callers do not supply a tenant or account selector.
/// </summary>
public interface IManagedBackupCatalog
{
    Task<ManagedBackupListing> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Restores a backup belonging to the authenticated customer.
/// Implementations resolve tenant ownership server-side.
/// </summary>
public interface IManagedBackupRestorer
{
    Task<ManagedBackupRestoreResult> RestoreAsync(
        Guid backupId,
        CancellationToken cancellationToken = default);
}

public sealed record ManagedBackupRestoreRequest(bool Confirm);

public sealed record ManagedBackupRestoreResult(
    Guid BackupId,
    DateTime RestoredAtUtc);

public enum ManagedBackupRestoreError
{
    NotFound,
    Conflict,
    InvalidBackup
}

/// <summary>
/// A known provider rejection, not an unknown transport or activation outcome.
/// </summary>
public sealed class ManagedBackupRestoreException(
    ManagedBackupRestoreError code,
    string message) : Exception(message)
{
    public ManagedBackupRestoreError Code { get; } = code;
}

/// <summary>
/// The product-level response exposed at GET /api/managed-backups.
/// </summary>
public sealed record ManagedBackupListing(
    int RetentionDays,
    IReadOnlyList<ManagedBackupSummary> Backups);

/// <summary>
/// A completed backup that can be listed to a customer. State is the stable
/// lowercase product value "completed"; in-progress and failed operations are
/// not part of this customer list. This does not claim that a restore has been
/// exercised or independently verified.
/// </summary>
public sealed record ManagedBackupSummary(
    Guid Id,
    DateTime CreatedAtUtc,
    long ArchiveBytes,
    long MediaBytes,
    string State);

/// <summary>
/// Raised by the default catalog when a host has no managed-backup provider.
/// </summary>
public sealed class ManagedBackupsNotSupportedException()
    : NotSupportedException("Managed backups are not supported by this Nostos host.")
{
}

/// <summary>
/// Fail-closed product default. Hosted compositions replace this only when
/// they can provide an authorized customer-scoped catalog.
/// </summary>
public sealed class NotSupportedManagedBackupCatalog : IManagedBackupCatalog
{
    public Task<ManagedBackupListing> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<ManagedBackupListing>(new ManagedBackupsNotSupportedException());
}

/// <summary>
/// Fail-closed default when the host supplies no managed restore adapter.
/// </summary>
public sealed class NotSupportedManagedBackupRestorer : IManagedBackupRestorer
{
    public Task<ManagedBackupRestoreResult> RestoreAsync(
        Guid backupId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ManagedBackupRestoreResult>(
            new ManagedBackupsNotSupportedException());
}

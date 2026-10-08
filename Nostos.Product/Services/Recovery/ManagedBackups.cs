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

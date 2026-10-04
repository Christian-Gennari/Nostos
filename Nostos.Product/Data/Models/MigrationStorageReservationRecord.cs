namespace Nostos.Backend.Data.Models;

// Durable, race-safe storage admission reservation (issue #679). Preflight
// reserves peak bytes before a large upload; capacity accounting subtracts only
// max(ReservedBytes - MaterializedBytes, 0), because materialized bytes are
// already reflected in physical free space. ClaimedJobId is set atomically at
// job creation, and ReleasedAtUtc takes the reservation out of capacity
// accounting. Host-local operational state: never portable library content.
// Version is an explicit integer concurrency token.
public class MigrationStorageReservationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Purpose { get; set; }

    public long ReservedBytes { get; set; }

    public long MaterializedBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAtUtc { get; set; }

    public Guid? ClaimedJobId { get; set; }

    public DateTime? ReleasedAtUtc { get; set; }

    public long Version { get; set; }
}

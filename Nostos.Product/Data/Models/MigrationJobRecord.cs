using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// Durable operational record for one library transfer job (issue #679). This is
// host-local operational state — leases, progress, idempotency, failure and
// expiry bookkeeping — and is explicitly excluded from portable archives; see
// PortableCompletenessInventoryTests. It stores no account identifiers and no
// absolute filesystem paths, only generated relative storage references held by
// the session/artifact records. Version is an explicit integer concurrency
// token: every guarded mutation increments it before saving.
public class MigrationJobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Direction { get; set; }

    public int State { get; set; }

    public int RecoveryStatus { get; set; }

    public int ProgressPhase { get; set; }

    public long ProgressBytesProcessed { get; set; }

    public long? ProgressTotalBytes { get; set; }

    public int? ProgressCompletedChunks { get; set; }

    public int? ProgressTotalChunks { get; set; }

    [MaxLength(512)]
    public string? ProgressMessage { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? HeartbeatAtUtc { get; set; }

    [MaxLength(128)]
    public string? MigrationLeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(64)]
    public string CreationPayloadHash { get; set; } = string.Empty;

    [MaxLength(96)]
    public string? FailureCode { get; set; }

    [MaxLength(1024)]
    public string? FailureMessage { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public int AttemptNumber { get; set; } = 1;

    [MaxLength(256)]
    public string? DestinationRevision { get; set; }

    public Guid? PreparedStagingId { get; set; }

    public string? PreparedImportMetadataJson { get; set; }

    public long ReservedStorageBytes { get; set; }

    public Guid? ReservationId { get; set; }

    [MaxLength(512)]
    public string? CancellationReason { get; set; }

    public DateTimeOffset? CancelledAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public long Version { get; set; }
}

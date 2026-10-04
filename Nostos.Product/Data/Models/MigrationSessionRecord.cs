using System.ComponentModel.DataAnnotations;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Data.Models;

// Durable operational record for one resumable upload session (issue #679).
// Host-local transfer state: the receipt rows, the relative StorageKey and the
// client fingerprint are operational bookkeeping, never portable library
// content. Deletion of the owning job cascades to its sessions during explicit
// retention cleanup. Version is an explicit integer concurrency token.
public class MigrationSessionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }

    public int Purpose { get; set; }

    public int State { get; set; }

    public long TotalBytes { get; set; }

    public int ChunkSize { get; set; } = MigrationContractLimits.DefaultChunkBytes;

    public int TotalChunks { get; set; }

    public long FileIdentitySizeBytes { get; set; }

    [MaxLength(64)]
    public string FileIdentitySha256 { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? ClientFingerprint { get; set; }

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(64)]
    public string CreationPayloadHash { get; set; } = string.Empty;

    public long ReceivedBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    [MaxLength(512)]
    public string StorageKey { get; set; } = string.Empty;

    public long Version { get; set; }
}

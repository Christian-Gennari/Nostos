using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// Local operational lifecycle for an export artifact file.
public enum MigrationExportArtifactState
{
    Preparing = 0,
    Available = 1,
    Expired = 2,
    Deleted = 3,
}

// Durable metadata for the generated archive file of an export job (issue #679).
// Host-local operational state: the relative storage key, retention timestamps
// and artifact hash never cross into portable archives. One row per export job;
// deleting the job cascades to its artifact record. Version is an explicit
// integer concurrency token.
public class MigrationExportArtifactRecord
{
    public Guid JobId { get; set; }

    public int State { get; set; }

    [MaxLength(512)]
    public string StorageKey { get; set; } = string.Empty;

    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [MaxLength(64)]
    public string? Sha256 { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? AvailableAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public long Version { get; set; }
}

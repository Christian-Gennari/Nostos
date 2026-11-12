using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

/// <summary>
/// One file of the Carrel Sync Protocol v1 mapped to one Writing Studio draft.
///
/// <para>The protocol identifies a file by a stable <see cref="FileId"/> the
/// device or the server made; Nostos keeps the draft in
/// <see cref="DraftId"/>. The path is derived from the draft's name and its
/// folder chain, and the revision moves whenever the draft or its path
/// changes, whether the change came from the device (<c>PUT</c>) or from
/// someone writing in the Studio.</para>
///
/// <para>Deleting a file leaves a tombstone (the row with
/// <see cref="Deleted"/> set and a final revision), so a device that was
/// offline still learns about the deletion on its next pull. Operational
/// mapping state: never part of a portable archive.</para>
/// </summary>
public class CarrelFileModel
{
    public const int MaxPathLength = 1024;

    public const int HashLength = 64;

    /// <summary>The protocol's stable file id (UUIDv7).</summary>
    public Guid FileId { get; set; }

    [Required]
    [MaxLength(200)]
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>The Writing Studio draft this file is. No FK: a tombstone outlives the draft.</summary>
    public Guid DraftId { get; set; }

    /// <summary>The last path reported to devices, relative and <c>/</c>-separated.</summary>
    [Required]
    [MaxLength(MaxPathLength)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the last version's UTF-8 bytes.</summary>
    [Required]
    [MaxLength(HashLength)]
    public string Hash { get; set; } = string.Empty;

    /// <summary>The protocol revision: 1 when created, +1 on every change and tombstone.</summary>
    public long Rev { get; set; }

    public bool Deleted { get; set; }

    /// <summary>When this row last changed (Unix seconds are derived from it).</summary>
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The file-feed cursor stamped by the change that produced this row.</summary>
    public long Cursor { get; set; }

    /// <summary>
    /// The draft's <c>UpdatedAt</c> when this row was last brought in line, so
    /// reconciliation can tell an unchanged draft from an edited one without
    /// reading every draft's text.
    /// </summary>
    public DateTime? DraftUpdatedAtUtc { get; set; }
}

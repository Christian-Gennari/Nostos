namespace Nostos.Backend.Data.Models;

/// <summary>
/// The sync cursor of one owner, for the two Carrel change feeds: files
/// (Writing Studio drafts) and the library (highlights, notes, passages).
///
/// Both counters only move forward. A change that is committed while another
/// transaction is changing files waits for this row, so cursor order is commit
/// order and a device that stores a cursor can never skip a later change.
/// Operational state: never part of a portable archive.
/// </summary>
public class CarrelSyncStateModel
{
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>Highest cursor handed out on the file feed; 0 before the first change.</summary>
    public long FilesCursor { get; set; }

    /// <summary>Highest cursor handed out on the library feed; 0 before the first change.</summary>
    public long LibraryCursor { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

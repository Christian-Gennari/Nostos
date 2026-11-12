namespace Nostos.Backend.Data.Models;

/// <summary>
/// One note the Carrel library feed has exported, with the cursor of its last
/// change and a signature of the payload it produced.
///
/// <para>Notes are exported as highlights, notes and passages. The feed has to
/// list deletions, and a deleted note leaves no row to look at, so this table
/// is the server's memory of what a device has been told: reconciliation
/// compares the library against it and writes a tombstone when a note (or the
/// book whose notes were deleted with it) is gone.</para>
///
/// Operational state: never part of a portable archive.
/// </summary>
public class CarrelLibraryItemModel
{
    public const int HashLength = 64;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>The note's id, which is also the library item id devices quote with.</summary>
    public Guid NoteId { get; set; }

    /// <summary>The library-feed cursor stamped by the change that produced this row.</summary>
    public long Cursor { get; set; }

    public bool Deleted { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the payload fields (text, note, location, topics,
    /// citation and book) this item last produced. A note whose words, book title
    /// or topics change gets a new signature, so the device is told.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(HashLength)]
    public string Signature { get; set; } = string.Empty;

    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;
}

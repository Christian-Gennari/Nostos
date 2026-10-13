using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

/// <summary>
/// One media file of a book that Nostos reads in place from an
/// administrator-mounted folder instead of owning a copy of it.
///
/// The row is the whole record of external ownership: a book file or track
/// with a row here is NOT in the books directory, and nothing may write to,
/// rename or delete what the row points at. The reference is a root identity
/// plus a path relative to that root, never an absolute server path, so it
/// can be revalidated against the configured root every time it is opened and
/// never reveals the host's layout.
///
/// <see cref="RootId"/> and <see cref="RelativePath"/> together are unique:
/// that is what makes linking the same file twice a no-op instead of a second
/// book.
/// </summary>
public class ExternalMediaLinkModel
{
    /// <summary><see cref="TrackNumber"/> of a book's single primary file.</summary>
    public const int PrimaryFile = 0;

    public const int MaxRelativePathLength = 1024;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }
    public BookModel? Book { get; set; }

    /// <summary>
    /// <see cref="PrimaryFile"/> for the book's one file, or the 1-based
    /// number of a track of a multi-track audiobook.
    /// </summary>
    public int TrackNumber { get; set; }

    /// <summary>Stable identity of the configured root the path is relative to.</summary>
    [Required]
    public string RootId { get; set; } = string.Empty;

    /// <summary>
    /// Forward-slash path below the root, with no empty, <c>.</c> or
    /// <c>..</c> segment. Compared byte for byte: the file systems this is
    /// read from are case-sensitive.
    /// </summary>
    [Required]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Size of the file when it was linked, to notice a replaced file on rescan.</summary>
    public long Length { get; set; }

    /// <summary>Modification time of the file when it was linked.</summary>
    public DateTime LastModifiedUtc { get; set; }

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}

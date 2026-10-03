using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

/// <summary>
/// Where a book's locally stored file came from, when it was not uploaded by
/// hand.
///
/// This lives in its own table with generic columns on purpose. Provider
/// identity is NOT bibliographic identity, so it must not become a column on
/// <see cref="BookModel"/> (a <c>GutenbergId</c> or <c>LibriVoxId</c> column
/// would put provider knowledge into the core library domain, and the next
/// provider would add another one), and it must not become a parallel
/// identity/work system either — matching and grouping stay with
/// <c>ILibraryService</c>.
///
/// <see cref="ProviderId"/>, <see cref="ExternalId"/> and <see cref="AssetId"/>
/// together are unique: that is what makes re-acquiring the same item a no-op
/// instead of a duplicate download and a duplicate book.
/// </summary>
public class BookAcquisitionModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }
    public BookModel? Book { get; set; }

    /// <summary>Stable provider identifier (see <c>IContentProvider.Id</c>).</summary>
    [Required]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// The provider's human-readable name, captured at acquisition time. Stored
    /// rather than looked up so the record still reads correctly if the provider
    /// is later renamed, disabled or removed.
    /// </summary>
    [Required]
    public string ProviderDisplayName { get; set; } = string.Empty;

    /// <summary>The provider's own id for the item, e.g. a Gutenberg ebook number.</summary>
    [Required]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>
    /// Which asset of that item was fetched. Never null (empty string when the
    /// provider names no asset) so the uniqueness rule can actually see it —
    /// SQLite treats NULLs in a unique index as distinct.
    /// </summary>
    [Required]
    public string AssetId { get; set; } = string.Empty;

    /// <summary>The source's own name for the format fetched, e.g. "epub3-images".</summary>
    public string? AssetFormat { get; set; }

    /// <summary>Extension of the file actually stored locally, e.g. ".epub".</summary>
    public string? ImportedExtension { get; set; }

    /// <summary>The item's human-facing page at the source.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// The rights statement exactly as the SOURCE worded it (e.g. "Public domain
    /// in the USA.").
    ///
    /// Stored as a quoted statement rather than a boolean on purpose: Nostos is
    /// in no position to decide that a work is unrestricted in every
    /// jurisdiction, and a flag claiming "free" would have to be un-invented the
    /// first time a real licensing question arrived.
    /// </summary>
    public string? RightsStatement { get; set; }

    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
}

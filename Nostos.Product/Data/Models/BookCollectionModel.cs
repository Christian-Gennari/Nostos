namespace Nostos.Backend.Data.Models;

/// <summary>
/// Join row for the Book ↔ Collection many-to-many.
///
/// A book may belong to any number of collections. This table is the
/// authoritative source of collection membership.
///
/// Delete behaviour is deliberately asymmetric:
///   Book       -> Cascade  (deleting a book must not strand membership rows)
///   Collection -> Restrict (preserves the collections Phase 1 guard: a
///                           non-empty collection cannot be removed by a raw
///                           SQL DELETE behind the service's back)
/// </summary>
public class BookCollectionModel
{
    public Guid BookId { get; set; }
    public BookModel? Book { get; set; }

    public Guid CollectionId { get; set; }
    public CollectionModel? Collection { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

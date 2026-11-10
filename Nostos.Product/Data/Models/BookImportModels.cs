using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// A committed reading-history import (issue #1521): one Goodreads or StoryGraph
// export, so every value it wrote can be taken back as a unit.
public class BookImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(16)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(260)]
    public string? FileName { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // How many values the batch wrote, shown in Recent imports. Derived rows
    // (memberships, notes) count too, so the number matches what undo takes back.
    public int ChangeCount { get; set; }

    public List<BookImportBatchBook> Books { get; set; } = [];
}

// What one import row did, and everything undo needs to put it back. Values
// live in ChangesJson because undo only ever reads them by batch: a column per
// value would be a second schema for the same data.
public class BookImportBatchBook
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BatchId { get; set; }
    public BookImportBatch Batch { get; set; } = null!;

    // Null when the row only became a reading-list entry: a wish is not a book.
    public Guid? BookId { get; set; }
    public BookModel? Book { get; set; }

    // The row's identity inside its export, for the result list and for
    // matching an undo line back to what the owner answered.
    [MaxLength(1024)]
    public string SourceKey { get; set; } = string.Empty;

    public string ChangesJson { get; set; } = "{}";

    // The book this import added, so undo can take it back when nothing else
    // has come to it since. No FK: the pointer is bookkeeping, and a deleted
    // book must never block the delete of anything else.
    public Guid? CreatedBookId { get; set; }

    // The reading-list entry this import added, if any.
    public Guid? ReadingListEntryId { get; set; }

    // The private note this import wrote, and the receipt that makes writing
    // it again on a re-import a no-op. Undo removes both.
    public Guid? NoteId { get; set; }

    [MaxLength(128)]
    public string? NoteIdempotencyKey { get; set; }
}

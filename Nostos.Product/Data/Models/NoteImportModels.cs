using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

// A decision the owner made once during an e-reader import: "this book on the
// device is that book in the library". Keyed by the device's own identity for
// the book, so later imports of the same device need no second confirmation.
public class NoteImportBookLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(16)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string SourceKey { get; set; } = string.Empty;

    public Guid BookId { get; set; }
    public BookModel Book { get; set; } = null!;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

// One committed import of one file, so it can be undone as a unit.
public class NoteImportBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(16)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(260)]
    public string? FileName { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<NoteImportBatchNote> Notes { get; set; } = [];
}

public class NoteImportBatchNote
{
    public Guid BatchId { get; set; }
    public NoteImportBatch Batch { get; set; } = null!;

    public Guid NoteId { get; set; }
    public NoteModel Note { get; set; } = null!;

    // The capture receipt written with the note. Undo removes it too, or the
    // receipt would replay and the note could never be imported again.
    [MaxLength(64)]
    public string ClientId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;
}

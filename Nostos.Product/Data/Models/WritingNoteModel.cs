using System;

namespace Nostos.Backend.Data.Models;

public class WritingNoteModel
{
    public Guid WritingId { get; set; }
    public WritingModel Writing { get; set; } = null!;

    public Guid NoteId { get; set; }
    public NoteModel Note { get; set; } = null!;

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

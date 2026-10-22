namespace Nostos.Backend.Data.Models;

/// <summary>
/// One note a writing cites (issue #1162): a live citation in the writing's
/// content whose <c>src</c> names this note. Derived, never edited by hand:
/// every save rebuilds a writing's rows from its content
/// (<see cref="Services.Writings.WritingCitationIndex"/>), the way a note's
/// body is the record of its <c>[[Topic]]</c> links. Topics are never stored
/// here; they are joined at read time through the note's <c>NoteTopics</c>.
/// Not portable: an archive carries the content, and import rebuilds this.
/// </summary>
public class WritingCitationModel
{
    public Guid WritingId { get; set; }
    public WritingModel Writing { get; set; } = null!;

    public Guid NoteId { get; set; }
    public NoteModel Note { get; set; } = null!;
}

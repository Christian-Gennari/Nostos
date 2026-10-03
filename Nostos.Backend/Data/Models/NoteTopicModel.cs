namespace Nostos.Backend.Data.Models;

public class NoteTopicModel
{
  public Guid NoteId { get; set; }
  public NoteModel Note { get; set; } = null!;

  public Guid TopicId { get; set; }
  public TopicModel Topic { get; set; } = null!;
}

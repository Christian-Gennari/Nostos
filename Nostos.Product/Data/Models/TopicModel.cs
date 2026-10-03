namespace Nostos.Backend.Data.Models;

public class TopicModel
{
  public Guid Id { get; set; } = Guid.NewGuid();
  public string Topic { get; set; } = string.Empty;

  // NEW: Allow us to check if this topic is used anywhere
  public ICollection<NoteTopicModel> NoteTopics { get; set; } = [];
}

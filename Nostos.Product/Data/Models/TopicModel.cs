namespace Nostos.Backend.Data.Models;

public class TopicModel
{
  public Guid Id { get; set; } = Guid.NewGuid();
  public string Topic { get; set; } = string.Empty;

  /// <summary>
  /// The reader's own words about the topic (at most
  /// <see cref="MaxDescriptionLength"/> characters); null when there are none.
  /// A topic with a description is kept even when no note carries it.
  /// </summary>
  public string? Description { get; set; }

  public const int MaxDescriptionLength = 4000;

  // NEW: Allow us to check if this topic is used anywhere
  public ICollection<NoteTopicModel> NoteTopics { get; set; } = [];
}

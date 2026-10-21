namespace Nostos.Backend.Data.Models;

public class TopicModel
{
  public Guid Id { get; set; } = Guid.NewGuid();

  private string _topic = string.Empty;

  /// <summary>The name as the reader wrote it. Setting it sets <see cref="NormalizedName"/>.</summary>
  public string Topic
  {
    get => _topic;
    set
    {
      _topic = value;
      NormalizedName = NormalizeName(value);
    }
  }

  /// <summary>
  /// The topic's identity: <see cref="Topic"/> folded by <see cref="NormalizeName"/>,
  /// unique across the library, so [[gulligt]] and [[Gulligt]] are one topic.
  /// A key that is not its name's folded form marks a case variant still to be
  /// merged (an older archive, a migration that could only fold ASCII); see
  /// <c>ITopicRepository.MergeCaseVariantsAsync</c>.
  /// </summary>
  public string NormalizedName { get; set; } = string.Empty;

  /// <summary>
  /// A topic name's identity key: trimmed, composed (NFC) and upper-cased with
  /// the invariant culture, so every letter folds, not only ASCII.
  /// </summary>
  public static string NormalizeName(string? name) =>
    (name ?? string.Empty).Trim().Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

  /// <summary>
  /// The key a case variant waits under until it is merged. Topic names
  /// cannot contain <c>|</c>, so it never equals a real key.
  /// </summary>
  public static string PendingMergeKey(string name, Guid id) => $"{NormalizeName(name)}|{id:N}";

  /// <summary>
  /// The reader's own words about the topic (at most
  /// <see cref="MaxDescriptionLength"/> characters); null when there are none.
  /// A topic with a description is kept even when no note carries it.
  /// </summary>
  public string? Description { get; set; }

  public const int MaxDescriptionLength = 4000;

  /// <summary>A description as it is kept: trimmed, and null when nothing is left.</summary>
  public static string? NormalizeDescription(string? description) =>
    string.IsNullOrWhiteSpace(description) ? null : description.Trim();

  // NEW: Allow us to check if this topic is used anywhere
  public ICollection<NoteTopicModel> NoteTopics { get; set; } = [];
}

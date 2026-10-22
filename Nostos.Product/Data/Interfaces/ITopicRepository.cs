using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Interfaces;

public interface ITopicRepository
{
    /// <summary>
    /// Returns all topics with their usage count, ordered by usage desc then name asc.
    /// </summary>
    Task<List<TopicDto>> GetAllWithUsageCountAsync();

    /// <summary>
    /// Returns topics whose linked notes match the given term in Content, SelectedText, or Book Title.
    /// </summary>
    Task<List<TopicDto>> SearchByNoteTextAsync(string term);
    Task<List<TopicDto>> SearchByNoteTextAsync(
        string term,
        IReadOnlyList<Guid>? bookIds,
        int limit = 50);

    /// <summary>
    /// Returns aggregate topic and reference counts in a single database query.
    /// </summary>
    Task<TopicStatsDto> GetStatsAsync();

    /// <summary>
    /// Returns topics that share notes with the requested topic.
    /// </summary>
    Task<List<RelatedTopicDto>> GetRelatedAsync(Guid id);

    /// <summary>
    /// Returns the complete topic co-occurrence graph: all topics as nodes
    /// and one undirected edge for every pair that co-occur in at least one note.
    /// </summary>
    Task<TopicGraphDto> GetGraphAsync();

    /// <summary>
    /// The writings that cite the topic's notes (issue #1162), newest first and
    /// at most <paramref name="limit"/>, with the total and a per-note count.
    /// Joined at read time through the derived citation index, so renaming,
    /// merging or unlinking a topic needs no extra write. Null when the topic
    /// does not exist.
    /// </summary>
    Task<TopicWritingUsageDto?> GetWritingUsageAsync(Guid id, int limit, CancellationToken ct = default);

    /// <summary>
    /// Gets a topic with its linked notes (deep includes for Book and Note data).
    /// Returns null if not found.
    /// </summary>
    Task<TopicModel?> GetByIdWithNotesAsync(Guid id);

    /// <summary>
    /// Renames a topic, merging its links into an existing target name when necessary.
    /// Returns the surviving topic, or null when the source is not found.
    /// </summary>
    Task<TopicModel?> RenameAsync(Guid id, string name);

    /// <summary>
    /// Merges the source topic into the target topic and returns the survivor.
    /// Returns null when either topic is not found.
    /// </summary>
    Task<TopicModel?> MergeAsync(Guid sourceId, Guid targetId);

    /// <summary>
    /// Sets the topic's description, or clears it when empty. Returns the topic,
    /// or null when it is not found.
    /// </summary>
    Task<TopicModel?> SetDescriptionAsync(Guid id, string? description);

    /// <summary>
    /// Deletes a topic and its note links. Returns false when it is not found.
    /// </summary>
    Task<bool> DeleteAsync(Guid id);

    // --- Methods for NoteProcessorService ---

    /// <summary>
    /// Finds existing topics whose names match any in the given list, in any
    /// case (by <see cref="TopicModel.NormalizedName"/>).
    /// </summary>
    Task<List<TopicModel>> GetByNamesAsync(IEnumerable<string> names);

    /// <summary>
    /// Stages new topics for insertion (does NOT call SaveChanges).
    /// </summary>
    void AddRange(IEnumerable<TopicModel> topics);

    /// <summary>
    /// Removes all NoteTopicModel links for a given note, and returns the names
    /// of the linked topics its STORED text did not declare (link-only links:
    /// older data, imports), so the processor can keep them.
    /// Loads then removes so changes stay in the current unit-of-work.
    /// </summary>
    Task<IReadOnlyList<string>> ClearNoteLinksAsync(NoteModel note);

    /// <summary>
    /// Stages a single note–topic link for insertion (does NOT call SaveChanges).
    /// </summary>
    void AddNoteLink(NoteTopicModel link);

    // --- Method for TopicCleanupWorker ---

    /// <summary>
    /// Bulk-deletes all topics that have zero note links. Returns the count deleted.
    /// </summary>
    Task<int> DeleteOrphanedAsync(CancellationToken ct = default);

    /// <summary>
    /// Merges topics whose key is not their name's folded form (case variants
    /// from an older archive, or keys the migration could only fold for ASCII)
    /// into the topic of that name, and sets every key. Returns how many topics
    /// were merged away.
    /// </summary>
    Task<int> MergeCaseVariantsAsync(CancellationToken ct = default);
}

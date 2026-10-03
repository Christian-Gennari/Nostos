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
    /// Deletes a topic and its note links. Returns false when it is not found.
    /// </summary>
    Task<bool> DeleteAsync(Guid id);

    // --- Methods for NoteProcessorService ---

    /// <summary>
    /// Finds existing topics whose names match any in the given list (case-insensitive via DB collation).
    /// </summary>
    Task<List<TopicModel>> GetByNamesAsync(IEnumerable<string> names);

    /// <summary>
    /// Stages new topics for insertion (does NOT call SaveChanges).
    /// </summary>
    void AddRange(IEnumerable<TopicModel> topics);

    /// <summary>
    /// Removes all NoteTopicModel links for a given note.
    /// Loads then removes so changes stay in the current unit-of-work.
    /// </summary>
    Task ClearNoteLinksAsync(Guid noteId);

    /// <summary>
    /// Stages a single note–topic link for insertion (does NOT call SaveChanges).
    /// </summary>
    void AddNoteLink(NoteTopicModel link);

    // --- Method for TopicCleanupWorker ---

    /// <summary>
    /// Bulk-deletes all topics that have zero note links. Returns the count deleted.
    /// </summary>
    Task<int> DeleteOrphanedAsync(CancellationToken ct = default);
}

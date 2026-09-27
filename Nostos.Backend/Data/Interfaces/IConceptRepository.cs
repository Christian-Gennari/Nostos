using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Interfaces;

public interface IConceptRepository
{
    /// <summary>
    /// Returns all concepts with their usage count, ordered by usage desc then name asc.
    /// </summary>
    Task<List<ConceptDto>> GetAllWithUsageCountAsync();

    /// <summary>
    /// Returns concepts whose linked notes match the given term in Content, SelectedText, or Book Title.
    /// </summary>
    Task<List<ConceptDto>> SearchByNoteTextAsync(string term);
    Task<List<ConceptDto>> SearchByNoteTextAsync(
        string term,
        IReadOnlyList<Guid>? bookIds,
        int limit = 50);

    /// <summary>
    /// Returns aggregate concept and reference counts in a single database query.
    /// </summary>
    Task<ConceptStatsDto> GetStatsAsync();

    /// <summary>
    /// Returns concepts that share notes with the requested concept.
    /// </summary>
    Task<List<RelatedConceptDto>> GetRelatedAsync(Guid id);

    /// <summary>
    /// Returns the complete concept co-occurrence graph: all concepts as nodes
    /// and one undirected edge for every pair that co-occur in at least one note.
    /// </summary>
    Task<ConceptGraphDto> GetGraphAsync();

    /// <summary>
    /// Gets a concept with its linked notes (deep includes for Book and Note data).
    /// Returns null if not found.
    /// </summary>
    Task<ConceptModel?> GetByIdWithNotesAsync(Guid id);

    /// <summary>
    /// Renames a concept, merging its links into an existing target name when necessary.
    /// Returns the surviving concept, or null when the source is not found.
    /// </summary>
    Task<ConceptModel?> RenameAsync(Guid id, string name);

    /// <summary>
    /// Merges the source concept into the target concept and returns the survivor.
    /// Returns null when either concept is not found.
    /// </summary>
    Task<ConceptModel?> MergeAsync(Guid sourceId, Guid targetId);

    /// <summary>
    /// Deletes a concept and its note links. Returns false when it is not found.
    /// </summary>
    Task<bool> DeleteAsync(Guid id);

    // --- Methods for NoteProcessorService ---

    /// <summary>
    /// Finds existing concepts whose names match any in the given list (case-insensitive via DB collation).
    /// </summary>
    Task<List<ConceptModel>> GetByNamesAsync(IEnumerable<string> names);

    /// <summary>
    /// Stages new concepts for insertion (does NOT call SaveChanges).
    /// </summary>
    void AddRange(IEnumerable<ConceptModel> concepts);

    /// <summary>
    /// Removes all NoteConceptModel links for a given note.
    /// Loads then removes so changes stay in the current unit-of-work.
    /// </summary>
    Task ClearNoteLinksAsync(Guid noteId);

    /// <summary>
    /// Stages a single note–concept link for insertion (does NOT call SaveChanges).
    /// </summary>
    void AddNoteLink(NoteConceptModel link);

    // --- Method for ConceptCleanupWorker ---

    /// <summary>
    /// Bulk-deletes all concepts that have zero note links. Returns the count deleted.
    /// </summary>
    Task<int> DeleteOrphanedAsync(CancellationToken ct = default);
}

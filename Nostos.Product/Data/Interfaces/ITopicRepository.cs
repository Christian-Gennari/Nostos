using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Interfaces;

public interface ITopicRepository
{
    /// <summary>
    /// Returns all topics with their usage count, ordered by usage desc then name asc.
    /// </summary>
    Task<List<TopicDto>> GetAllWithUsageCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of topics, ordered by usage desc then name asc, optionally only
    /// topics with no kind or only one kind. The assistant's large-library
    /// classification reads the library in pages rather than all at once.
    /// </summary>
    async Task<TopicPageDto> GetPageWithUsageAsync(
        int page,
        int pageSize,
        bool unkindedOnly,
        Guid? kindId,
        CancellationToken cancellationToken = default)
    {
        // A host or test double that does not page still answers: the whole
        // library is one list, sliced here. The product repository overrides
        // this with a real query page.
        var all = await GetAllWithUsageCountAsync(cancellationToken);
        if (unkindedOnly)
            all = all.Where(topic => topic.KindId is null).ToList();
        else if (kindId is { } only)
            all = all.Where(topic => topic.KindId == only).ToList();

        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 250);
        var total = all.Count;
        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)safeSize);
        return new TopicPageDto(
            all.Skip((safePage - 1) * safeSize).Take(safeSize).ToList(),
            safePage,
            safeSize,
            total,
            totalPages,
            unkindedOnly ? total : all.Count(topic => topic.KindId is null),
            safePage < totalPages ? safePage + 1 : null);
    }

    /// <summary>
    /// The topics with these ids, with their usage count; ids that do not exist
    /// are absent. Used to classify a batch without reading the whole library.
    /// </summary>
    async Task<List<TopicDto>> GetByIdsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var wanted = ids.Distinct().ToHashSet();
        return (await GetAllWithUsageCountAsync(cancellationToken))
            .Where(topic => wanted.Contains(topic.Id))
            .ToList();
    }

    /// <summary>
    /// Returns topics whose linked notes match the given term in Content, SelectedText, or Book Title.
    /// </summary>
    Task<List<TopicDto>> SearchByNoteTextAsync(string term, CancellationToken cancellationToken = default);
    Task<List<TopicDto>> SearchByNoteTextAsync(
        string term,
        IReadOnlyList<Guid>? bookIds,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns aggregate topic and reference counts in a single database query.
    /// </summary>
    Task<TopicStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns topics that share notes with the requested topic.
    /// </summary>
    Task<List<RelatedTopicDto>> GetRelatedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the complete topic co-occurrence graph: all topics as nodes
    /// and one undirected edge for every pair that co-occur in at least one note.
    /// </summary>
    Task<TopicGraphDto> GetGraphAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The writings that cite the topic's notes (issue #1162), newest first and
    /// at most <paramref name="limit"/> (up to 500), with the total and, per
    /// cited note, how many and which writings cite it.
    /// Joined at read time through the derived citation index, so renaming,
    /// merging or unlinking a topic needs no extra write. Null when the topic
    /// does not exist.
    /// </summary>
    Task<TopicWritingUsageDto?> GetWritingUsageAsync(Guid id, int limit, CancellationToken ct = default);

    /// <summary>
    /// Gets a topic with its linked notes (deep includes for Book and Note data).
    /// Returns null if not found.
    /// </summary>
    Task<TopicModel?> GetByIdWithNotesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A topic by id, untracked. Null when it does not exist.</summary>
    Task<TopicModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a topic, merging its links into an existing target name when necessary.
    /// Returns the surviving topic, or null when the source is not found.
    /// </summary>
    Task<TopicModel?> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges the source topic into the target topic and returns the survivor.
    /// Returns null when either topic is not found.
    /// </summary>
    Task<TopicModel?> MergeAsync(Guid sourceId, Guid targetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the topic's description, or clears it when empty. Returns the topic,
    /// or null when it is not found.
    /// </summary>
    Task<TopicModel?> SetDescriptionAsync(
        Guid id,
        string? description,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a topic and its note links. Returns false when it is not found.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    // --- Methods for NoteProcessorService ---

    /// <summary>
    /// Finds existing topics whose names match any in the given list, in any
    /// case (by <see cref="TopicModel.NormalizedName"/>).
    /// </summary>
    Task<List<TopicModel>> GetByNamesAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default);

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
    Task<IReadOnlyList<string>> ClearNoteLinksAsync(
        NoteModel note,
        CancellationToken cancellationToken = default);

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

    // --- Topic kinds (issue #1767) ---

    Task<List<TopicKindDto>> GetAllKindsAsync(CancellationToken cancellationToken = default);

    Task<TopicKindMutationResult> CreateKindAsync(
        string name,
        string colorKey,
        CancellationToken cancellationToken = default);

    Task<TopicKindMutationResult> UpdateKindAsync(
        Guid id,
        string? name,
        string? colorKey,
        int? sortOrder,
        bool swapColor,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteKindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets one kind (or clears it) on many topics in one unit of work. Kept for
    /// the reader's bulk action, which may replace a kind the reader set.
    /// </summary>
    Task<SetTopicsKindOutcome> SetTopicsKindAsync(
        IReadOnlyList<Guid> topicIds,
        Guid? kindId,
        string source = TopicKindSources.Reader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one kind per topic in a single transaction, for a client that
    /// classifies many topics at once. With <paramref name="overwriteReaderKinds"/>
    /// false, a topic whose kind the reader set (<see cref="TopicKindSources.Reader"/>)
    /// is reported as skipped and left alone. With
    /// <paramref name="ignoreUnknownTopicIds"/> true, unknown ids are reported in
    /// the result and the known ones are still applied; otherwise one unknown id
    /// refuses the whole request, as the reader's endpoint expects.
    /// </summary>
    Task<SetTopicsKindOutcome> SetTopicKindsAsync(
        IReadOnlyList<TopicKindAssignment> assignments,
        string source = TopicKindSources.Reader,
        bool overwriteReaderKinds = true,
        bool ignoreUnknownTopicIds = false,
        CancellationToken cancellationToken = default);
}

/// <summary>One topic's new kind in a batch; a null kind clears it.</summary>
public sealed record TopicKindAssignment(Guid TopicId, Guid? KindId);

public enum TopicKindMutationStatus
{
    Success,
    NotFound,
    InvalidInput,
    Conflict,
    LimitReached,
}

public sealed record TopicKindMutationResult(
    TopicKindMutationStatus Status,
    TopicKindDto? Kind = null,
    string? ErrorMessage = null);

public enum SetTopicsKindStatus
{
    Success,
    InvalidInput,
    KindNotFound,
    TopicNotFound,
}

public sealed record SetTopicsKindOutcome(
    SetTopicsKindStatus Status,
    SetTopicsKindResultDto? Result = null,
    string? ErrorMessage = null);

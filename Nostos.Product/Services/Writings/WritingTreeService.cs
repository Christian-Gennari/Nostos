using Nostos.Backend.Data.Interfaces;

namespace Nostos.Backend.Services.Writings;

/// <summary>What a proposed move would do to the writing folder tree.</summary>
public enum WritingMoveOutcome
{
    /// <summary>The destination is the root, or a folder the writing is not inside.</summary>
    Allowed,

    /// <summary>The writing was asked to become its own parent.</summary>
    IntoItself,

    /// <summary>The destination is inside the writing being moved.</summary>
    IntoDescendant,

    /// <summary>The stored folder chain from the destination already loops.</summary>
    CircularHierarchy,

    /// <summary>The destination is not a writing in this library.</summary>
    UnknownParent,
}

/// <summary>
/// Refuses moves that would create a folder cycle (issue #1715).
///
/// The tree is read as one parent map — a single query for every writing's
/// <see cref="Nostos.Backend.Data.Models.WritingModel.Id"/> and
/// <c>ParentId</c> — and the ancestor chain is walked in memory, so the number
/// of database round trips no longer grows with the folder depth. A recursive
/// CTE would also be one query, but its syntax differs between SQLite and
/// PostgreSQL (Nostos-Cloud runs the latter); the map is portable and a
/// library's writings are small (two GUID columns per row).
///
/// The outcome is decided here and turned into the endpoint's existing bare
/// string bodies by <c>WritingsEndpoints</c>, so the wire format is untouched.
/// </summary>
public sealed class WritingTreeService
{
    private readonly IWritingRepository _repository;

    public WritingTreeService(IWritingRepository repository) => _repository = repository;

    /// <summary>
    /// Says whether <paramref name="writingId"/> may be moved under
    /// <paramref name="newParentId"/> (null = the root). Moving to the root or
    /// into itself needs no read at all; anything else reads the parent map once.
    /// </summary>
    public async Task<WritingMoveOutcome> CheckMoveAsync(
        Guid writingId,
        Guid? newParentId,
        CancellationToken cancellationToken = default)
    {
        if (newParentId is null)
            return WritingMoveOutcome.Allowed;

        if (newParentId.Value == writingId)
            return WritingMoveOutcome.IntoItself;

        var parents = await _repository.GetParentMapAsync(cancellationToken);

        if (!parents.ContainsKey(newParentId.Value))
            return WritingMoveOutcome.UnknownParent;

        var visited = new HashSet<Guid>();
        var ancestorId = newParentId.Value;
        while (true)
        {
            // A loop already stored between other folders is refused rather
            // than followed forever.
            if (!visited.Add(ancestorId))
                return WritingMoveOutcome.CircularHierarchy;

            if (ancestorId == writingId)
                return WritingMoveOutcome.IntoDescendant;

            if (!parents.TryGetValue(ancestorId, out var parentId) || parentId is null)
                return WritingMoveOutcome.Allowed;

            ancestorId = parentId.Value;
        }
    }
}

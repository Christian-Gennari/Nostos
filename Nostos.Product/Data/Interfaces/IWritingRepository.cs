using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Interfaces;

public enum AddKeptNoteStatus
{
    Success,
    AlreadyExists,
    WritingNotFound,
    WritingIsFolder,
    NoteNotFound,
}

public sealed record AddKeptNoteResult(AddKeptNoteStatus Status, WritingNoteModel? WritingNote);

public interface IWritingRepository
{
    /// <summary>The flat file list for the tree, without any document's Content.</summary>
    Task<List<WritingDto>> GetListAsync(CancellationToken cancellationToken = default);
    Task<WritingModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(WritingModel writing, CancellationToken cancellationToken = default);
    Task UpdateAsync(WritingModel writing, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a writing's name and content and rebuilds the notes it cites from
    /// that content (issue #1162), in one transaction.
    /// </summary>
    Task SaveContentAsync(WritingModel writing, CancellationToken ct = default);
    Task DeleteAsync(WritingModel writing, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every writing's Id and ParentId in one read, for the folder-cycle check
    /// (issue #1715). Keyed by Id; a null value means the root level.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid?>> GetParentMapAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the kept source notes for a writing document, or null if the writing does not exist.
    /// Ordered by AddedAt ascending, then NoteId.
    /// </summary>
    Task<List<WritingNoteModel>?> GetKeptNotesAsync(
        Guid writingId,
        CancellationToken cancellationToken = default);

    Task<AddKeptNoteResult> AddKeptNoteAsync(
        Guid writingId,
        Guid noteId,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveKeptNoteAsync(
        Guid writingId,
        Guid noteId,
        CancellationToken cancellationToken = default);
}

using Nostos.Backend.Data.Models;

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
    Task<List<WritingModel>> GetAllAsync();
    Task<WritingModel?> GetByIdAsync(Guid id);
    Task AddAsync(WritingModel writing);
    Task UpdateAsync(WritingModel writing);
    Task DeleteAsync(WritingModel writing);

    /// <summary>
    /// Gets the ParentId of a writing item for ancestor-chain cycle detection.
    /// </summary>
    Task<Guid?> GetParentIdAsync(Guid id);

    /// <summary>
    /// Returns the kept source notes for a writing document, or null if the writing does not exist.
    /// Ordered by AddedAt ascending, then NoteId.
    /// </summary>
    Task<List<WritingNoteModel>?> GetKeptNotesAsync(Guid writingId);

    Task<AddKeptNoteResult> AddKeptNoteAsync(Guid writingId, Guid noteId);

    Task<bool> RemoveKeptNoteAsync(Guid writingId, Guid noteId);
}

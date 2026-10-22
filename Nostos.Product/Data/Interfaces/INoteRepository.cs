using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Interfaces;

public interface INoteRepository
{
    Task<List<NoteModel>> GetByBookIdAsync(Guid bookId);
    Task<NoteModel?> GetByIdAsync(Guid id);

    /// <summary>
    /// Gets a note with its NoteTopics and Book eagerly loaded (for update/re-processing).
    /// </summary>
    Task<NoteModel?> GetByIdWithTopicsAsync(Guid id);

    /// <summary>
    /// Gets a note with its Book eagerly loaded (for building the response DTO after create).
    /// </summary>
    Task<NoteModel?> GetByIdWithBookAsync(Guid id);

    Task AddAsync(NoteModel note);
    Task SaveChangesAsync();
    Task DeleteAsync(NoteModel note);

    /// <summary>
    /// Bulk-deletes all NoteTopicModel links for a given note.
    /// </summary>
    Task DeleteTopicLinksAsync(Guid noteId);
    /// <summary>
    /// Notes whose text, quote or book title matches `query` (issue #158). Case is
    /// ignored; the caller caps the row count.
    /// </summary>
    Task<List<NoteModel>> SearchByTextAsync(
        string query,
        int limit,
        IReadOnlyCollection<Guid>? bookIds = null);

    /// <summary>
    /// Notes in which every term appears in at least one searchable field
    /// (text, quotation, passage title, book title or author), case-insensitively
    /// on every provider. Newest first; the caller caps the row count.
    /// </summary>
    Task<List<NoteModel>> SearchByAllTermsAsync(
        IReadOnlyList<string> terms,
        int limit,
        IReadOnlyCollection<Guid>? bookIds = null);

    /// <summary>
    /// The stored exactly-once receipt for a capture key pair, or null when the
    /// pair has not been seen. Read before a capture so a replay can return the
    /// stored result without touching the notes table (issue #260 §3).
    /// </summary>
    Task<NoteCommandReceipt?> GetReceiptAsync(string clientId, string idempotencyKey);

    /// <summary>
    /// Stages a receipt for the next <see cref="SaveChangesAsync"/>. Never saved
    /// on its own: the caller writes it inside the capture transaction so the
    /// note and its receipt commit together, or not at all.
    /// </summary>
    Task AddReceiptAsync(NoteCommandReceipt receipt);

    /// <summary>
    /// True when a capture under <paramref name="clientId"/> produced the note:
    /// how an Ask Nostos conversation proves a note is its own.
    /// </summary>
    Task<bool> HasReceiptForNoteAsync(string clientId, Guid noteId);

    /// <summary>
    /// Notes linked to no topic at all. These are unreachable through the index's
    /// topic rows, which is the gap #158 was filed about.
    /// </summary>
    Task<List<NoteModel>> GetWithoutTopicsAsync(int limit, int offset);

    /// <summary>A stable, bounded page across all notes, including notes without topics.</summary>
    Task<(List<NoteModel> Items, int Total)> BrowseAsync(NoteBrowseQuery query, int limit, int offset);

    /// <summary>
    /// The books that have notes matching <paramref name="query"/> (its book is
    /// ignored), with how many each: the Brain's Book filter for one place.
    /// </summary>
    Task<IReadOnlyList<NoteBookFacetDto>> GetBookFacetsAsync(NoteBrowseQuery query);

    /// <summary>How many notes, passages and notes without a topic there are.</summary>
    Task<NoteSummaryDto> GetSummaryAsync();

    /// <summary>
    /// How many notes are linked to no topic right now. The unlinked-note review
    /// queue traverses the whole set, so a page on its own would silently imply
    /// that the page IS the set (issue #256).
    /// </summary>
    Task<int> CountWithoutTopicsAsync();

    /// <summary>Total canonical notes visible in the current persistence scope.</summary>
    Task<int> CountAsync();

    /// <summary>
    /// Bounded structural note counts by book for whole-knowledge overview.
    /// </summary>
    Task<IReadOnlyList<NoteBookCount>> GetBookCountsAsync(int limit);

}

public sealed record NoteBookCount(Guid BookId, string BookTitle, int NoteCount);

/// <summary>
/// What a list of notes in the Brain shows: one place (everything, unsorted,
/// a kind, a topic, or what two topics share), narrowed by book and text.
/// </summary>
public sealed record NoteBrowseQuery(
    string? Text = null,
    Guid? BookId = null,
    bool WithoutTopics = false,
    string? Kind = null,
    Guid? TopicId = null,
    Guid? WithTopicId = null,
    NoteBrowseOrder Order = NoteBrowseOrder.Newest);

public enum NoteBrowseOrder
{
    Newest,
    Oldest,
    /// <summary>By book title, then in the order the notes were made.</summary>
    Book,
}


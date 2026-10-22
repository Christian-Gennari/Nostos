using Nostos.Backend.Data.Interfaces;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Canonical note domain service. The REST endpoints call this one service;
/// note creation/update/delete semantics (including <c>[[WikiLink]]</c> topic
/// processing) live here, never in a handler, mirroring <c>ILibraryService</c>
/// for the library.
/// </summary>
public interface INoteService
{
    Task<IReadOnlyList<NoteDto>> GetByBookAsync(Guid bookId, CancellationToken ct = default);
    Task<NoteSearchHitDto?> GetAsync(Guid noteId, CancellationToken ct = default);
    /// <summary>
    /// The listed notes that exist, with their topics (issue #1162: the Studio
    /// shows the topics of the sources a writing cites). Ids naming no note are
    /// left out, never an error.
    /// </summary>
    Task<IReadOnlyList<NoteSearchHitDto>> GetManyAsync(IReadOnlyCollection<Guid> noteIds, CancellationToken ct = default);
    /// <summary>A passage in full for the passage viewer; null when the note is not one.</summary>
    Task<PassageDto?> GetPassageAsync(Guid noteId, CancellationToken ct = default);
    Task<NoteSearchPageDto> GetUnlinkedAsync(int limit, int offset, CancellationToken ct = default);
    /// <summary>
    /// One page of a place in the Brain. An unknown <see cref="NoteBrowseQuery.Kind"/>
    /// matches nothing rather than everything.
    /// </summary>
    Task<NoteSearchPageDto> BrowseAsync(
        NoteBrowseQuery query, int limit, int offset, CancellationToken ct = default);
    Task<IReadOnlyList<NoteBookFacetDto>> GetBookFacetsAsync(NoteBrowseQuery query, CancellationToken ct = default);
    Task<NoteSummaryDto> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a <c>[[Topic]]</c> reference for each named topic to each listed note,
    /// creating topics that do not exist yet, in one unit of work. Notes that
    /// already declare a topic are left as they are.
    /// </summary>
    Task<NoteCommandResult<TopicLinkResultDto>> LinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default);

    /// <summary>
    /// Removes each named topic's references from each listed note
    /// (<see cref="TopicReferences.Remove"/>), in one unit of work.
    /// </summary>
    Task<NoteCommandResult<TopicLinkResultDto>> UnlinkTopicsAsync(
        IReadOnlyList<string> topics, IReadOnlyList<Guid> noteIds, CancellationToken ct = default);
    Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(string query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        IReadOnlyList<Guid>? bookIds,
        CancellationToken ct = default);

    Task<NoteCommandResult<NoteDto>> CreateAsync(
        Guid bookId, CreateNoteDto dto, string? clientId = null, string? idempotencyKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Canonical in-process capture entry point (issue #260 §3). Shares the
    /// idempotency path with <see cref="CreateAsync"/> — one implementation,
    /// not two — so the assistant tool bridge and the REST endpoint can never
    /// diverge on what a repeated capture means.
    /// </summary>
    Task<NoteCommandResult<NoteDto>> CaptureAsync(CaptureNoteRequest request, CancellationToken ct = default);

    Task<NoteCommandResult<NoteDto>> UpdateAsync(Guid noteId, UpdateNoteDto dto, CancellationToken ct = default);
    Task<NoteCommandResult<bool>> DeleteAsync(Guid noteId, CancellationToken ct = default);

    /// <summary>
    /// Records where the reader found an imported highlight in the book, so it
    /// is drawn and opened like one made in Nostos. An import carries the
    /// e-reader's position, which this reader cannot open; the first location
    /// found is kept and a later call returns the note unchanged.
    /// </summary>
    Task<NoteCommandResult<NoteDto>> SetImportedLocationAsync(Guid noteId, string? cfiRange, CancellationToken ct = default);

    Task<NoteCommandResult<NoteDto>> LinkToExistingTopicAsync(Guid noteId, Guid topicId, CancellationToken ct = default);
    Task<NoteReviewDto?> GetForReviewAsync(Guid noteId, CancellationToken ct = default);

    /// <summary>
    /// Re-derives the note's text in <paramref name="processingMode"/> from the
    /// ORIGINAL raw transcript — never from already-processed prose — and returns
    /// the updated note (issue #262 §7, §8). The raw transcript is preserved.
    /// </summary>
    Task<NoteCommandResult<NoteDto>> ReprocessAsync(
        Guid noteId, string processingMode, CancellationToken ct = default);

    /// <summary>
    /// Ask Nostos's "clean that up": clarifies a note that a capture under
    /// <paramref name="clientId"/> created, from its original words. Any other
    /// note is refused, so a conversation can only tidy what it saved.
    /// </summary>
    Task<NoteCommandResult<NoteDto>> RewriteCaptureAsync(
        Guid noteId, string clientId, CancellationToken ct = default);

    /// <summary>
    /// The raw transcript of one note and the mode its text currently reflects
    /// (issue #262 §8), so the original capture stays readable after any mode
    /// processed it.
    /// </summary>
    Task<NoteCommandResult<NoteRawTranscriptDto>> GetRawAsync(
        Guid noteId, CancellationToken ct = default);

    /// <summary>
    /// Restores the note's text from its raw transcript and records the mode as
    /// <c>verbatim</c> (issue #262 §8). Never invents a transcript: a note that
    /// kept none is a typed refusal.
    /// </summary>
    Task<NoteCommandResult<NoteDto>> RestoreRawAsync(
        Guid noteId, CancellationToken ct = default);
}

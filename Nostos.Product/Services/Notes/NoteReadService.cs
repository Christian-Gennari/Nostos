using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Every read of the note store (issue #1619): one note, a page of notes, the
/// Brain's browse/facets/summary, and the note-review read. Reads go through
/// <see cref="INoteRepository"/>; the projection lives in
/// <see cref="NoteProjector"/>, so a read never hand-builds a DTO.
/// </summary>
public sealed class NoteReadService
{
    /// <summary>The most notes one <see cref="GetManyAsync"/> call answers for.</summary>
    public const int MaxNotesByIds = 200;

    private readonly INoteRepository _notes;
    private readonly NoteProjector _projector;
    private readonly NoteEditionCounter _editions;

    public NoteReadService(
        INoteRepository notes,
        NoteProjector projector,
        NoteEditionCounter editions)
    {
        _notes = notes;
        _projector = projector;
        _editions = editions;
    }

    public async Task<IReadOnlyList<NoteDto>> GetByBookAsync(Guid bookId, CancellationToken ct = default)
    {
        var notes = await _notes.GetByBookIdAsync(bookId, ct);
        return notes.Select(n => n.ToDto()).ToList();
    }

    public async Task<NoteSearchHitDto?> GetAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await _notes.GetForReadingAsync(noteId, ct);

        return note is null ? null : _projector.ToSearchHit(note, null, await _editions.CountsAsync([note], ct));
    }

    public async Task<IReadOnlyList<NoteSearchHitDto>> GetManyAsync(
        IReadOnlyCollection<Guid> noteIds, CancellationToken ct = default)
    {
        var ids = noteIds.Distinct().Take(MaxNotesByIds).ToList();
        if (ids.Count == 0)
            return [];

        var notes = await _notes.GetManyForReadingAsync(ids, ct);

        var order = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var editions = await _editions.CountsAsync(notes, ct);
        return notes.OrderBy(n => order[n.Id]).Select(n => _projector.ToSearchHit(n, null, editions)).ToList();
    }

    public async Task<NoteSearchPageDto> GetUnlinkedAsync(int limit, int offset, CancellationToken ct = default)
    {
        var take = NoteBrowseLimits.Clamp(limit);
        var skip = Math.Max(offset, 0);
        var total = await _notes.CountWithoutTopicsAsync(ct);
        var notes = await _notes.GetWithoutTopicsAsync(take, skip, ct);
        var editions = await _editions.CountsAsync(notes, ct);
        return new NoteSearchPageDto(
            notes.Select(n => _projector.ToSearchHit(n, null, editions)).ToList(), total, skip, take);
    }

    public async Task<NoteSearchPageDto> BrowseAsync(
        NoteBrowseQuery query, int limit, int offset, CancellationToken ct = default)
    {
        var take = NoteBrowseLimits.Clamp(limit);
        var skip = Math.Max(offset, 0);
        var (items, total) = await _notes.BrowseAsync(NoteBrowseLimits.Normalized(query), take, skip, ct);
        var editions = await _editions.CountsAsync(items, ct);
        return new NoteSearchPageDto(
            items.Select(n => _projector.ToSearchHit(n, query.Text?.Trim(), editions)).ToList(), total, skip, take);
    }

    public Task<IReadOnlyList<NoteBookFacetDto>> GetBookFacetsAsync(
        NoteBrowseQuery query, CancellationToken ct = default) =>
        _notes.GetBookFacetsAsync(NoteBrowseLimits.Normalized(query), ct);

    public Task<NoteSummaryDto> GetSummaryAsync(CancellationToken ct = default) =>
        _notes.GetSummaryAsync(ct);

    public async Task<PassageDto?> GetPassageAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await _notes.GetByIdWithBookAsync(noteId, ct);

        return note is null || !note.IsPassage() ? null : note.ToPassageDto();
    }

    public async Task<NoteReviewDto?> GetForReviewAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await _notes.GetForReadingAsync(noteId, ct);

        return note is null ? null : _projector.ToReview(note);
    }

    /// <summary>
    /// A saved note as its reply, read back to get its book's title and author:
    /// a note tracked for an edit does not carry its book.
    /// </summary>
    public async Task<NoteDto> SavedNoteAsync(Guid noteId, CancellationToken ct = default) =>
        (await _notes.GetByIdWithBookAsync(noteId, ct))!.ToDto();
}

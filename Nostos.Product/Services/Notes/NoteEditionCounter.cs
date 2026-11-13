using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// How many editions each listed note's book has, in one query for a whole page
/// (never one per note), keyed by work (issue #1619, extracted from
/// <see cref="NoteService"/>).
/// </summary>
public sealed class NoteEditionCounter
{
    private readonly IBookRepository _books;

    public NoteEditionCounter(IBookRepository books)
    {
        _books = books;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountsAsync(
        IEnumerable<NoteModel> notes, CancellationToken ct = default)
    {
        var workIds = notes.Where(n => n.Book is not null).Select(n => n.Book!.WorkId).Distinct().ToList();
        if (workIds.Count == 0) return new Dictionary<Guid, int>();
        return await _books.CountEditionsByWorkAsync(workIds, ct);
    }
}

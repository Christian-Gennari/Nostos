namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>An imported highlight, as far as choosing a starter draft's passages needs it.</summary>
public sealed record StarterPassageCandidate(
    Guid NoteId,
    Guid BookId,
    string? SelectedText,
    string? Note,
    DateTime CreatedAt);

/// <summary>
/// Chooses the passages a starter draft opens with (issue #1220): the reader's
/// own highlights, so the first session in the Studio shows a passage arriving
/// with its source. Pure and deterministic, so the same import always offers
/// the same draft.
/// </summary>
public static class StarterPassageSelector
{
    /// <summary>How many passages a starter draft holds.</summary>
    public const int Count = 3;

    /// <summary>Fewer words than this is a fragment, not a passage worth writing from.</summary>
    public const int MinimumWords = 6;

    /// <summary>Past this a quotation takes over the page it is meant to start.</summary>
    public const int ComfortableWords = 120;

    /// <summary>
    /// The best <paramref name="count"/> passages, spread across books when
    /// there are several: highlights the reader wrote a note on come first,
    /// then ones long enough to say something, comfortable lengths before very
    /// long ones. Each book gives its best passage before any book gives a
    /// second. A highlight without text cannot be quoted and is never chosen.
    /// </summary>
    public static IReadOnlyList<StarterPassageCandidate> Pick(
        IEnumerable<StarterPassageCandidate> candidates,
        int count = Count)
    {
        var ranked = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.SelectedText))
            .GroupBy(candidate => candidate.NoteId)
            .Select(group => group.First())
            .OrderBy(Rank)
            .ThenBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.NoteId)
            .ToList();

        var picked = new List<StarterPassageCandidate>(count);
        // One round per depth: every book's best, then every book's second best.
        var byBook = ranked
            .GroupBy(candidate => candidate.BookId)
            .Select(group => group.ToList())
            .ToList();
        for (var depth = 0; picked.Count < count; depth++)
        {
            var round = byBook
                .Where(book => book.Count > depth)
                .Select(book => book[depth])
                .OrderBy(Rank)
                .ThenBy(candidate => candidate.CreatedAt)
                .ThenBy(candidate => candidate.NoteId)
                .ToList();
            if (round.Count == 0)
                break;
            picked.AddRange(round.Take(count - picked.Count));
        }

        return picked;
    }

    /// <summary>Lower is better: a note and enough words, then enough words, then anything.</summary>
    private static int Rank(StarterPassageCandidate candidate)
    {
        var words = WordCount(candidate.SelectedText);
        var hasNote = !string.IsNullOrWhiteSpace(candidate.Note);
        var substantial = words >= MinimumWords;
        var comfortable = words <= ComfortableWords;
        return (substantial, hasNote, comfortable) switch
        {
            (true, true, true) => 0,
            (true, false, true) => 1,
            (true, true, false) => 2,
            (true, false, false) => 3,
            (false, true, _) => 4,
            _ => 5,
        };
    }

    public static int WordCount(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Product.BookText;

namespace Nostos.Backend.Tests.Knowledge.Quality;

/// <summary>
/// Faithful reproduction of the pre-#562 ("legacy literal") single-query
/// behaviour, established from git history rather than recollection:
/// <list type="bullet">
/// <item>Notes: <c>NoteService.SearchAsync</c> at 49f86a9^ was a single
/// <c>SearchByTextAsync(query, Clamp(limit))</c> whole-phrase LIKE over
/// Content/SelectedText/Book.Title — no planner, no fusion, no book scope.
/// The repository predicate itself is unchanged by #562, so legacy notes are
/// reproduced by calling <c>INoteRepository.SearchByTextAsync</c> with the raw
/// query directly.</item>
/// <item>Concepts: <c>ConceptRepository.SearchByNoteTextAsync</c> at 49f86a9^
/// was a single unescaped <c>LIKE %term%</c> over linked-note Content/
/// SelectedText/Book.Title only (concept names were NOT matched), ordered by
/// NoteMatchCount desc then Name, Take(50). The current repository always
/// expands through <c>LexicalQueryPlanner</c>, so the legacy concept ranking
/// is replicated inline below with the exact old predicate.</item>
/// <item>Book text: #562 (commit 49f86a9) contains no change to
/// <c>BookTextSearchService</c> or the FTS index query — both eras pass the
/// raw query to <c>IBookTextIndex.SearchAsync</c>, which ORs quoted tokens.
/// The book-text path is therefore byte-identical across eras; the legacy
/// leg reuses the current service call and is labelled as such.</item>
/// </list>
/// </summary>
internal static class LegacyLiteralRetrieval
{
    public sealed record LegacyResult(
        IReadOnlyList<Guid> NoteIds,
        IReadOnlyList<Guid> ConceptIds,
        IReadOnlyList<(Guid BookId, int Ordinal)> Passages);

    public static async Task<LegacyResult> SearchAsync(
        RetrievalQualityHarness h,
        string query,
        int maxPerSource,
        CancellationToken ct = default)
    {
        var noteModels = await h.NoteRepository.SearchByTextAsync(
            query, maxPerSource, bookIds: null);
        var conceptIds = await SearchConceptsSinglePhraseAsync(h, query);
        var passages = await h.BookTextSearch.SearchAsync(
            new BookTextSearchRequest(query, MaxPassages: maxPerSource), ct);

        return new LegacyResult(
            noteModels.Select(note => note.Id).ToList(),
            conceptIds,
            passages.Passages
                .Select(passage => (passage.BookId, passage.Ordinal))
                .ToList());
    }

    /// <summary>
    /// Exact replica of the pre-#562 concept search predicate
    /// (49f86a9^:Nostos.Backend/Data/Repositories/ConceptRepository.cs):
    /// one unescaped LIKE over the linked note's text columns, no planner
    /// variants, no concept-name matching, NoteMatchCount desc then Name.
    /// The fixture queries contain no LIKE wildcards, so the missing escape
    /// is behaviourally irrelevant here; it is preserved for fidelity.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SearchConceptsSinglePhraseAsync(
        RetrievalQualityHarness h,
        string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var cleanTerm = term.Trim();
        var pattern = $"%{cleanTerm}%";

        var matchingRows = await h.Db.NoteConcepts
            .AsNoTracking()
            .Where(nc =>
                EF.Functions.Like(nc.Note.Content, pattern) ||
                (nc.Note.SelectedText != null && EF.Functions.Like(nc.Note.SelectedText, pattern)) ||
                (nc.Note.Book != null && EF.Functions.Like(nc.Note.Book.Title, pattern)))
            .Select(nc => new
            {
                nc.ConceptId,
                ConceptName = nc.Concept.Concept,
                TotalUsageCount = nc.Concept.NoteConcepts.Count(),
                nc.NoteId,
            })
            .ToListAsync();

        return matchingRows
            .GroupBy(row => row.ConceptId)
            .Select(group => new
            {
                ConceptId = group.Key,
                NoteMatchCount = group.Select(row => row.NoteId).Distinct().Count(),
                Name = group.First().ConceptName,
            })
            .OrderByDescending(item => item.NoteMatchCount)
            .ThenBy(item => item.Name)
            .Take(50)
            .Select(item => item.ConceptId)
            .ToList();
    }
}

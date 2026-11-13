using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Search;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Lexical note search (issue #1619): the query variants a person's words fan
/// out to, their rank fusion, and the hit projection. One implementation for the
/// search box and the assistant's retrieval, so the two can never disagree on
/// what a phrase finds or which hit comes first.
/// </summary>
public sealed class NoteSearchService
{
    // Above the planner's phrase weight (4): every word present beats the exact
    // phrase being absent, and the phrase is rare in a short "@" query anyway.
    private const int AllTermsWeight = 5;

    private readonly INoteRepository _notes;
    private readonly NoteProjector _projector;
    private readonly NoteSnippetFormatter _snippets;
    private readonly NoteEditionCounter _editions;

    public NoteSearchService(
        INoteRepository notes,
        NoteProjector projector,
        NoteSnippetFormatter snippets,
        NoteEditionCounter editions)
    {
        _notes = notes;
        _projector = projector;
        _snippets = snippets;
        _editions = editions;
    }

    public Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        CancellationToken ct = default) =>
        SearchCoreAsync(query, limit, bookIds: null, forgiving: true, ct);

    public Task<IReadOnlyList<NoteSearchHitDto>> SearchAsync(
        string query,
        int limit,
        IReadOnlyList<Guid>? bookIds,
        CancellationToken ct = default) =>
        SearchCoreAsync(query, limit, bookIds, forgiving: false, ct);

    /// <summary>
    /// <paramref name="forgiving"/> is for what a person types into a search box:
    /// when no note holds the words as typed, accents are ignored and a small
    /// typo is allowed (issue #1380). Retrieval for the assistant stays exact.
    /// </summary>
    private async Task<IReadOnlyList<NoteSearchHitDto>> SearchCoreAsync(
        string query,
        int limit,
        IReadOnlyList<Guid>? bookIds,
        bool forgiving,
        CancellationToken ct)
    {
        var take = NoteBrowseLimits.Clamp(limit);
        var variants = LexicalQueryPlanner.Build(query);
        if (variants.Count == 0)
            return [];

        // Keep enough candidates per variant for deterministic fusion without
        // letting one broad token expand into an unbounded note scan.
        var candidateLimit = Math.Clamp(take * 3, 20, 60);
        var fused = new Dictionary<Guid, NoteFusionCandidate>();

        // Several words: a note holding every one of them, across its text and
        // its book's title and author, outranks one that shares only a single
        // word (issue #1038). "schopenhauer will" names an author and a subject.
        var tokens = variants
            .Where(variant => variant.Kind == LexicalQueryVariantKind.Token)
            .Select(variant => variant.Text)
            .ToList();
        var searches = variants
            .Select(variant => (variant.Text, variant.Weight, Terms: (IReadOnlyList<string>)[variant.Text]))
            .ToList();
        if (tokens.Count >= 2)
            searches.Insert(0, (string.Join(' ', tokens), AllTermsWeight, tokens));

        var matchedAllTerms = new HashSet<Guid>();
        foreach (var variant in searches)
        {
            var hits = await _notes.SearchByAllTermsAsync(
                variant.Terms, candidateLimit, bookIds, cancellationToken: ct);
            if (variant.Weight == AllTermsWeight)
                matchedAllTerms.UnionWith(hits.Select(note => note.Id));
            for (var rank = 0; rank < hits.Count; rank++)
            {
                var note = hits[rank];
                var score = (variant.Weight * 100) + Math.Max(0, candidateLimit - rank);

                if (!fused.TryGetValue(note.Id, out var current))
                {
                    fused[note.Id] = new NoteFusionCandidate(
                        note,
                        score,
                        variant.Text,
                        MatchedVariants: 1);
                    continue;
                }

                fused[note.Id] = current with
                {
                    Score = current.Score + score,
                    MatchedVariants = current.MatchedVariants + 1,
                    // Prefer the more specific variant for the snippet.
                    SnippetTerm = variant.Weight > SpecificityWeight(current.SnippetTerm, variants)
                        ? variant.Text
                        : current.SnippetTerm,
                };
            }
        }

        if (fused.Count == 0 && forgiving)
        {
            var forgiven = await _notes.SearchByAllTermsAsync(
                [query.Trim()], candidateLimit, bookIds, forgiving: true, ct);
            foreach (var note in forgiven)
                fused[note.Id] = new NoteFusionCandidate(note, 0, query.Trim(), MatchedVariants: 1);
        }

        // With several words: a note holding every word comes first, and among
        // those a word found in the note itself says more than one found only in
        // its book's title. "schopenhauer will" is about the will, not about
        // every note from "The World as Will and Representation".
        // Single-word queries keep their existing order exactly.
        var multiWord = tokens.Count >= 2;
        var ranked = fused.Values
            .OrderByDescending(candidate => multiWord && matchedAllTerms.Contains(candidate.Note.Id))
            .ThenByDescending(candidate => multiWord ? OwnTextMatches(candidate.Note, tokens) : 0)
            .ThenByDescending(candidate => candidate.MatchedVariants)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Note.CreatedAt)
            .ThenBy(candidate => candidate.Note.Id)
            .Take(take)
            .ToList();
        var editions = await _editions.CountsAsync(ranked.Select(candidate => candidate.Note), ct);
        return ranked
            .Select(candidate => _projector.ToSearchHit(
                candidate.Note, _snippets.TermFor(candidate.Note, candidate.SnippetTerm, tokens), editions))
            .ToList();
    }

    private static int OwnTextMatches(NoteModel note, IReadOnlyList<string> tokens) =>
        tokens.Count(token =>
            note.Content.Contains(token, StringComparison.OrdinalIgnoreCase)
            || (note.SelectedText?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false)
            || (note.PassageTitle?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false));

    private static int SpecificityWeight(
        string term,
        IReadOnlyList<LexicalQueryVariant> variants) =>
        variants.FirstOrDefault(
            variant => string.Equals(variant.Text, term, StringComparison.OrdinalIgnoreCase))?.Weight ?? 0;

    private sealed record NoteFusionCandidate(
        NoteModel Note,
        int Score,
        string SnippetTerm,
        int MatchedVariants);
}

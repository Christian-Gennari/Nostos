using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Search;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Repositories;

public class ConceptRepository : IConceptRepository
{
    private readonly NostosDbContext _db;

    public ConceptRepository(NostosDbContext db)
    {
        _db = db;
    }

    public async Task<List<ConceptDto>> GetAllWithUsageCountAsync()
    {
        return await _db
            .Concepts.OrderByDescending(c => c.NoteConcepts.Count())
            .ThenBy(c => c.Concept)
            .Select(c => new ConceptDto(c.Id, c.Concept, c.NoteConcepts.Count()))
            .ToListAsync();
    }

    public Task<List<ConceptDto>> SearchByNoteTextAsync(string term) =>
        SearchByNoteTextAsync(term, bookIds: null, limit: 50);

    public async Task<List<ConceptDto>> SearchByNoteTextAsync(
        string term,
        IReadOnlyList<Guid>? bookIds,
        int limit = 50)
    {
        var variants = LexicalQueryPlanner.Build(term);
        if (variants.Count == 0 || bookIds is { Count: 0 })
            return [];

        var take = Math.Clamp(limit, 1, 50);
        var fused = new Dictionary<Guid, ConceptFusionCandidate>();

        foreach (var variant in variants)
        {
            var partial = await SearchSingleVariantAsync(variant.Text, bookIds, take);
            for (var rank = 0; rank < partial.Count; rank++)
            {
                var concept = partial[rank];
                var score = (variant.Weight * 100)
                    + (concept.NoteMatchCount * 10)
                    + Math.Max(0, take - rank);

                if (!fused.TryGetValue(concept.Id, out var current))
                {
                    fused[concept.Id] = new ConceptFusionCandidate(
                        concept,
                        score,
                        variant.Text,
                        MatchedVariants: 1,
                        BestVariantWeight: variant.Weight);
                    continue;
                }

                fused[concept.Id] = current with
                {
                    Score = current.Score + score,
                    MatchedVariants = current.MatchedVariants + 1,
                    Best = concept.NoteMatchCount > current.Best.NoteMatchCount
                        ? concept
                        : current.Best,
                    SnippetTerm = variant.Weight > current.BestVariantWeight
                        ? variant.Text
                        : current.SnippetTerm,
                    BestVariantWeight = Math.Max(current.BestVariantWeight, variant.Weight),
                };
            }
        }

        return fused.Values
            .OrderByDescending(candidate => candidate.MatchedVariants)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Best.NoteMatchCount)
            .ThenBy(candidate => candidate.Best.Name)
            .Take(take)
            .Select(candidate => candidate.Best)
            .ToList();
    }

    private async Task<List<ConceptDto>> SearchSingleVariantAsync(
        string cleanTerm,
        IReadOnlyList<Guid>? bookIds,
        int limit)
    {
        var pattern = $"%{Escape(cleanTerm)}%";

        var rows = _db.NoteConcepts
            .AsNoTracking()
            .AsQueryable();

        if (bookIds is { Count: > 0 })
            rows = rows.Where(nc => bookIds.Contains(nc.Note.BookId));

        var matchingRows = await rows
            .Where(nc =>
                EF.Functions.Like(nc.Concept.Concept, pattern, "\\")
                || EF.Functions.Like(nc.Note.Content, pattern, "\\")
                || (nc.Note.SelectedText != null && EF.Functions.Like(nc.Note.SelectedText, pattern, "\\"))
                || (nc.Note.Book != null && EF.Functions.Like(nc.Note.Book.Title, pattern, "\\")))
            .Select(nc => new
            {
                nc.ConceptId,
                ConceptName = nc.Concept.Concept,
                TotalUsageCount = nc.Concept.NoteConcepts.Count(),
                nc.NoteId,
                nc.Note.Content,
                nc.Note.SelectedText,
                BookTitle = nc.Note.Book != null ? nc.Note.Book.Title : null,
                nc.Note.CreatedAt
            })
            .ToListAsync();

        if (matchingRows.Count == 0)
            return [];

        return matchingRows
            .GroupBy(row => new { row.ConceptId, row.ConceptName, row.TotalUsageCount })
            .Select(group =>
            {
                var noteGroups = group.GroupBy(row => row.NoteId).ToList();
                var snippet = noteGroups
                    .Select(noteGroup => noteGroup.OrderBy(row => row.CreatedAt).First())
                    .OrderBy(row => row.CreatedAt)
                    .Select(row =>
                        MatchSnippet(row.Content, cleanTerm)
                        ?? MatchSnippet(row.SelectedText, cleanTerm)
                        ?? MatchSnippet(row.BookTitle, cleanTerm))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

                return new ConceptDto(
                    group.Key.ConceptId,
                    group.Key.ConceptName,
                    group.Key.TotalUsageCount,
                    noteGroups.Count,
                    snippet);
            })
            .OrderByDescending(concept => concept.NoteMatchCount)
            .ThenBy(concept => concept.Name)
            .Take(limit)
            .ToList();
    }

    private static string? MatchSnippet(string? text, string term)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
            return null;
        return CreateSnippet(text, term);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed record ConceptFusionCandidate(
        ConceptDto Best,
        int Score,
        string SnippetTerm,
        int MatchedVariants,
        int BestVariantWeight = 0);

    private static string CreateSnippet(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return string.Empty;

        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        int matchIndex = cleaned.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0)
            return string.Empty;

        int matchLength = term.Length;
        int targetStart = Math.Max(0, matchIndex - 60);
        int targetEnd = Math.Min(cleaned.Length, matchIndex + matchLength + 60);

        int start = targetStart;
        bool truncatedStart = false;
        if (start > 0)
        {
            truncatedStart = true;
            if (char.IsWhiteSpace(cleaned[start]))
            {
                while (start < matchIndex && char.IsWhiteSpace(cleaned[start]))
                    start++;
            }
            else if (!char.IsWhiteSpace(cleaned[start - 1]))
            {
                int spaceIndex = cleaned.IndexOf(' ', start);
                if (spaceIndex >= 0 && spaceIndex < matchIndex)
                {
                    start = spaceIndex + 1;
                    while (start < matchIndex && char.IsWhiteSpace(cleaned[start]))
                        start++;
                }
            }
        }

        int end = targetEnd;
        bool truncatedEnd = false;
        if (end < cleaned.Length)
        {
            truncatedEnd = true;
            if (char.IsWhiteSpace(cleaned[end]))
            {
                // Word boundary already
            }
            else if (char.IsWhiteSpace(cleaned[end - 1]))
            {
                while (end > matchIndex + matchLength && char.IsWhiteSpace(cleaned[end - 1]))
                    end--;
            }
            else
            {
                int spaceIndex = cleaned.LastIndexOf(' ', end - 1);
                if (spaceIndex >= matchIndex + matchLength)
                {
                    end = spaceIndex;
                    while (end > matchIndex + matchLength && char.IsWhiteSpace(cleaned[end - 1]))
                        end--;
                }
            }
        }

        var fragment = cleaned.Substring(start, end - start).Trim();
        return $"{(truncatedStart ? "…" : "")}{fragment}{(truncatedEnd ? "…" : "")}";
    }

    public async Task<ConceptStatsDto> GetStatsAsync()
    {
        var stats = await _db
            .Concepts.Select(c => new
            {
                c.Concept,
                UsageCount = c.NoteConcepts.Count(),
            })
            .GroupBy(_ => 1)
            .Select(group => new ConceptStatsDto(
                group.Count(),
                group.Sum(c => c.UsageCount),
                group.Count(c => c.UsageCount == 1),
                group
                    .OrderByDescending(c => c.UsageCount)
                    .ThenBy(c => c.Concept)
                    .Select(c => c.Concept)
                    .FirstOrDefault(),
                group.Max(c => c.UsageCount)
            ))
            .SingleOrDefaultAsync();

        return stats ?? new ConceptStatsDto(0, 0, 0, null, 0);
    }

    public async Task<List<RelatedConceptDto>> GetRelatedAsync(Guid id)
    {
        var noteIds = await _db
            .NoteConcepts.Where(nc => nc.ConceptId == id)
            .Select(nc => nc.NoteId)
            .ToListAsync();

        if (noteIds.Count == 0)
            return [];

        // Keep the raw co-occurrence rows so the API can explain each structural
        // relationship with the exact shared notes rather than only a score.
        var relatedRows = await _db
            .NoteConcepts.AsNoTracking()
            .Where(nc => noteIds.Contains(nc.NoteId) && nc.ConceptId != id)
            .Select(nc => new
            {
                nc.ConceptId,
                Name = nc.Concept.Concept,
                nc.NoteId,
            })
            .ToListAsync();

        return relatedRows
            .GroupBy(row => new { row.ConceptId, row.Name })
            .Select(group =>
            {
                var sharedNoteIds = group
                    .Select(row => row.NoteId)
                    .Distinct()
                    .OrderBy(noteId => noteId)
                    .ToList();

                return new RelatedConceptDto(
                    group.Key.ConceptId,
                    group.Key.Name,
                    sharedNoteIds.Count,
                    sharedNoteIds
                );
            })
            .OrderByDescending(related => related.SharedNotes)
            .ThenBy(related => related.Name)
            .ToList();
    }

    public async Task<ConceptGraphDto> GetGraphAsync()
    {
        // All concepts as nodes, including isolates (0 note links).
        var nodes = await _db
            .Concepts.OrderByDescending(c => c.NoteConcepts.Count())
            .ThenBy(c => c.Concept)
            .Select(c => new ConceptGraphNodeDto(c.Id, c.Concept, c.NoteConcepts.Count()))
            .ToListAsync();

        // All note-concept pairs (note → concept id) in one query.
        var pairs = await _db.NoteConcepts
            .Select(nc => new { nc.NoteId, nc.ConceptId })
            .ToListAsync();

        // Group by note to find co-occurring pairs, then aggregate edge weights.
        var byNote = pairs.GroupBy(p => p.NoteId);
        var edgeMap = new Dictionary<(Guid, Guid), int>();

        foreach (var group in byNote)
        {
            var conceptIds = group.Select(p => p.ConceptId).Distinct().OrderBy(id => id).ToList();
            for (int i = 0; i < conceptIds.Count; i++)
            {
                for (int j = i + 1; j < conceptIds.Count; j++)
                {
                    var key = (conceptIds[i], conceptIds[j]);
                    edgeMap[key] = edgeMap.GetValueOrDefault(key) + 1;
                }
            }
        }

        var edges = edgeMap
            .Select(kv => new ConceptGraphEdgeDto(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .OrderByDescending(e => e.SharedNotes)
            .ThenBy(e => e.SourceId)
            .ThenBy(e => e.TargetId)
            .ToList();

        return new ConceptGraphDto(nodes, edges);
    }

    public async Task<ConceptModel?> GetByIdWithNotesAsync(Guid id)
    {
        return await _db
            .Concepts.Include(c => c.NoteConcepts)
            .ThenInclude(nc => nc.Note)
            .ThenInclude(n => n.Book)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<ConceptModel?> RenameAsync(Guid id, string name)
    {
        var source = await _db
            .Concepts.Include(c => c.NoteConcepts)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (source is null)
            return null;

        var target = await _db
            .Concepts.Include(c => c.NoteConcepts)
            .FirstOrDefaultAsync(c => c.Id != id && c.Concept == name);

        if (target is null)
        {
            source.Concept = name;
            await _db.SaveChangesAsync();
            return source;
        }

        MoveLinks(source, target);
        _db.Concepts.Remove(source);
        await _db.SaveChangesAsync();
        return target;
    }

    public async Task<ConceptModel?> MergeAsync(Guid sourceId, Guid targetId)
    {
        var source = await _db
            .Concepts.Include(c => c.NoteConcepts)
            .FirstOrDefaultAsync(c => c.Id == sourceId);
        var target = await _db
            .Concepts.Include(c => c.NoteConcepts)
            .FirstOrDefaultAsync(c => c.Id == targetId);

        if (source is null || target is null)
            return null;

        MoveLinks(source, target);
        _db.Concepts.Remove(source);
        await _db.SaveChangesAsync();
        return target;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var concept = await _db
            .Concepts.Include(c => c.NoteConcepts)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (concept is null)
            return false;

        _db.NoteConcepts.RemoveRange(concept.NoteConcepts);
        _db.Concepts.Remove(concept);
        await _db.SaveChangesAsync();
        return true;
    }

    private void MoveLinks(ConceptModel source, ConceptModel target)
    {
        var targetNoteIds = target.NoteConcepts.Select(link => link.NoteId).ToHashSet();

        foreach (var sourceLink in source.NoteConcepts.ToList())
        {
            _db.NoteConcepts.Remove(sourceLink);

            if (targetNoteIds.Contains(sourceLink.NoteId))
                continue;

            var targetLink = new NoteConceptModel
            {
                NoteId = sourceLink.NoteId,
                ConceptId = target.Id,
                Concept = target,
            };
            target.NoteConcepts.Add(targetLink);
            _db.NoteConcepts.Add(targetLink);
            targetNoteIds.Add(sourceLink.NoteId);
        }
    }

    // --- Methods for NoteProcessorService ---

    public async Task<List<ConceptModel>> GetByNamesAsync(IEnumerable<string> names)
    {
        var nameList = names.ToList();
        return await _db.Concepts.Where(c => nameList.Contains(c.Concept)).ToListAsync();
    }

    public void AddRange(IEnumerable<ConceptModel> concepts)
    {
        _db.Concepts.AddRange(concepts);
    }

    public async Task ClearNoteLinksAsync(Guid noteId)
    {
        var currentLinks = await _db.NoteConcepts.Where(nc => nc.NoteId == noteId).ToListAsync();

        if (currentLinks.Count != 0)
        {
            _db.NoteConcepts.RemoveRange(currentLinks);
        }
    }

    public void AddNoteLink(NoteConceptModel link)
    {
        _db.NoteConcepts.Add(link);
    }

    // --- Method for ConceptCleanupWorker ---

    public async Task<int> DeleteOrphanedAsync(CancellationToken ct = default)
    {
        return await _db.Concepts.Where(c => !c.NoteConcepts.Any()).ExecuteDeleteAsync(ct);
    }
}

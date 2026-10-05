using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Search;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Repositories;

public class TopicRepository : ITopicRepository
{
    private readonly NostosDbContext _db;

    public TopicRepository(NostosDbContext db)
    {
        _db = db;
    }

    public async Task<List<TopicDto>> GetAllWithUsageCountAsync()
    {
        return await _db
            .Topics.OrderByDescending(c => c.NoteTopics.Count())
            .ThenBy(c => c.Topic)
            .Select(c => new TopicDto(c.Id, c.Topic, c.NoteTopics.Count()))
            .ToListAsync();
    }

    public Task<List<TopicDto>> SearchByNoteTextAsync(string term) =>
        SearchByNoteTextAsync(term, bookIds: null, limit: 50);

    public async Task<List<TopicDto>> SearchByNoteTextAsync(
        string term,
        IReadOnlyList<Guid>? bookIds,
        int limit = 50)
    {
        var variants = LexicalQueryPlanner.Build(term);
        if (variants.Count == 0 || bookIds is { Count: 0 })
            return [];

        var take = Math.Clamp(limit, 1, 50);
        var fused = new Dictionary<Guid, TopicFusionCandidate>();

        foreach (var variant in variants)
        {
            var partial = await SearchSingleVariantAsync(variant.Text, bookIds, take);
            for (var rank = 0; rank < partial.Count; rank++)
            {
                var topic = partial[rank];
                var score = (variant.Weight * 100)
                    + (topic.NoteMatchCount * 10)
                    + Math.Max(0, take - rank);

                if (!fused.TryGetValue(topic.Id, out var current))
                {
                    fused[topic.Id] = new TopicFusionCandidate(
                        topic,
                        score,
                        variant.Text,
                        MatchedVariants: 1,
                        BestVariantWeight: variant.Weight);
                    continue;
                }

                fused[topic.Id] = current with
                {
                    Score = current.Score + score,
                    MatchedVariants = current.MatchedVariants + 1,
                    Best = topic.NoteMatchCount > current.Best.NoteMatchCount
                        ? topic
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

    private async Task<List<TopicDto>> SearchSingleVariantAsync(
        string cleanTerm,
        IReadOnlyList<Guid>? bookIds,
        int limit)
    {
        var pattern = $"%{Escape(cleanTerm)}%";

        var rows = _db.NoteTopics
            .AsNoTracking()
            .AsQueryable();

        if (bookIds is { Count: > 0 })
            rows = rows.Where(nc => bookIds.Contains(nc.Note.BookId));

        var matchingRows = await rows
            .Where(nc =>
                EF.Functions.Like(nc.Topic.Topic, pattern, "\\")
                || EF.Functions.Like(nc.Note.Content, pattern, "\\")
                || (nc.Note.SelectedText != null && EF.Functions.Like(nc.Note.SelectedText, pattern, "\\"))
                || (nc.Note.Book != null && EF.Functions.Like(nc.Note.Book.Title, pattern, "\\")))
            .Select(nc => new
            {
                nc.TopicId,
                TopicName = nc.Topic.Topic,
                TotalUsageCount = nc.Topic.NoteTopics.Count(),
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
            .GroupBy(row => new { row.TopicId, row.TopicName, row.TotalUsageCount })
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

                return new TopicDto(
                    group.Key.TopicId,
                    group.Key.TopicName,
                    group.Key.TotalUsageCount,
                    noteGroups.Count,
                    snippet);
            })
            .OrderByDescending(topic => topic.NoteMatchCount)
            .ThenBy(topic => topic.Name)
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

    private sealed record TopicFusionCandidate(
        TopicDto Best,
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

    public async Task<TopicStatsDto> GetStatsAsync()
    {
        var stats = await _db
            .Topics.Select(c => new
            {
                c.Topic,
                UsageCount = c.NoteTopics.Count(),
            })
            .GroupBy(_ => 1)
            .Select(group => new TopicStatsDto(
                group.Count(),
                group.Sum(c => c.UsageCount),
                group.Count(c => c.UsageCount == 1),
                group
                    .OrderByDescending(c => c.UsageCount)
                    .ThenBy(c => c.Topic)
                    .Select(c => c.Topic)
                    .FirstOrDefault(),
                group.Max(c => c.UsageCount)
            ))
            .SingleOrDefaultAsync();

        return stats ?? new TopicStatsDto(0, 0, 0, null, 0);
    }

    public async Task<List<RelatedTopicDto>> GetRelatedAsync(Guid id)
    {
        var noteIds = await _db
            .NoteTopics.Where(nc => nc.TopicId == id)
            .Select(nc => nc.NoteId)
            .ToListAsync();

        if (noteIds.Count == 0)
            return [];

        // Keep the raw co-occurrence rows so the API can explain each structural
        // relationship with the exact shared notes rather than only a score.
        var relatedRows = await _db
            .NoteTopics.AsNoTracking()
            .Where(nc => noteIds.Contains(nc.NoteId) && nc.TopicId != id)
            .Select(nc => new
            {
                nc.TopicId,
                Name = nc.Topic.Topic,
                nc.NoteId,
            })
            .ToListAsync();

        return relatedRows
            .GroupBy(row => new { row.TopicId, row.Name })
            .Select(group =>
            {
                var sharedNoteIds = group
                    .Select(row => row.NoteId)
                    .Distinct()
                    .OrderBy(noteId => noteId)
                    .ToList();

                return new RelatedTopicDto(
                    group.Key.TopicId,
                    group.Key.Name,
                    sharedNoteIds.Count,
                    sharedNoteIds
                );
            })
            .OrderByDescending(related => related.SharedNotes)
            .ThenBy(related => related.Name)
            .ToList();
    }

    public async Task<TopicGraphDto> GetGraphAsync()
    {
        // All topics as nodes, including isolates (0 note links).
        var nodes = await _db
            .Topics.OrderByDescending(c => c.NoteTopics.Count())
            .ThenBy(c => c.Topic)
            .Select(c => new TopicGraphNodeDto(c.Id, c.Topic, c.NoteTopics.Count()))
            .ToListAsync();

        // All note-topic pairs (note → topic id) in one query.
        var pairs = await _db.NoteTopics
            .Select(nc => new { nc.NoteId, nc.TopicId })
            .ToListAsync();

        // Group by note to find co-occurring pairs, then aggregate edge weights.
        var byNote = pairs.GroupBy(p => p.NoteId);
        var edgeMap = new Dictionary<(Guid, Guid), int>();

        foreach (var group in byNote)
        {
            var topicIds = group.Select(p => p.TopicId).Distinct().OrderBy(id => id).ToList();
            for (int i = 0; i < topicIds.Count; i++)
            {
                for (int j = i + 1; j < topicIds.Count; j++)
                {
                    var key = (topicIds[i], topicIds[j]);
                    edgeMap[key] = edgeMap.GetValueOrDefault(key) + 1;
                }
            }
        }

        var edges = edgeMap
            .Select(kv => new TopicGraphEdgeDto(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .OrderByDescending(e => e.SharedNotes)
            .ThenBy(e => e.SourceId)
            .ThenBy(e => e.TargetId)
            .ToList();

        return new TopicGraphDto(nodes, edges);
    }

    public async Task<TopicModel?> GetByIdWithNotesAsync(Guid id)
    {
        return await _db
            .Topics.Include(c => c.NoteTopics)
            .ThenInclude(nc => nc.Note)
            .ThenInclude(n => n.Book)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<TopicModel?> RenameAsync(Guid id, string name)
    {
        var source = await _db
            .Topics.Include(c => c.NoteTopics)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (source is null)
            return null;

        var target = await _db
            .Topics.Include(c => c.NoteTopics)
            .FirstOrDefaultAsync(c => c.Id != id && c.Topic == name);

        if (target is null)
        {
            source.Topic = name;
            await _db.SaveChangesAsync();
            return source;
        }

        MoveLinks(source, target);
        _db.Topics.Remove(source);
        await _db.SaveChangesAsync();
        return target;
    }

    public async Task<TopicModel?> MergeAsync(Guid sourceId, Guid targetId)
    {
        var source = await _db
            .Topics.Include(c => c.NoteTopics)
            .FirstOrDefaultAsync(c => c.Id == sourceId);
        var target = await _db
            .Topics.Include(c => c.NoteTopics)
            .FirstOrDefaultAsync(c => c.Id == targetId);

        if (source is null || target is null)
            return null;

        MoveLinks(source, target);
        _db.Topics.Remove(source);
        await _db.SaveChangesAsync();
        return target;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var topic = await _db
            .Topics.Include(c => c.NoteTopics)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (topic is null)
            return false;

        _db.NoteTopics.RemoveRange(topic.NoteTopics);
        _db.Topics.Remove(topic);
        await _db.SaveChangesAsync();
        return true;
    }

    private void MoveLinks(TopicModel source, TopicModel target)
    {
        var targetNoteIds = target.NoteTopics.Select(link => link.NoteId).ToHashSet();

        foreach (var sourceLink in source.NoteTopics.ToList())
        {
            _db.NoteTopics.Remove(sourceLink);

            if (targetNoteIds.Contains(sourceLink.NoteId))
                continue;

            var targetLink = new NoteTopicModel
            {
                NoteId = sourceLink.NoteId,
                TopicId = target.Id,
                Topic = target,
            };
            target.NoteTopics.Add(targetLink);
            _db.NoteTopics.Add(targetLink);
            targetNoteIds.Add(sourceLink.NoteId);
        }
    }

    // --- Methods for NoteProcessorService ---

    public async Task<List<TopicModel>> GetByNamesAsync(IEnumerable<string> names)
    {
        var nameList = names.ToList();
        return await _db.Topics.Where(c => nameList.Contains(c.Topic)).ToListAsync();
    }

    public void AddRange(IEnumerable<TopicModel> topics)
    {
        _db.Topics.AddRange(topics);
    }

    public async Task ClearNoteLinksAsync(Guid noteId)
    {
        var currentLinks = await _db.NoteTopics.Where(nc => nc.NoteId == noteId).ToListAsync();

        if (currentLinks.Count != 0)
        {
            _db.NoteTopics.RemoveRange(currentLinks);
        }
    }

    public void AddNoteLink(NoteTopicModel link)
    {
        _db.NoteTopics.Add(link);
    }

    // --- Method for TopicCleanupWorker ---

    public async Task<int> DeleteOrphanedAsync(CancellationToken ct = default)
    {
        // ExecuteDelete bypasses the change tracker, so the portable-state
        // revision must be advanced explicitly in the same transaction
        // (issue #679 Slice 11).
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var deleted = await _db.Topics.Where(c => !c.NoteTopics.Any()).ExecuteDeleteAsync(ct);
        await LibraryRevision.AdvanceAsync(_db, ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }
}

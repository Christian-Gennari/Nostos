using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Search;
using Nostos.Backend.Services;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Repositories;

public class TopicRepository : ITopicRepository
{
    private readonly NostosDbContext _db;

    public TopicRepository(NostosDbContext db)
    {
        _db = db;
    }

    public async Task<List<TopicDto>> GetAllWithUsageCountAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db
            .Topics.OrderByDescending(c => c.NoteTopics.Count())
            .ThenBy(c => c.Topic)
            .Select(c => new TopicDto(c.Id, c.Topic, c.NoteTopics.Count(), 0, null, c.Description))
            .ToListAsync(cancellationToken);
    }

    public Task<List<TopicDto>> SearchByNoteTextAsync(
        string term,
        CancellationToken cancellationToken = default) =>
        SearchByNoteTextAsync(term, bookIds: null, limit: 50, cancellationToken: cancellationToken);

    public async Task<List<TopicDto>> SearchByNoteTextAsync(
        string term,
        IReadOnlyList<Guid>? bookIds,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var variants = LexicalQueryPlanner.Build(term);
        if (variants.Count == 0 || bookIds is { Count: 0 })
            return [];

        var take = Math.Clamp(limit, 1, 50);
        var fused = new Dictionary<Guid, TopicFusionCandidate>();

        foreach (var variant in variants)
        {
            var partial = await SearchSingleVariantAsync(
                variant.Text,
                bookIds,
                take,
                cancellationToken);
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
        int limit,
        CancellationToken cancellationToken)
    {
        var pattern = SqlLikePatternHelper.Contains(cleanTerm);

        var rows = _db.NoteTopics
            .AsNoTracking()
            .AsQueryable();

        if (bookIds is { Count: > 0 })
            rows = rows.Where(nc => bookIds.Contains(nc.Note.BookId));

        var matchingRows = await rows
            .Where(nc =>
                EF.Functions.Like(nc.Topic.Topic, pattern, SqlLikePatternHelper.EscapeChar)
                || EF.Functions.Like(nc.Note.Content, pattern, SqlLikePatternHelper.EscapeChar)
                || (nc.Note.SelectedText != null
                    && EF.Functions.Like(nc.Note.SelectedText, pattern, SqlLikePatternHelper.EscapeChar))
                || (nc.Note.Book != null
                    && EF.Functions.Like(nc.Note.Book.Title, pattern, SqlLikePatternHelper.EscapeChar)))
            .Select(nc => new
            {
                nc.TopicId,
                TopicName = nc.Topic.Topic,
                nc.Topic.Description,
                TotalUsageCount = nc.Topic.NoteTopics.Count(),
                nc.NoteId,
                nc.Note.Content,
                nc.Note.SelectedText,
                BookTitle = nc.Note.Book != null ? nc.Note.Book.Title : null,
                nc.Note.CreatedAt
            })
            .ToListAsync(cancellationToken);

        if (matchingRows.Count == 0)
            return [];

        return matchingRows
            .GroupBy(row => new { row.TopicId, row.TopicName, row.Description, row.TotalUsageCount })
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
                    snippet,
                    group.Key.Description);
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

    public async Task<TopicStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
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
            .SingleOrDefaultAsync(cancellationToken);

        return stats ?? new TopicStatsDto(0, 0, 0, null, 0);
    }

    public async Task<List<RelatedTopicDto>> GetRelatedAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var noteIds = await _db
            .NoteTopics.Where(nc => nc.TopicId == id)
            .Select(nc => nc.NoteId)
            .ToListAsync(cancellationToken);

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
            .ToListAsync(cancellationToken);

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

    public async Task<TopicGraphDto> GetGraphAsync(CancellationToken cancellationToken = default)
    {
        // All topics as nodes, including isolates (0 note links).
        var nodes = await _db
            .Topics.OrderByDescending(c => c.NoteTopics.Count())
            .ThenBy(c => c.Topic)
            .Select(c => new TopicGraphNodeDto(c.Id, c.Topic, c.NoteTopics.Count()))
            .ToListAsync(cancellationToken);

        // All note-topic pairs (note → topic id) in one query.
        var pairs = await _db.NoteTopics
            .Select(nc => new { nc.NoteId, nc.TopicId })
            .ToListAsync(cancellationToken);

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

    public async Task<TopicWritingUsageDto?> GetWritingUsageAsync(
        Guid id, int limit, CancellationToken ct = default)
    {
        if (!await _db.Topics.AnyAsync(t => t.Id == id, ct))
            return null;

        // Citations of this topic's notes, from documents only (a folder has
        // no content, so it never has rows; the filter says so anyway).
        var citations = _db.WritingCitations.Where(c =>
            c.Writing.Type == WritingType.Document
            && _db.NoteTopics.Any(nt => nt.TopicId == id && nt.NoteId == c.NoteId));

        var writings = _db.Writings.Where(w => citations.Any(c => c.WritingId == w.Id));
        var total = await writings.CountAsync(ct);
        var shown = total == 0
            ? []
            : await writings
                .OrderByDescending(w => w.UpdatedAt)
                .ThenBy(w => w.Name)
                .ThenBy(w => w.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(w => new TopicWritingDto(w.Id, w.Name, w.UpdatedAt))
                .ToListAsync(ct);

        var pairs = total == 0
            ? []
            : await citations
                .OrderByDescending(c => c.Writing.UpdatedAt)
                .ThenBy(c => c.Writing.Name)
                .ThenBy(c => c.WritingId)
                .Select(c => new { c.NoteId, c.WritingId })
                .ToListAsync(ct);
        var perNote = pairs
            .GroupBy(p => p.NoteId)
            .Select(g => new NoteCitationCountDto(g.Key, g.Count(), g.Select(p => p.WritingId).ToList()))
            .ToList();

        return new TopicWritingUsageDto(total, shown, perNote);
    }

    public async Task<TopicModel?> GetByIdWithNotesAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Topics.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new
            {
                Topic = c,
                Notes = c.NoteTopics.AsQueryable()
                    .OrderBy(nc => nc.NoteId)
                    .Select(nc => nc.Note)
                    .Select(BookReadShapes.NoteWithBookHeadline)
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        row.Topic.NoteTopics = row.Notes
            .Select(note => new NoteTopicModel
            {
                NoteId = note.Note.Id,
                Note = note.ToNote(),
                TopicId = row.Topic.Id,
                Topic = row.Topic,
            })
            .ToList();
        return row.Topic;
    }

    public async Task<TopicModel?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _db.Topics.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<TopicModel?> RenameAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        var source = await WithLinkedNotes().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (source is null)
            return null;

        // A name another topic already has, in any case, is that topic.
        var key = TopicModel.NormalizeName(name);
        var target = await _db
            .Topics.Include(c => c.NoteTopics)
            .FirstOrDefaultAsync(c => c.Id != id && c.NormalizedName == key, cancellationToken);

        if (target is not null)
            return await MergeIntoAsync(source, target, cancellationToken);

        // A note's body is what every later save re-derives its links from, so
        // the body follows the rename; otherwise the old name comes back on the
        // note's next edit.
        RewriteReferences(source, content => TopicReferences.Rename(content, source.Topic, name));
        source.Topic = name;
        await _db.SaveChangesAsync(cancellationToken);
        return source;
    }

    public async Task<TopicModel?> MergeAsync(
        Guid sourceId,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var source = await WithLinkedNotes().FirstOrDefaultAsync(c => c.Id == sourceId, cancellationToken);
        var target = await _db
            .Topics.Include(c => c.NoteTopics)
            .FirstOrDefaultAsync(c => c.Id == targetId, cancellationToken);

        if (source is null || target is null)
            return null;

        return await MergeIntoAsync(source, target, cancellationToken);
    }

    /// <summary>
    /// Moves <paramref name="source"/>'s notes to <paramref name="target"/>, text
    /// and links, and removes the source: a merge, or a rename onto a name that
    /// is already a topic.
    /// </summary>
    private async Task<TopicModel> MergeIntoAsync(
        TopicModel source,
        TopicModel target,
        CancellationToken cancellationToken = default)
    {
        ApplyMerge(source, target);
        await _db.SaveChangesAsync(cancellationToken);
        return target;
    }

    private void ApplyMerge(TopicModel source, TopicModel target)
    {
        RewriteReferences(source, content => TopicReferences.Rename(content, source.Topic, target.Topic));
        MoveLinks(source, target);
        target.Description = MergeDescriptions(target.Description, source.Description);
        _db.Topics.Remove(source);
    }

    /// <summary>
    /// Deletes a topic and its links, and turns its references in the notes'
    /// text into plain words, so the topic stays deleted when a note is next
    /// saved.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var topic = await WithLinkedNotes().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (topic is null)
            return false;

        RewriteReferences(topic, content => TopicReferences.Remove(content, topic.Topic));
        _db.NoteTopics.RemoveRange(topic.NoteTopics);
        _db.Topics.Remove(topic);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The survivor's words, then the merged topic's after a blank line; either
    /// alone when the other has none. Cut to the length a description may have,
    /// never inside a character.
    /// </summary>
    internal static string? MergeDescriptions(string? target, string? source)
    {
        var kept = TopicModel.NormalizeDescription(target);
        var added = TopicModel.NormalizeDescription(source);
        var merged = kept is null ? added : added is null ? kept : $"{kept}\n\n{added}";
        if (merged is not { Length: > TopicModel.MaxDescriptionLength })
            return merged;
        var length = TopicModel.MaxDescriptionLength;
        if (char.IsHighSurrogate(merged[length - 1]))
            length--;
        return merged[..length].TrimEnd();
    }

    /// <summary>
    /// Sets or clears a topic's description. Returns the topic, or null when it
    /// is not found.
    /// </summary>
    public async Task<TopicModel?> SetDescriptionAsync(
        Guid id,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var topic = await _db.Topics.Include(c => c.NoteTopics).FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (topic is null)
            return null;
        topic.Description = TopicModel.NormalizeDescription(description);
        await _db.SaveChangesAsync(cancellationToken);
        return topic;
    }

    private IQueryable<TopicModel> WithLinkedNotes() =>
        _db.Topics.Include(c => c.NoteTopics).ThenInclude(link => link.Note);

    private static void RewriteReferences(TopicModel topic, Func<string, string> rewrite)
    {
        // Setting an equal value leaves the property unmodified to EF.
        foreach (var note in topic.NoteTopics.Select(link => link.Note).OfType<NoteModel>())
            note.Content = rewrite(note.Content);
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

    public async Task<List<TopicModel>> GetByNamesAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var keys = names.Select(TopicModel.NormalizeName).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
            return [];

        var stored = await _db.Topics
            .Where(c => keys.Contains(c.NormalizedName))
            .ToListAsync(cancellationToken);

        // Topics created earlier in this same unit of work are not in the
        // database yet. Linking one new topic to several notes at once must
        // reuse it, or the second note creates a duplicate the unique index
        // rejects. Matched by key, as the stored lookup above is.
        // Entries() would run DetectChanges over every tracked entity on each
        // call; an Added topic is already marked Added without it.
        var detect = _db.ChangeTracker.AutoDetectChangesEnabled;
        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var pending = _db.ChangeTracker.Entries<TopicModel>()
                .Where(entry => entry.State == EntityState.Added
                    && keys.Contains(entry.Entity.NormalizedName, StringComparer.Ordinal))
                .Select(entry => entry.Entity)
                .Where(topic => !stored.Contains(topic));
            return stored.Concat(pending).ToList();
        }
        finally
        {
            _db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }
    }

    /// <summary>
    /// Merges each topic whose key is not its name's folded form into the topic
    /// that has that name in another case, then gives every topic its key.
    /// Such rows come from an archive made before names compared in any case,
    /// or from the migration, which folds ASCII only. Returns how many topics
    /// were merged away.
    /// </summary>
    public async Task<int> MergeCaseVariantsAsync(CancellationToken ct = default)
    {
        var rows = await _db.Topics
            .AsNoTracking()
            .Select(c => new { c.Id, c.Topic, c.NormalizedName, Links = c.NoteTopics.Count() })
            .ToListAsync(ct);
        var stale = rows
            .Where(row => row.NormalizedName != TopicModel.NormalizeName(row.Topic))
            .Select(row => TopicModel.NormalizeName(row.Topic))
            .ToHashSet(StringComparer.Ordinal);
        if (stale.Count == 0)
            return 0;

        var staleGroups = rows
            .GroupBy(row => TopicModel.NormalizeName(row.Topic), StringComparer.Ordinal)
            .Where(group => stale.Contains(group.Key))
            .ToList();

        var topicIds = staleGroups.SelectMany(group => group.Select(row => row.Id)).ToList();
        var topicsById = (await WithLinkedNotes()
                .Where(c => topicIds.Contains(c.Id))
                .ToListAsync(ct))
            .ToDictionary(c => c.Id);

        var merged = 0;
        var deletedKeys = new HashSet<string>(StringComparer.Ordinal);
        var pendingSurvivorKeys = new List<(TopicModel Survivor, string Key, string OriginalKey)>();

        foreach (var group in staleGroups)
        {
            // The variant on the most notes keeps its name, then the first in
            // ordinal order (capitals first), so every run picks the same one.
            var ordered = group
                .OrderByDescending(row => row.Links)
                .ThenBy(row => row.Topic, StringComparer.Ordinal)
                .ToList();
            var survivor = topicsById[ordered[0].Id];

            foreach (var variant in ordered.Skip(1))
            {
                var source = topicsById[variant.Id];
                ApplyMerge(source, survivor);
                deletedKeys.Add(source.NormalizedName);
                merged++;
            }

            if (survivor.NormalizedName != group.Key)
                pendingSurvivorKeys.Add((survivor, group.Key, survivor.NormalizedName));
        }

        // Apply non-colliding survivor keys in the same SaveChangesAsync batch.
        // If a deleted variant still holds the survivor's target key in the DB,
        // flush deletions first inside one transaction so the unique index on
        // NormalizedName is never violated regardless of EF command ordering,
        // while keeping a single LibraryRevision advance for the transaction.
        var colliding = new List<(TopicModel Survivor, string Key, string OriginalKey)>();
        foreach (var item in pendingSurvivorKeys)
        {
            if (deletedKeys.Contains(item.Key))
                colliding.Add(item);
            else
                item.Survivor.NormalizedName = item.Key;
        }

        if (colliding.Count == 0)
        {
            await _db.SaveChangesAsync(ct);
            return merged;
        }

        await SaveWithKeyCollisionsAsync(colliding, ct);
        return merged;
    }

    private async Task SaveWithKeyCollisionsAsync(
        List<(TopicModel Survivor, string Key, string OriginalKey)> colliding,
        CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            await _db.SaveChangesAsync(ct);
            foreach (var (survivor, key, _) in colliding)
                survivor.NormalizedName = key;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                foreach (var (survivor, key, _) in colliding)
                    survivor.NormalizedName = key;
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                foreach (var (survivor, _, originalKey) in colliding)
                    survivor.NormalizedName = originalKey;
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        });
    }

    public void AddRange(IEnumerable<TopicModel> topics)
    {
        _db.Topics.AddRange(topics);
    }

    public async Task<IReadOnlyList<string>> ClearNoteLinksAsync(
        NoteModel note,
        CancellationToken cancellationToken = default)
    {
        var currentLinks = await _db.NoteTopics
            .Include(link => link.Topic)
            .Where(nc => nc.NoteId == note.Id)
            .ToListAsync(cancellationToken);

        if (currentLinks.Count == 0)
            return [];

        _db.NoteTopics.RemoveRange(currentLinks);

        // What the note said before this unit of work changed it: a link whose
        // name that text never declared was made some other way, and must not
        // vanish because the text was edited.
        var entry = _db.Entry(note);
        var stored = entry.State is EntityState.Unchanged or EntityState.Modified
            ? entry.Property(n => n.Content).OriginalValue
            : note.Content;
        var declared = TopicReferences.Names(stored).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return currentLinks
            .Select(link => link.Topic?.Topic)
            .OfType<string>()
            .Where(name => !declared.Contains(name))
            .ToList();
    }

    public void AddNoteLink(NoteTopicModel link)
    {
        _db.NoteTopics.Add(link);
    }

    // --- Method for TopicCleanupWorker ---

    public async Task<int> DeleteOrphanedAsync(CancellationToken ct = default)
    {
        // ExecuteDelete bypasses the change tracker. The revision row is
        // advanced FIRST, before the portable rows, so every portable writer
        // takes the singleton lock in the same order (issue #679 Slice 11).
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await LibraryRevision.AdvanceAndGetAsync(_db, ct);
        // A topic the reader described is kept: its words are theirs to delete.
        var deleted = await _db.Topics
            .Where(c => !c.NoteTopics.Any() && (c.Description == null || c.Description.Trim() == ""))
            .ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }
}

using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Product.BookText;

public sealed class BookTextSearchService(
    IBookTextIndex index,
    ILibraryService library,
    BookTextOptions options) : IBookTextSearchService
{
    public async Task<BookTextSearchResponse> SearchAsync(
        BookTextSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            return new BookTextSearchResponse([], [], false);

        var books = await ResolveScopeAsync(request, ct);
        if (books.Count == 0)
            return new BookTextSearchResponse([], [], false);

        var ids = books.Select(book => book.Id).Distinct().ToList();
        var states = (await index.GetStatesAsync(ids, ct)).ToList();

        var ready = states
            .Where(state => state.Status == BookTextIngestionStatus.Ready)
            .Select(state => state.BookId)
            .ToHashSet();

        var searchableIds = ids.Where(ready.Contains).ToList();
        if (searchableIds.Count == 0)
            return new BookTextSearchResponse([], states, false);

        var maxPassages = Math.Clamp(
            request.MaxPassages ?? options.MaxPassages,
            1,
            options.MaxPassages);
        var radius = Math.Clamp(
            request.NeighborRadius ?? options.NeighborRadius,
            0,
            Math.Max(0, options.NeighborRadius));

        var query = request.Query.Trim();
        var candidateLimit = Math.Max(maxPassages, options.MaxSearchCandidates);
        var hits = await index.SearchAsync(
            query,
            searchableIds,
            candidateLimit,
            ct);

        if (hits.Count == 0)
        {
            hits = await RelaxedSearchAsync(
                request,
                searchableIds,
                candidateLimit,
                ct);
        }

        var bookMap = books.ToDictionary(book => book.Id);
        var selected = new List<BookTextIndexedChunk>();
        var selectedKeys = new HashSet<(Guid BookId, string Hash, string Version, int Ordinal)>();

        void AddSelected(BookTextIndexedChunk chunk)
        {
            var key = (chunk.BookId, chunk.SourceSha256, chunk.ExtractorVersion, chunk.Ordinal);
            if (selectedKeys.Add(key))
                selected.Add(chunk);
        }

        // Neighbours of every hit in one ranged read. The selection below is the
        // same walk as before (hit, then its neighbours nearest first, until the
        // cap), so only the reads are batched, never the ranking or the content.
        var neighborsByRevision = new Dictionary<(Guid, string, string), List<BookTextIndexedChunk>>();
        if (radius > 0 && hits.Count > 0)
        {
            var wanted = hits
                .Select(hit => new BookTextNeighborQuery(
                    hit.Chunk.BookId,
                    hit.Chunk.SourceSha256,
                    hit.Chunk.ExtractorVersion,
                    hit.Chunk.Ordinal))
                .Distinct()
                .ToList();
            foreach (var neighbor in await index.GetNeighborsManyAsync(wanted, radius, ct))
            {
                var revision = (neighbor.BookId, neighbor.SourceSha256, neighbor.ExtractorVersion);
                if (!neighborsByRevision.TryGetValue(revision, out var list))
                    neighborsByRevision[revision] = list = [];
                list.Add(neighbor);
            }
        }

        foreach (var hit in hits)
        {
            var chunk = hit.Chunk;
            AddSelected(chunk);

            if (radius > 0 && selected.Count < maxPassages * 3
                && neighborsByRevision.TryGetValue(
                    (chunk.BookId, chunk.SourceSha256, chunk.ExtractorVersion),
                    out var revisionChunks))
            {
                foreach (var neighbor in revisionChunks
                    .Where(neighbor => Math.Abs(neighbor.Ordinal - chunk.Ordinal) <= radius)
                    .OrderBy(neighbor => Math.Abs(neighbor.Ordinal - chunk.Ordinal))
                    .ThenBy(neighbor => neighbor.Ordinal))
                {
                    AddSelected(neighbor);
                }
            }

            if (selected.Count >= maxPassages * 3)
                break;
        }

        var passages = new List<BookTextSearchPassage>(maxPassages);
        var totalChars = 0;

        foreach (var chunk in selected)
        {
            if (!bookMap.TryGetValue(chunk.BookId, out var book))
                continue;

            var text = chunk.Text.Length <= options.MaxPassageChars
                ? chunk.Text
                : chunk.Text[..options.MaxPassageChars];

            if (totalChars + text.Length > options.MaxTotalPassageChars)
                break;

            passages.Add(new BookTextSearchPassage(
                chunk.BookId,
                book.Title,
                book.Author,
                chunk.SourceSha256,
                chunk.ExtractorVersion,
                chunk.Format,
                chunk.Ordinal,
                text,
                chunk.HeadingPath,
                chunk.SourceSegments));

            totalChars += text.Length;
            if (passages.Count >= maxPassages)
                break;
        }

        return new BookTextSearchResponse(passages, states, passages.Count > 0);
    }

    private async Task<IReadOnlyList<BookTextSearchHit>> RelaxedSearchAsync(
        BookTextSearchRequest request,
        IReadOnlyList<Guid> bookIds,
        int candidateLimit,
        CancellationToken ct)
    {
        var tokens = Nostos.Backend.Search.LexicalQueryPlanner.Build(request.Query)
            .Where(variant =>
                variant.Kind == Nostos.Backend.Search.LexicalQueryVariantKind.Token)
            .Select(variant => variant.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (tokens.Count <= 1)
            return [];

        var joined = string.Join(" ", tokens);
        if (!string.Equals(
                CollapseWhitespace(request.Query),
                joined,
                StringComparison.OrdinalIgnoreCase))
        {
            var joinedHits = await index.SearchAsync(
                joined,
                bookIds,
                candidateLimit,
                ct);
            if (joinedHits.Count > 0)
                return joinedHits;
        }

        var bestHits = new Dictionary<Guid, BookTextSearchHit>();
        foreach (var token in tokens.Take(4))
        {
            var tokenHits = await index.SearchAsync(
                token,
                bookIds,
                candidateLimit,
                ct);

            foreach (var hit in tokenHits)
            {
                if (!bestHits.TryGetValue(hit.Chunk.Id, out var existing)
                    || hit.Score > existing.Score)
                {
                    bestHits[hit.Chunk.Id] = hit;
                }
            }
        }

        return bestHits.Values
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Chunk.Ordinal)
            .ThenBy(hit => hit.Chunk.BookId)
            .Take(candidateLimit)
            .ToList();
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private async Task<IReadOnlyList<LibraryBookScopeEntry>> ResolveScopeAsync(
        BookTextSearchRequest request,
        CancellationToken ct) =>
        await library.ListBookScopeAsync(request.BookIds, request.CollectionId, 500, ct);
}

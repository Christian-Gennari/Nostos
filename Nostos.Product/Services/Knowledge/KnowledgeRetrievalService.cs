using Microsoft.Extensions.Logging;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Search;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Knowledge;

public static class KnowledgeEvidenceKinds
{
    public const string Note = "note";
    public const string Topic = "topic";
    public const string BookText = "book_text";
}

/// <summary>
/// Stable canonical pointer to evidence. Handles are references only: assistant
/// mutations still require their normal canonical ids, trust class and approval
/// path. A handle never grants write authority.
/// </summary>
public sealed record KnowledgeEvidenceHandle(
    string Kind,
    Guid? NoteId = null,
    Guid? TopicId = null,
    Guid? BookId = null,
    string? SourceSha256 = null,
    string? ExtractorVersion = null,
    int? Ordinal = null);

public sealed record KnowledgeSearchRequest(
    string Query,
    IReadOnlyList<Guid>? BookIds = null,
    Guid? CollectionId = null,
    int? MaxPerSource = null);

public sealed record KnowledgeNoteEvidence(
    KnowledgeEvidenceHandle Handle,
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string Snippet,
    IReadOnlyList<string> TopicNames,
    DateTime CreatedAt,
    string? CfiRange,
    string SourceAnchorKind,
    string? SourceAnchorValue,
    bool AnchorVerified);

public sealed record KnowledgeTopicSupportingNote(
    KnowledgeEvidenceHandle Handle,
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string Snippet);

public sealed record KnowledgeTopicEvidence(
    KnowledgeEvidenceHandle Handle,
    Guid TopicId,
    string Name,
    int UsageCount,
    int NoteMatchCount,
    string? MatchSnippet,
    IReadOnlyList<KnowledgeTopicSupportingNote> SupportingNotes);

public sealed record KnowledgeBookEvidence(
    KnowledgeEvidenceHandle Handle,
    Guid BookId,
    string BookTitle,
    string? BookAuthor,
    string SourceSha256,
    string ExtractorVersion,
    BookTextSourceFormat Format,
    int Ordinal,
    string Text,
    IReadOnlyList<string> HeadingPath,
    IReadOnlyList<BookTextSourceSegment> SourceSegments);

public sealed record KnowledgeSearchResponse(
    IReadOnlyList<string> QueryVariants,
    IReadOnlyList<KnowledgeNoteEvidence> Notes,
    IReadOnlyList<KnowledgeTopicEvidence> Topics,
    IReadOnlyList<KnowledgeBookEvidence> BookPassages,
    IReadOnlyList<BookTextIngestionState> BookTextStates,
    bool EvidenceAvailable);

public sealed record KnowledgeBookNoteCount(Guid BookId, string BookTitle, int NoteCount);

public sealed record KnowledgeOverview(
    int TotalNotes,
    int UnlinkedNotes,
    int TotalTopics,
    int TotalTopicReferences,
    IReadOnlyList<TopicDto> TopTopics,
    IReadOnlyList<KnowledgeBookNoteCount> TopBooksByNoteCount);

public sealed record KnowledgeReadResponse(
    KnowledgeEvidenceHandle Handle,
    KnowledgeNoteRead? Note = null,
    KnowledgeTopicRead? Topic = null,
    KnowledgeBookEvidence? BookPassage = null);

public sealed record KnowledgeNoteRead(
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string Content,
    string? SelectedText,
    IReadOnlyList<string> TopicNames,
    DateTime CreatedAt,
    string? CfiRange,
    string SourceAnchorKind,
    string? SourceAnchorValue,
    bool AnchorVerified);

public sealed record KnowledgeTopicRead(
    Guid TopicId,
    string Name,
    int UsageCount,
    IReadOnlyList<KnowledgeTopicSupportingNote> Notes);

/// <summary>
/// Optional provider-neutral extension point for future semantic/hybrid
/// retrieval. Implementations return canonical handles only; Nostos resolves
/// those handles back through canonical data before exposing evidence.
/// </summary>
public interface IKnowledgeRetrievalContributor
{
    string Name { get; }

    Task<IReadOnlyList<KnowledgeEvidenceHandle>> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default);
}

public interface IKnowledgeRetrievalService
{
    Task<KnowledgeSearchResponse> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default);

    Task<KnowledgeOverview> OverviewAsync(CancellationToken ct = default);

    Task<KnowledgeReadResponse?> ReadAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct = default);
}

public sealed class NoOpKnowledgeRetrievalService : IKnowledgeRetrievalService
{
    public static NoOpKnowledgeRetrievalService Instance { get; } = new();

    private NoOpKnowledgeRetrievalService() { }

    public Task<KnowledgeSearchResponse> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new KnowledgeSearchResponse([], [], [], [], [], false));

    public Task<KnowledgeOverview> OverviewAsync(CancellationToken ct = default) =>
        Task.FromResult(new KnowledgeOverview(0, 0, 0, 0, [], []));

    public Task<KnowledgeReadResponse?> ReadAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct = default) =>
        Task.FromResult<KnowledgeReadResponse?>(null);
}

public sealed class KnowledgeRetrievalService(
    INoteService notes,
    INoteRepository noteRepository,
    ITopicRepository topics,
    ILibraryService library,
    IBookTextSearchService bookText,
    IBookTextIndex bookTextIndex,
    IEnumerable<IKnowledgeRetrievalContributor> contributors,
    IEmbeddingProvider? embeddings = null,
    IBookTextEmbeddingIndex? embeddingIndex = null,
    BookTextOptions? bookTextOptions = null,
    ILogger<KnowledgeRetrievalService>? logger = null) : IKnowledgeRetrievalService
{
    /// <summary>Candidates taken from each channel (lexical, vector) before fusion.</summary>
    public const int HybridCandidatesPerChannel = 30;

    // A search turn must not wait out the provider's own (ingestion-sized)
    // request timeout for a single query vector.
    private static readonly TimeSpan QueryEmbeddingTimeout = TimeSpan.FromSeconds(10);

    private const int DefaultMaxPerSource = 6;
    private const int MaximumMaxPerSource = 8;
    private const int SupportingNotesPerTopic = 3;
    private const int OverviewTopCount = 12;

    public async Task<KnowledgeSearchResponse> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            return new KnowledgeSearchResponse([], [], [], [], [], false);

        var variants = LexicalQueryPlanner.Build(request.Query);
        if (variants.Count == 0)
            return new KnowledgeSearchResponse([], [], [], [], [], false);

        var max = Math.Clamp(
            request.MaxPerSource ?? DefaultMaxPerSource,
            1,
            MaximumMaxPerSource);

        var scopedBookIds = await ResolveScopedBookIdsAsync(request, ct);

        var noteHits = await notes.SearchAsync(
            request.Query,
            max,
            scopedBookIds,
            ct);

        var topicHits = await topics.SearchByNoteTextAsync(
            request.Query,
            scopedBookIds,
            max);

        var bookResponse = await bookText.SearchAsync(
            new BookTextSearchRequest(
                request.Query,
                request.BookIds,
                request.CollectionId,
                max),
            ct);

        var noteEvidence = noteHits
            .Select(ToNoteEvidence)
            .ToList();

        var topicEvidence = new List<KnowledgeTopicEvidence>(topicHits.Count);
        foreach (var topic in topicHits)
        {
            topicEvidence.Add(await ToTopicEvidenceAsync(
                topic,
                variants,
                scopedBookIds,
                ct));
        }

        var bookEvidence = await FuseBookEvidenceAsync(
            request.Query.Trim(),
            bookResponse,
            bookResponse.Passages.Select(ToBookEvidence).ToList(),
            max,
            ct);

        // Optional contributors can add canonical candidates (for example a
        // future embedding index). Their output is never trusted as evidence on
        // its own: resolve every handle through current Nostos data first.
        foreach (var contributor in contributors)
        {
            var handles = await contributor.SearchAsync(request, ct);
            foreach (var handle in handles.Take(max * 3))
            {
                var resolved = await ReadAsync(handle, ct);
                if (resolved is null)
                    continue;

                if (resolved.Note is { } note
                    && IsBookAllowed(note.BookId, scopedBookIds)
                    && noteEvidence.Count < max
                    && noteEvidence.All(item => item.NoteId != note.NoteId))
                {
                    noteEvidence.Add(ToNoteEvidence(note));
                }
                else if (resolved.Topic is { } topic
                    && topicEvidence.Count < max
                    && topicEvidence.All(item => item.TopicId != topic.TopicId))
                {
                    var scopedTopic = ToTopicEvidence(topic, scopedBookIds);
                    if (scopedTopic is not null)
                        topicEvidence.Add(scopedTopic);
                }
                else if (resolved.BookPassage is { } passage
                    && IsBookAllowed(passage.BookId, scopedBookIds)
                    && bookEvidence.Count < max
                    && bookEvidence.All(item => item.Handle != passage.Handle))
                {
                    bookEvidence.Add(passage);
                }
            }
        }

        return new KnowledgeSearchResponse(
            variants.Select(variant => variant.Text).ToList(),
            noteEvidence.Take(max).ToList(),
            topicEvidence.Take(max).ToList(),
            bookEvidence.Take(max).ToList(),
            bookResponse.States,
            noteEvidence.Count > 0 || topicEvidence.Count > 0 || bookEvidence.Count > 0);
    }

    public async Task<KnowledgeOverview> OverviewAsync(CancellationToken ct = default)
    {
        var totalNotes = await noteRepository.CountAsync();
        var unlinkedNotes = await noteRepository.CountWithoutTopicsAsync();
        var topicStats = await topics.GetStatsAsync();
        var topTopics = (await topics.GetAllWithUsageCountAsync())
            .Take(OverviewTopCount)
            .ToList();
        var topBooks = await noteRepository.GetBookCountsAsync(OverviewTopCount);

        return new KnowledgeOverview(
            totalNotes,
            unlinkedNotes,
            topicStats.TotalTopics,
            topicStats.TotalReferences,
            topTopics,
            topBooks
                .Select(item => new KnowledgeBookNoteCount(
                    item.BookId,
                    item.BookTitle,
                    item.NoteCount))
                .ToList());
    }

    public async Task<KnowledgeReadResponse?> ReadAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (string.Equals(handle.Kind, KnowledgeEvidenceKinds.Note, StringComparison.Ordinal))
        {
            if (handle.NoteId is not { } noteId)
                return null;

            var note = await noteRepository.GetByIdWithTopicsAsync(noteId);
            return note is null
                ? null
                : new KnowledgeReadResponse(handle, Note: ToNoteRead(note));
        }

        if (string.Equals(handle.Kind, KnowledgeEvidenceKinds.Topic, StringComparison.Ordinal))
        {
            if (handle.TopicId is not { } topicId)
                return null;

            var topic = await topics.GetByIdWithNotesAsync(topicId);
            if (topic is null)
                return null;

            return new KnowledgeReadResponse(
                handle,
                Topic: ToTopicRead(topic));
        }

        if (string.Equals(handle.Kind, KnowledgeEvidenceKinds.BookText, StringComparison.Ordinal))
        {
            var passage = await ReadBookPassageAsync(handle, ct);
            return passage is null
                ? null
                : new KnowledgeReadResponse(handle, BookPassage: passage);
        }

        return null;
    }

    private async Task<IReadOnlyList<Guid>?> ResolveScopedBookIdsAsync(
        KnowledgeSearchRequest request,
        CancellationToken ct)
    {
        if (request.BookIds is { Count: > 0 })
        {
            var valid = new List<Guid>();
            foreach (var bookId in request.BookIds.Distinct().Take(32))
            {
                var result = await library.GetBookAsync(bookId, ct);
                if (result.Data is BookDto)
                    valid.Add(bookId);
            }
            return valid;
        }

        if (request.CollectionId is not { } collectionId)
            return null;

        const int pageSize = 100;
        var page = 1;
        var ids = new List<Guid>();
        while (ids.Count < 500)
        {
            var result = await library.ListBooksAsync(
                BookFilter.All,
                BookSort.Title,
                search: null,
                page,
                pageSize,
                collectionId,
                groupByWork: false,
                format: null,
                ct);

            if (result.Data is not PaginatedResponse<BookDto> batch)
                break;

            var items = batch.Items.ToList();
            ids.AddRange(items.Select(item => item.Id));
            if (items.Count == 0 || ids.Count >= batch.TotalCount)
                break;
            page++;
        }

        return ids;
    }

    private async Task<KnowledgeTopicEvidence> ToTopicEvidenceAsync(
        TopicDto topic,
        IReadOnlyList<LexicalQueryVariant> variants,
        IReadOnlyList<Guid>? scopedBookIds,
        CancellationToken ct)
    {
        var model = await topics.GetByIdWithNotesAsync(topic.Id);
        IReadOnlyList<KnowledgeTopicSupportingNote> supporting = model is null
            ? []
            : SupportingNotes(
                model,
                variants.Select(variant => variant.Text).ToList(),
                scopedBookIds,
                SupportingNotesPerTopic);

        return new KnowledgeTopicEvidence(
            TopicHandle(topic.Id),
            topic.Id,
            topic.Name,
            topic.UsageCount,
            topic.NoteMatchCount,
            topic.NoteMatchSnippet,
            supporting);
    }

    private static IReadOnlyList<KnowledgeTopicSupportingNote> SupportingNotes(
        TopicModel topic,
        IReadOnlyList<string> variants,
        IReadOnlyList<Guid>? scopedBookIds,
        int limit)
    {
        return topic.NoteTopics
            .Select(link => link.Note)
            .Where(note => note is not null)
            .Cast<NoteModel>()
            .Where(note => scopedBookIds is null || scopedBookIds.Contains(note.BookId))
            .Select(note => new
            {
                Note = note,
                MatchCount = variants.Count(variant => Matches(note, variant)),
            })
            .Where(candidate => candidate.MatchCount > 0)
            .OrderByDescending(candidate => candidate.MatchCount)
            .ThenByDescending(candidate => candidate.Note.CreatedAt)
            .ThenBy(candidate => candidate.Note.Id)
            .Take(limit)
            .Select(candidate => new KnowledgeTopicSupportingNote(
                NoteHandle(candidate.Note.Id),
                candidate.Note.Id,
                candidate.Note.BookId,
                candidate.Note.Book?.Title,
                Snippet(candidate.Note, variants)))
            .ToList();
    }

    private static bool Matches(NoteModel note, string variant) =>
        note.Content.Contains(variant, StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(note.SelectedText)
            && note.SelectedText.Contains(variant, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(note.Book?.Title)
            && note.Book.Title.Contains(variant, StringComparison.OrdinalIgnoreCase));

    private async Task<KnowledgeBookEvidence?> ReadBookPassageAsync(
        KnowledgeEvidenceHandle handle,
        CancellationToken ct)
    {
        if (handle.BookId is not { } bookId
            || string.IsNullOrWhiteSpace(handle.SourceSha256)
            || string.IsNullOrWhiteSpace(handle.ExtractorVersion)
            || handle.Ordinal is not { } ordinal)
            return null;

        var state = await bookTextIndex.GetStateAsync(bookId, ct);
        if (state is null
            || state.Status != BookTextIngestionStatus.Ready
            || !string.Equals(state.SourceSha256, handle.SourceSha256, StringComparison.Ordinal)
            || !string.Equals(state.ExtractorVersion, handle.ExtractorVersion, StringComparison.Ordinal))
            return null;

        // Existing neighbor API is revision-scoped and includes the requested
        // ordinal. Selecting the exact ordinal gives a stable reread without
        // broadening the public book-text index contract.
        var nearby = await bookTextIndex.GetNeighborsAsync(
            bookId,
            handle.SourceSha256,
            handle.ExtractorVersion,
            ordinal,
            radius: 1,
            ct);
        var chunk = nearby.SingleOrDefault(item => item.Ordinal == ordinal);
        if (chunk is null)
            return null;

        var bookResult = await library.GetBookAsync(bookId, ct);
        if (bookResult.Data is not BookDto book)
            return null;

        return new KnowledgeBookEvidence(
            BookHandle(chunk),
            chunk.BookId,
            book.Title,
            book.Author,
            chunk.SourceSha256,
            chunk.ExtractorVersion,
            chunk.Format,
            chunk.Ordinal,
            chunk.Text,
            chunk.HeadingPath,
            chunk.SourceSegments);
    }

    /// <summary>
    /// Hybrid passage retrieval (issue #683): fuses the lexical FTS ranking with
    /// the vector ranking by Reciprocal Rank Fusion.
    ///
    /// The vector channel is strictly additive. With no active embedding model,
    /// no stored vectors, or a provider that fails or times out, this returns
    /// <paramref name="lexical"/> untouched — the turn never fails because of
    /// embeddings. Only caller cancellation propagates.
    /// </summary>
    private async Task<List<KnowledgeBookEvidence>> FuseBookEvidenceAsync(
        string query,
        BookTextSearchResponse bookResponse,
        List<KnowledgeBookEvidence> lexical,
        int max,
        CancellationToken ct)
    {
        if (embeddings is null || embeddingIndex is null)
            return lexical;

        // Same visibility rule as the lexical pipeline: only books whose
        // current revision is Ready, inside the scope it already resolved.
        var readyBookIds = bookResponse.States
            .Where(state => state.Status == BookTextIngestionStatus.Ready)
            .Select(state => state.BookId)
            .Distinct()
            .ToList();
        if (readyBookIds.Count == 0)
            return lexical;

        IReadOnlyList<BookTextSearchHit> vectorHits;
        try
        {
            var model = await embeddings.GetActiveModelAsync(ct);
            if (string.IsNullOrWhiteSpace(model))
                return lexical;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryEmbeddingTimeout);

            var batch = await embeddings.EmbedAsync([query], timeout.Token);
            if (batch.Vectors.Count == 0 || batch.Vectors[0].Length == 0)
                return lexical;

            vectorHits = await embeddingIndex.SearchAsync(
                model,
                batch.Vectors[0],
                readyBookIds,
                HybridCandidatesPerChannel,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Deliberately no query text in this line.
            logger?.LogWarning(
                "Vector retrieval failed with {Failure}; answering from lexical search only.",
                exception is EmbeddingException embedding
                    ? embedding.Code
                    : exception.GetType().Name);
            return lexical;
        }

        if (vectorHits.Count == 0)
            return lexical;

        var lexicalHits = await bookTextIndex.SearchAsync(
            query,
            readyBookIds,
            HybridCandidatesPerChannel,
            ct);

        var chunks = new Dictionary<KnowledgeEvidenceHandle, BookTextIndexedChunk>();
        foreach (var hit in lexicalHits.Concat(vectorHits))
            chunks.TryAdd(BookHandle(hit.Chunk), hit.Chunk);

        // The strict FTS query can come back empty where the lexical pipeline's
        // relaxed fallback still found passages; those then stand in as the
        // lexical ranking so the channel is never dropped from the fusion.
        var lexicalRanking = lexicalHits.Count > 0
            ? lexicalHits.Select(hit => BookHandle(hit.Chunk)).ToList()
            : lexical.Select(item => item.Handle).ToList();

        var fused = ReciprocalRankFusion.Fuse<KnowledgeEvidenceHandle>(
        [
            new(lexicalRanking),
            new(vectorHits.Select(hit => BookHandle(hit.Chunk)).ToList()),
        ]);

        var options = bookTextOptions ?? new BookTextOptions();
        var lexicalByHandle = lexical
            .GroupBy(item => item.Handle)
            .ToDictionary(group => group.Key, group => group.First());
        var books = new Dictionary<Guid, BookDto?>();
        var evidence = new List<KnowledgeBookEvidence>(max);
        var totalChars = 0;

        foreach (var candidate in fused)
        {
            KnowledgeBookEvidence? item;
            if (chunks.TryGetValue(candidate.Key, out var chunk))
            {
                if (!books.TryGetValue(chunk.BookId, out var book))
                {
                    book = (await library.GetBookAsync(chunk.BookId, ct)).Data as BookDto;
                    books[chunk.BookId] = book;
                }

                if (book is null)
                    continue;

                item = new KnowledgeBookEvidence(
                    candidate.Key,
                    chunk.BookId,
                    book.Title,
                    book.Author,
                    chunk.SourceSha256,
                    chunk.ExtractorVersion,
                    chunk.Format,
                    chunk.Ordinal,
                    chunk.Text.Length <= options.MaxPassageChars
                        ? chunk.Text
                        : chunk.Text[..options.MaxPassageChars],
                    chunk.HeadingPath,
                    chunk.SourceSegments);
            }
            else if (!lexicalByHandle.TryGetValue(candidate.Key, out item))
            {
                continue;
            }

            // The same passage budget the lexical pipeline enforces.
            if (totalChars + item.Text.Length > options.MaxTotalPassageChars)
                break;

            evidence.Add(item);
            totalChars += item.Text.Length;
            if (evidence.Count >= max)
                break;
        }

        return evidence.Count > 0 ? evidence : lexical;
    }

    private static KnowledgeNoteEvidence ToNoteEvidence(NoteSearchHitDto note) =>
        new(
            NoteHandle(note.Id),
            note.Id,
            note.BookId,
            note.BookTitle,
            note.Snippet ?? Clip(note.SelectedText ?? note.Content, 240),
            note.TopicNames,
            note.CreatedAt,
            note.CfiRange,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified);

    private static KnowledgeNoteEvidence ToNoteEvidence(KnowledgeNoteRead note) =>
        new(
            NoteHandle(note.NoteId),
            note.NoteId,
            note.BookId,
            note.BookTitle,
            Clip(note.SelectedText ?? note.Content, 240),
            note.TopicNames,
            note.CreatedAt,
            note.CfiRange,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified);

    private static KnowledgeTopicEvidence? ToTopicEvidence(
        KnowledgeTopicRead topic,
        IReadOnlyList<Guid>? scopedBookIds)
    {
        var notes = topic.Notes
            .Where(note => IsBookAllowed(note.BookId, scopedBookIds))
            .ToList();

        if (scopedBookIds is not null && notes.Count == 0)
            return null;

        return new KnowledgeTopicEvidence(
            TopicHandle(topic.TopicId),
            topic.TopicId,
            topic.Name,
            topic.UsageCount,
            notes.Count,
            notes.FirstOrDefault()?.Snippet,
            notes);
    }

    private static bool IsBookAllowed(
        Guid bookId,
        IReadOnlyList<Guid>? scopedBookIds) =>
        scopedBookIds is null || scopedBookIds.Contains(bookId);

    private static KnowledgeBookEvidence ToBookEvidence(BookTextSearchPassage passage) =>
        new(
            new KnowledgeEvidenceHandle(
                KnowledgeEvidenceKinds.BookText,
                BookId: passage.BookId,
                SourceSha256: passage.SourceSha256,
                ExtractorVersion: passage.ExtractorVersion,
                Ordinal: passage.Ordinal),
            passage.BookId,
            passage.BookTitle,
            passage.BookAuthor,
            passage.SourceSha256,
            passage.ExtractorVersion,
            passage.Format,
            passage.Ordinal,
            passage.Text,
            passage.HeadingPath,
            passage.SourceSegments);

    private static KnowledgeNoteRead ToNoteRead(NoteModel note) =>
        new(
            note.Id,
            note.BookId,
            note.Book?.Title,
            note.Content,
            note.SelectedText,
            note.NoteTopics
                .Where(link => link.Topic is not null)
                .Select(link => link.Topic!.Topic)
                .OrderBy(name => name)
                .ToList(),
            note.CreatedAt,
            note.CfiRange,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified);

    private static KnowledgeTopicRead ToTopicRead(TopicModel topic)
    {
        var notes = topic.NoteTopics
            .Select(link => link.Note)
            .Where(note => note is not null)
            .Cast<NoteModel>()
            .OrderByDescending(note => note.CreatedAt)
            .ThenBy(note => note.Id)
            .Take(12)
            .Select(note => new KnowledgeTopicSupportingNote(
                NoteHandle(note.Id),
                note.Id,
                note.BookId,
                note.Book?.Title,
                Clip(note.SelectedText ?? note.Content, 240)))
            .ToList();

        return new KnowledgeTopicRead(
            topic.Id,
            topic.Topic,
            topic.NoteTopics.Count,
            notes);
    }

    private static KnowledgeEvidenceHandle NoteHandle(Guid noteId) =>
        new(KnowledgeEvidenceKinds.Note, NoteId: noteId);

    private static KnowledgeEvidenceHandle TopicHandle(Guid topicId) =>
        new(KnowledgeEvidenceKinds.Topic, TopicId: topicId);

    private static KnowledgeEvidenceHandle BookHandle(BookTextIndexedChunk chunk) =>
        new(
            KnowledgeEvidenceKinds.BookText,
            BookId: chunk.BookId,
            SourceSha256: chunk.SourceSha256,
            ExtractorVersion: chunk.ExtractorVersion,
            Ordinal: chunk.Ordinal);

    private static string Snippet(NoteModel note, IReadOnlyList<string> variants)
    {
        var source = string.IsNullOrWhiteSpace(note.SelectedText)
            ? note.Content
            : note.SelectedText!;

        var flat = string.Join(
            ' ',
            source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var match = variants
            .Select(variant => (Variant: variant, Index: flat.IndexOf(variant, StringComparison.OrdinalIgnoreCase)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .FirstOrDefault();

        if (match.Variant is null)
            return Clip(flat, 240);

        var start = Math.Max(0, match.Index - 80);
        var length = Math.Min(240, flat.Length - start);
        var snippet = flat.Substring(start, length);
        return (start > 0 ? "…" : string.Empty)
            + snippet
            + (start + length < flat.Length ? "…" : string.Empty);
    }

    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var flat = string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Product.BookText;

public sealed class BookTextOptions
{
    public const string SectionName = "BookText";

    public long MaxSourceBytes { get; set; } = 512L * 1024 * 1024;
    public int TargetChunkChars { get; set; } = 1500;
    public int MaxChunkChars { get; set; } = 2500;
    public int OverlapChars { get; set; } = 180;
    public int MaxSearchCandidates { get; set; } = 24;
    public int MaxPassages { get; set; } = 8;
    public int MaxPassageChars { get; set; } = 1800;
    public int MaxTotalPassageChars { get; set; } = 12000;
    public int NeighborRadius { get; set; } = 1;
    public int StaleProcessingMinutes { get; set; } = 15;

    // Passage embeddings (issue #683). Only used when the host has an active
    // embedding provider; with none, these are inert.
    public int EmbeddingBatchSize { get; set; } = 16;
    public int EmbeddingIdlePollSeconds { get; set; } = 30;
    public int EmbeddingFailureBackoffSeconds { get; set; } = 300;
}

public enum BookTextIngestionStatus
{
    Pending,
    Processing,
    Ready,
    Failed,
    Unsupported,
}

public sealed record BookTextIngestionWork(
    Guid BookId,
    string SourceFileName,
    BookTextSourceFormat Format,
    int Attempt);

public sealed record BookTextIngestionState(
    Guid BookId,
    BookTextIngestionStatus Status,
    string? SourceFileName,
    BookTextSourceFormat? Format,
    string? SourceSha256,
    string? ExtractorVersion,
    string? ErrorCode,
    string? ErrorMessage,
    int Attempts,
    int ChunkCount,
    long CharacterCount,
    DateTime UpdatedAtUtc);

public sealed record BookTextExtractedDocument(
    BookTextSourceFormat Format,
    IReadOnlyList<BookTextArtifactBlock> Blocks)
{
    public long CharacterCount => Blocks.Sum(block => (long)block.Text.Length);
}

public interface IBookTextExtractor
{
    BookTextSourceFormat Format { get; }
    Task<BookTextExtractedDocument> ExtractAsync(Stream source, CancellationToken ct = default);
}

public sealed record BookTextIndexedChunk(
    Guid Id,
    Guid BookId,
    string SourceSha256,
    string ExtractorVersion,
    BookTextSourceFormat Format,
    int Ordinal,
    string Text,
    IReadOnlyList<string> HeadingPath,
    IReadOnlyList<BookTextSourceSegment> SourceSegments);

public sealed record BookTextSearchHit(BookTextIndexedChunk Chunk, double Score);

public sealed record BookTextSearchRequest(
    string Query,
    IReadOnlyList<Guid>? BookIds = null,
    Guid? CollectionId = null,
    int? MaxPassages = null,
    int? NeighborRadius = null);

public sealed record BookTextSearchPassage(
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

public sealed record BookTextSearchResponse(
    IReadOnlyList<BookTextSearchPassage> Passages,
    IReadOnlyList<BookTextIngestionState> States,
    bool EvidenceAvailable);

public interface IBookTextIndex
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task ScheduleAsync(
        Guid bookId,
        string sourceFileName,
        BookTextSourceFormat format,
        CancellationToken ct = default);

    Task<BookTextIngestionWork?> TryClaimNextAsync(
        TimeSpan staleAfter,
        CancellationToken ct = default);

    Task<bool> ReplaceReadyAsync(
        BookTextSourceRevision revision,
        IReadOnlyList<BookTextIndexedChunk> chunks,
        long characterCount,
        int expectedAttempt,
        CancellationToken ct = default);

    Task<bool> MarkFailedAsync(
        Guid bookId,
        string errorCode,
        string errorMessage,
        bool unsupported,
        int expectedAttempt,
        CancellationToken ct = default);

    Task DeleteBookAsync(Guid bookId, CancellationToken ct = default);

    Task<BookTextIngestionState?> GetStateAsync(Guid bookId, CancellationToken ct = default);

    Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(
        string query,
        IReadOnlyList<Guid> bookIds,
        int maxCandidates,
        CancellationToken ct = default);

    Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(
        Guid bookId,
        string sourceSha256,
        string extractorVersion,
        int ordinal,
        int radius,
        CancellationToken ct = default);
}

public interface IBookDerivedArtifactStorage
{
    Task WriteAsync(
        BookTextSourceRevision revision,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken ct = default);

    Task DeleteBookArtifactsAsync(Guid bookId, CancellationToken ct = default);

    Task PruneBookArtifactsAsync(
        Guid bookId,
        BookTextSourceRevision keep,
        CancellationToken ct = default);
}

public interface IBookTextIngestionScheduler
{
    Task ScheduleAsync(Guid bookId, string sourceFileName, CancellationToken ct = default);
}

public interface IBookTextLifecycle
{
    Task DeleteAsync(Guid bookId, CancellationToken ct = default);
}

public interface IBookTextSearchService
{
    Task<BookTextSearchResponse> SearchAsync(BookTextSearchRequest request, CancellationToken ct = default);
}

public sealed class BookTextBackfillService(
    ILibraryService library,
    IBookTextIndex index,
    IBookTextIngestionScheduler scheduler,
    ILogger<BookTextBackfillService> logger)
{
    public async Task<int> ScheduleMissingAsync(CancellationToken ct = default)
    {
        const int pageSize = 100;
        var page = 1;
        var scheduled = 0;

        while (true)
        {
            var result = await library.ListBooksAsync(
                BookFilter.All,
                BookSort.Title,
                search: null,
                page,
                pageSize,
                collectionId: null,
                groupByWork: false,
                format: null,
                ct);

            if (result.Data is not PaginatedResponse<BookDto> batch)
                break;

            var items = batch.Items.ToList();
            foreach (var book in items)
            {
                if (!book.HasFile || string.IsNullOrWhiteSpace(book.FileName)
                    || !BookTextFormatResolver.TryResolve(book.FileName, out _))
                    continue;

                var state = await index.GetStateAsync(book.Id, ct);
                if (state is not null
                    && string.Equals(
                        state.ExtractorVersion,
                        BookTextArtifactSchema.CurrentExtractorVersion,
                        StringComparison.Ordinal))
                    continue;

                await scheduler.ScheduleAsync(book.Id, book.FileName, ct);
                scheduled++;
            }

            if (items.Count == 0 || page * pageSize >= batch.TotalCount)
                break;

            page++;
        }

        if (scheduled > 0)
        {
            logger.LogInformation(
                "Scheduled {Count} existing imported publication(s) for book-text indexing.",
                scheduled);
        }

        return scheduled;
    }
}

public sealed class NoOpBookTextIndex : IBookTextIndex
{
    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleAsync(Guid bookId, string sourceFileName, BookTextSourceFormat format, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BookTextIngestionWork?> TryClaimNextAsync(TimeSpan staleAfter, CancellationToken ct = default) => Task.FromResult<BookTextIngestionWork?>(null);
    public Task<bool> ReplaceReadyAsync(BookTextSourceRevision revision, IReadOnlyList<BookTextIndexedChunk> chunks, long characterCount, int expectedAttempt, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> MarkFailedAsync(Guid bookId, string errorCode, string errorMessage, bool unsupported, int expectedAttempt, CancellationToken ct = default) => Task.FromResult(false);
    public Task DeleteBookAsync(Guid bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BookTextIngestionState?> GetStateAsync(Guid bookId, CancellationToken ct = default) => Task.FromResult<BookTextIngestionState?>(null);
    public Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(string query, IReadOnlyList<Guid> bookIds, int maxCandidates, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextSearchHit>>([]);
    public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(Guid bookId, string sourceSha256, string extractorVersion, int ordinal, int radius, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextIndexedChunk>>([]);
}

public sealed class NoOpBookTextArtifactStorage : IBookDerivedArtifactStorage
{
    public Task WriteAsync(BookTextSourceRevision revision, Func<Stream, CancellationToken, Task> writer, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task DeleteBookArtifactsAsync(Guid bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task PruneBookArtifactsAsync(Guid bookId, BookTextSourceRevision keep, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class NoOpBookTextIngestionScheduler : IBookTextIngestionScheduler
{
    public Task ScheduleAsync(Guid bookId, string sourceFileName, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class BookTextIngestionScheduler(
    IBookTextIndex index,
    IBookDerivedArtifactStorage artifacts,
    ILogger<BookTextIngestionScheduler> logger) : IBookTextIngestionScheduler
{
    public async Task ScheduleAsync(Guid bookId, string sourceFileName, CancellationToken ct = default)
    {
        if (!BookTextFormatResolver.TryResolve(sourceFileName, out var format))
            return;

        try
        {
            // Invalidate retrieval first. If derived-object cleanup is slower or
            // temporarily unavailable, a replaced source must still stop serving
            // chunks from the previous revision immediately.
            await index.ScheduleAsync(bookId, sourceFileName, format, ct);
            await artifacts.DeleteBookArtifactsAsync(bookId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Could not schedule book-text ingestion for {BookId}; exception type {ExceptionType}. The source import remains valid.",
                bookId,
                exception.GetType().Name);
        }
    }
}

public sealed class BookTextLifecycle(
    IBookTextIndex index,
    IBookDerivedArtifactStorage artifacts) : IBookTextLifecycle
{
    public async Task DeleteAsync(Guid bookId, CancellationToken ct = default)
    {
        await index.DeleteBookAsync(bookId, ct);
        await artifacts.DeleteBookArtifactsAsync(bookId, ct);
    }
}

public static class BookTextFormatResolver
{
    public static bool TryResolve(string? fileName, out BookTextSourceFormat format)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            format = BookTextSourceFormat.Pdf;
            return true;
        }

        if (extension.Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            format = BookTextSourceFormat.Epub;
            return true;
        }

        format = default;
        return false;
    }
}

public sealed class BookTextUnsupportedException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class BookTextIngestionEngine(
    IBookAssetStorage sourceStorage,
    IBookDerivedArtifactStorage artifacts,
    IBookTextIndex index,
    IEnumerable<IBookTextExtractor> extractors,
    BookTextOptions options,
    ILogger<BookTextIngestionEngine> logger)
{
    private readonly IReadOnlyDictionary<BookTextSourceFormat, IBookTextExtractor> _extractors =
        extractors.ToDictionary(extractor => extractor.Format);

    public async Task ProcessAsync(BookTextIngestionWork work, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        string? tempPath = null;

        try
        {
            var info = await sourceStorage.GetBookFileInfoAsync(work.BookId, ct)
                ?? throw new BookTextUnsupportedException(
                    "book_text_source_missing",
                    "The source publication is no longer available.");

            if (info.Length <= 0)
                throw new BookTextUnsupportedException(
                    "book_text_source_empty",
                    "The source publication is empty.");

            if (options.MaxSourceBytes > 0 && info.Length > options.MaxSourceBytes)
                throw new BookTextUnsupportedException(
                    "book_text_source_too_large",
                    "The source publication is above the configured indexing size limit.");

            if (!_extractors.TryGetValue(work.Format, out var extractor))
                throw new BookTextUnsupportedException(
                    "book_text_format_unsupported",
                    "This publication format is not supported for text indexing.");

            await using var source = await sourceStorage.OpenBookFileAsync(work.BookId, range: null, ct)
                ?? throw new BookTextUnsupportedException(
                    "book_text_source_missing",
                    "The source publication is no longer available.");

            tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nostos-book-text-{work.BookId:N}-{Guid.NewGuid():N}{Path.GetExtension(info.FileName)}");

            string sourceHash;
            await using (var staged = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await source.Content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await staged.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await staged.FlushAsync(ct);
                sourceHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            await using var extractionSource = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var extracted = await extractor.ExtractAsync(extractionSource, ct);
            if (extracted.Blocks.Count == 0 || extracted.CharacterCount < 20)
                throw new BookTextUnsupportedException(
                    work.Format == BookTextSourceFormat.Pdf
                        ? "book_text_pdf_no_extractable_text"
                        : "book_text_no_extractable_text",
                    "The publication did not contain enough extractable text.");

            var revision = new BookTextSourceRevision(
                work.BookId,
                sourceHash,
                BookTextArtifactSchema.CurrentExtractorVersion,
                work.Format);

            var chunks = BookTextChunker.Chunk(
                revision,
                extracted.Blocks,
                options.TargetChunkChars,
                options.MaxChunkChars,
                options.OverlapChars);

            if (chunks.Count == 0)
                throw new BookTextUnsupportedException(
                    "book_text_no_chunks",
                    "The publication did not contain searchable text after normalization.");

            await artifacts.WriteAsync(
                revision,
                (destination, token) => BookTextArtifactCodec.WriteAsync(
                    destination,
                    revision,
                    extracted.Blocks,
                    token),
                ct);

            var committed = await index.ReplaceReadyAsync(
                revision,
                chunks,
                extracted.CharacterCount,
                work.Attempt,
                ct);

            if (!committed)
            {
                // A delete/replacement/retry superseded this worker after it had
                // already extracted the source. Never resurrect stale chunks.
                // If the state row itself is gone, the book was deleted and no
                // newer worker can own the artifact namespace, so clean it fully.
                if (await index.GetStateAsync(work.BookId, ct) is null)
                    await artifacts.DeleteBookArtifactsAsync(work.BookId, ct);

                logger.LogInformation(
                    "Book-text ingestion result for {BookId} was superseded by newer lifecycle state.",
                    work.BookId);
                return;
            }

            // The current claim won. Any older source/extractor artifacts are
            // now unreachable and can be removed without racing the active
            // revision.
            await artifacts.PruneBookArtifactsAsync(work.BookId, revision, ct);

            logger.LogInformation(
                "Book-text ingestion completed for {BookId}: format {Format}, source bytes {SourceBytes}, characters {Characters}, chunks {Chunks}, elapsed {ElapsedMs} ms.",
                work.BookId,
                work.Format,
                info.Length,
                extracted.CharacterCount,
                chunks.Count,
                (long)(DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BookTextUnsupportedException exception)
        {
            await index.MarkFailedAsync(
                work.BookId,
                exception.Code,
                exception.Message,
                unsupported: true,
                work.Attempt,
                CancellationToken.None);

            logger.LogInformation(
                "Book-text ingestion is unsupported for {BookId}: {Code}.",
                work.BookId,
                exception.Code);
        }
        catch (Exception exception)
        {
            await index.MarkFailedAsync(
                work.BookId,
                "book_text_ingestion_failed",
                "Text indexing failed and can be retried.",
                unsupported: false,
                work.Attempt,
                CancellationToken.None);

            logger.LogWarning(
                "Book-text ingestion failed for {BookId}; exception type {ExceptionType}. Raw publication text is not logged.",
                work.BookId,
                exception.GetType().Name);
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }
}

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
        var states = new List<BookTextIngestionState>(ids.Count);
        foreach (var id in ids)
        {
            var state = await index.GetStateAsync(id, ct);
            if (state is not null) states.Add(state);
        }

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

        foreach (var hit in hits)
        {
            var chunk = hit.Chunk;
            AddSelected(chunk);

            if (radius > 0 && selected.Count < maxPassages * 3)
            {
                var neighbors = await index.GetNeighborsAsync(
                    chunk.BookId,
                    chunk.SourceSha256,
                    chunk.ExtractorVersion,
                    chunk.Ordinal,
                    radius,
                    ct);

                foreach (var neighbor in neighbors
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

    private async Task<List<BookDto>> ResolveScopeAsync(
        BookTextSearchRequest request,
        CancellationToken ct)
    {
        if (request.BookIds is { Count: > 0 })
        {
            var books = new List<BookDto>();
            foreach (var id in request.BookIds.Distinct().Take(32))
            {
                var result = await library.GetBookAsync(id, ct);
                if (result.Data is BookDto book)
                    books.Add(book);
            }
            return books;
        }

        var page = 1;
        const int pageSize = 100;
        var resultBooks = new List<BookDto>();
        while (resultBooks.Count < 500)
        {
            var result = await library.ListBooksAsync(
                BookFilter.All,
                BookSort.Title,
                search: null,
                page,
                pageSize,
                request.CollectionId,
                groupByWork: false,
                format: null,
                ct);

            if (result.Data is not PaginatedResponse<BookDto> batch)
                break;

            var items = batch.Items.ToList();
            resultBooks.AddRange(items);
            if (items.Count == 0 || resultBooks.Count >= batch.TotalCount)
                break;
            page++;
        }

        return resultBooks;
    }
}

public static class BookTextChunker
{
    public static IReadOnlyList<BookTextIndexedChunk> Chunk(
        BookTextSourceRevision revision,
        IReadOnlyList<BookTextArtifactBlock> blocks,
        int targetChars,
        int maxChars,
        int overlapChars)
    {
        targetChars = Math.Max(256, targetChars);
        maxChars = Math.Max(targetChars, maxChars);
        overlapChars = Math.Clamp(overlapChars, 0, targetChars / 2);

        var chunks = new List<BookTextIndexedChunk>();
        var currentText = new StringBuilder();
        var currentSegments = new List<BookTextSourceSegment>();
        IReadOnlyList<string> currentHeadings = [];

        void Flush()
        {
            var text = currentText.ToString().Trim();
            if (text.Length == 0)
            {
                currentText.Clear();
                currentSegments.Clear();
                return;
            }

            var ordinal = chunks.Count;
            chunks.Add(new BookTextIndexedChunk(
                BookTextIdentity.ChunkId(revision, ordinal),
                revision.BookId,
                revision.SourceSha256,
                revision.ExtractorVersion,
                revision.Format,
                ordinal,
                text,
                currentHeadings.ToArray(),
                currentSegments.ToArray()));

            currentText.Clear();
            currentSegments.Clear();
        }

        foreach (var block in blocks.OrderBy(block => block.Order))
        {
            var normalized = BookTextNormalization.Normalize(block.Text);
            if (normalized.Length == 0)
                continue;

            var pieces = SplitLongBlock(normalized, maxChars, overlapChars);
            foreach (var piece in pieces)
            {
                if (currentText.Length > 0
                    && (!currentHeadings.SequenceEqual(block.HeadingPath)
                        || currentText.Length + 2 + piece.Text.Length > targetChars))
                {
                    Flush();
                }

                if (currentText.Length > 0)
                    currentText.Append("\n\n");

                var baseOffset = currentText.Length;
                currentText.Append(piece.Text);
                currentHeadings = block.HeadingPath;

                var pieceStart = piece.SourceStart;
                var pieceEnd = piece.SourceStart + piece.SourceLength;
                foreach (var segment in block.SourceSegments)
                {
                    var segmentStart = segment.TextStart;
                    var segmentEnd = segment.TextStart + segment.TextLength;
                    var overlapStart = Math.Max(pieceStart, segmentStart);
                    var overlapEnd = Math.Min(pieceEnd, segmentEnd);
                    if (overlapStart >= overlapEnd)
                        continue;

                    var overlapLength = overlapEnd - overlapStart;
                    var sourceDelta = overlapStart - segmentStart;
                    currentSegments.Add(new BookTextSourceSegment(
                        TextStart: baseOffset + overlapStart - pieceStart,
                        TextLength: overlapLength,
                        Locator: SliceLocator(segment.Locator, sourceDelta, overlapLength)));
                }

                if (currentText.Length >= maxChars)
                    Flush();
            }
        }

        Flush();
        return chunks;
    }

    private static IReadOnlyList<(string Text, int SourceStart, int SourceLength)> SplitLongBlock(
        string text,
        int maxChars,
        int overlapChars)
    {
        if (text.Length <= maxChars)
            return [(text, 0, text.Length)];

        var result = new List<(string Text, int SourceStart, int SourceLength)>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            var take = Math.Min(maxChars, remaining);
            if (take < remaining)
            {
                var window = text.AsSpan(start, take);
                var lastBreak = window.LastIndexOfAny(' ', '\n', '\t');
                if (lastBreak > maxChars / 2)
                    take = lastBreak;
            }

            var raw = text.Substring(start, take);
            var leading = 0;
            while (leading < raw.Length && char.IsWhiteSpace(raw[leading])) leading++;
            var trailing = raw.Length;
            while (trailing > leading && char.IsWhiteSpace(raw[trailing - 1])) trailing--;

            if (trailing > leading)
            {
                var pieceStart = start + leading;
                var pieceLength = trailing - leading;
                result.Add((raw.Substring(leading, pieceLength), pieceStart, pieceLength));
            }

            if (start + take >= text.Length)
                break;

            start += Math.Max(1, take - overlapChars);
        }

        return result;
    }

    private static BookTextSourceLocator SliceLocator(
        BookTextSourceLocator locator,
        int sourceDelta,
        int length) =>
        locator switch
        {
            PdfBookTextSourceLocator pdf => pdf with
            {
                StartTextOffset = pdf.StartTextOffset is { } start ? start + sourceDelta : null,
                EndTextOffset = pdf.StartTextOffset is { } pdfStart
                    ? pdfStart + sourceDelta + length
                    : pdf.EndTextOffset,
            },
            EpubBookTextSourceLocator epub => epub with
            {
                StartTextOffset = epub.StartTextOffset is { } start ? start + sourceDelta : null,
                EndTextOffset = epub.StartTextOffset is { } epubStart
                    ? epubStart + sourceDelta + length
                    : epub.EndTextOffset,
            },
            _ => locator,
        };
}

public static class BookTextIdentity
{
    public static Guid ChunkId(BookTextSourceRevision revision, int ordinal)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{revision.BookId:D}\0{revision.SourceSha256}\0{revision.ExtractorVersion}\0{ordinal}");
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }
}

public static class BookTextNormalization
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (ch == '\n')
            {
                while (builder.Length > 0 && builder[^1] == ' ')
                    builder.Length--;
                if (builder.Length > 0 && builder[^1] != '\n')
                    builder.Append('\n');
                pendingSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0 && builder[^1] != '\n')
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}

public static class BookTextArtifactCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        Stream destination,
        BookTextSourceRevision revision,
        IReadOnlyList<BookTextArtifactBlock> blocks,
        CancellationToken ct = default)
    {
        await using var gzip = new System.IO.Compression.GZipStream(
            destination,
            System.IO.Compression.CompressionLevel.Optimal,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            gzip,
            new UTF8Encoding(false),
            64 * 1024,
            leaveOpen: true);

        await writer.WriteLineAsync(
            JsonSerializer.Serialize<BookTextArtifactRecord>(
                new BookTextArtifactManifest(BookTextArtifactSchema.CurrentVersion, revision),
                Json).AsMemory(),
            ct);

        foreach (var block in blocks.OrderBy(block => block.Order))
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize<BookTextArtifactRecord>(block, Json).AsMemory(),
                ct);
        }

        await writer.FlushAsync(ct);
    }
}

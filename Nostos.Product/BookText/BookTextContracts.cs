using System.Text.Json.Serialization;

namespace Nostos.Product.BookText;

/// <summary>
/// Stable wire/storage identifiers for the regeneratable book-text layer.
///
/// The original publication remains authoritative. These values version only
/// the derived representation that can be rebuilt from that publication.
/// </summary>
public static class BookTextArtifactSchema
{
    public const int CurrentVersion = 1;
    // v3: PDF pages are composed from placed words in reading order
    // (PdfReadingOrder) instead of the raw glyph stream. Every stored
    // extraction is rebuilt, and with it the index and embeddings.
    public const string CurrentExtractorVersion = "nostos-book-text-v3";
    public const string SuggestedFileName = "extraction-v1.jsonl.gz";
}

/// <summary>
/// Source formats the locator contract can represent. Audio is reserved for a
/// future transcript implementation; #468 v1 extraction supports PDF and EPUB.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookTextSourceFormat
{
    Pdf,
    Epub,
    Audio,
}

/// <summary>
/// Identity of one exact imported source revision.
///
/// Title, ISBN, author, filename and other bibliographic metadata are
/// deliberately absent: derived state is reusable only for the exact source
/// bytes and extractor version that produced it.
/// </summary>
public sealed record BookTextSourceRevision
{
    public BookTextSourceRevision(
        Guid bookId,
        string sourceSha256,
        string extractorVersion,
        BookTextSourceFormat format)
    {
        if (bookId == Guid.Empty)
            throw new ArgumentException("A source revision requires a book id.", nameof(bookId));

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        var normalizedHash = sourceSha256.Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException(
                "SourceSha256 must be exactly 64 hexadecimal characters.",
                nameof(sourceSha256));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(extractorVersion);

        BookId = bookId;
        SourceSha256 = normalizedHash;
        ExtractorVersion = extractorVersion.Trim();
        Format = format;
    }

    public Guid BookId { get; init; }
    public string SourceSha256 { get; init; }
    public string ExtractorVersion { get; init; }
    public BookTextSourceFormat Format { get; init; }
}

/// <summary>
/// A typed pointer back into the authoritative imported publication.
///
/// Locators are serialized with an explicit discriminator so artifact/index
/// records never need to infer their meaning from optional fields.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PdfBookTextSourceLocator), "pdf")]
[JsonDerivedType(typeof(EpubBookTextSourceLocator), "epub")]
[JsonDerivedType(typeof(AudioBookTextSourceLocator), "audio")]
public abstract record BookTextSourceLocator;

/// <summary>
/// PDF provenance.
///
/// <see cref="PageIndex"/> is the zero-based physical page index in the exact
/// source PDF. <see cref="PageLabel"/> is separate because printed/PDF labels
/// such as "xii" or "1" are not interchangeable with physical page position.
/// Text offsets, when present, are offsets in the extractor's normalized text
/// for that physical page.
/// </summary>
public sealed record PdfBookTextSourceLocator(
    int PageIndex,
    string? PageLabel = null,
    int? StartTextOffset = null,
    int? EndTextOffset = null) : BookTextSourceLocator;

/// <summary>
/// EPUB provenance.
///
/// epub.js CFI is preferred when available because the current Nostos reader can
/// pass it directly to rendition.display(). ResourceHref is the OPF/package-relative
/// manifest href used by epub.js's spine, not the archive-root path used to read
/// the ZIP entry. Resource href + spine index remain mandatory structural
/// fallbacks. Text offsets, when present, are offsets in the normalized visible
/// text of that spine resource, never fake page numbers.
/// </summary>
public sealed record EpubBookTextSourceLocator(
    int SpineIndex,
    string ResourceHref,
    string? Cfi = null,
    int? StartTextOffset = null,
    int? EndTextOffset = null) : BookTextSourceLocator;

/// <summary>
/// Reserved forward-compatible locator for a future supplied/on-demand audio
/// transcript. Phase 1 does not introduce audiobook transcription.
/// </summary>
public sealed record AudioBookTextSourceLocator(
    long StartMs,
    long EndMs) : BookTextSourceLocator;

/// <summary>
/// Maps a range of one derived block back to one exact source location.
///
/// A block that crosses PDF pages or EPUB resources carries multiple segments
/// instead of collapsing its provenance to one ambiguous location.
/// </summary>
public sealed record BookTextSourceSegment(
    int TextStart,
    int TextLength,
    BookTextSourceLocator Locator);

/// <summary>
/// One record in the line-delimited derived artifact. The first record is a
/// manifest; subsequent records are ordered blocks.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "recordType")]
[JsonDerivedType(typeof(BookTextArtifactManifest), "manifest")]
[JsonDerivedType(typeof(BookTextArtifactBlock), "block")]
public abstract record BookTextArtifactRecord;

/// <summary>
/// First JSONL record. Schema version and exact source revision make a derived
/// artifact independently auditable and safely disposable/rebuildable.
/// </summary>
public sealed record BookTextArtifactManifest(
    int SchemaVersion,
    BookTextSourceRevision Source) : BookTextArtifactRecord;

/// <summary>
/// A structure-preserving extracted text block.
///
/// <see cref="HeadingPath"/> contains chapter/section ancestry when the source
/// exposes it. <see cref="SourceSegments"/> is never replaced by a synthetic
/// page/location field; every displayed citation must ultimately come from one
/// of these source-backed segments.
/// </summary>
public sealed record BookTextArtifactBlock(
    int Order,
    string Text,
    IReadOnlyList<string> HeadingPath,
    IReadOnlyList<BookTextSourceSegment> SourceSegments) : BookTextArtifactRecord;

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

/// <summary>One search hit whose neighbouring chunks are wanted.</summary>
public sealed record BookTextNeighborQuery(
    Guid BookId,
    string SourceSha256,
    string ExtractorVersion,
    int Ordinal);

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

    /// <summary>
    /// The state of every listed book that has one, in one read. The default
    /// asks <see cref="GetStateAsync"/> per book, so an index that does not
    /// override it behaves as before; absent books are simply not returned.
    /// Result order follows <paramref name="bookIds"/>.
    /// </summary>
    async Task<IReadOnlyList<BookTextIngestionState>> GetStatesAsync(
        IReadOnlyList<Guid> bookIds,
        CancellationToken ct = default)
    {
        var states = new List<BookTextIngestionState>(bookIds.Count);
        foreach (var id in bookIds)
        {
            var state = await GetStateAsync(id, ct);
            if (state is not null) states.Add(state);
        }
        return states;
    }

    /// <summary>
    /// Every chunk within <paramref name="radius"/> ordinals of any listed
    /// chunk (the listed chunks included when they exist), same revision only,
    /// as a flat list in no particular order. The default asks
    /// <see cref="GetNeighborsAsync"/> per item and removes duplicates.
    /// </summary>
    async Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsManyAsync(
        IReadOnlyList<BookTextNeighborQuery> queries,
        int radius,
        CancellationToken ct = default)
    {
        var chunks = new Dictionary<Guid, BookTextIndexedChunk>();
        foreach (var query in queries)
        {
            var neighbors = await GetNeighborsAsync(
                query.BookId,
                query.SourceSha256,
                query.ExtractorVersion,
                query.Ordinal,
                radius,
                ct);
            foreach (var neighbor in neighbors)
                chunks.TryAdd(neighbor.Id, neighbor);
        }
        return chunks.Values.ToList();
    }
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

public sealed class BookTextUnsupportedException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

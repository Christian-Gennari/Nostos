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
    public const string CurrentExtractorVersion = "nostos-book-text-v2";
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

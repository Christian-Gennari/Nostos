using System.Text.Json.Serialization;

namespace Nostos.Backend.Services.Portability;

// The archive's JSON keys (concepts / noteConcepts / concept / conceptId) are an
// on-disk interchange format: they stay as written so archives exported before the
// Concepts -> Topics rename still import. Only the C# names changed.

internal static class PortableArchiveFormat
{
    public const string Name = "nostos-portable";
    public const int Version = 1;
    // Bumped from 1 to 2 with WritingNotes (issue #491), and from 2 to 3
    // with remembered e-reader book mappings (NoteImportBookLink, issue #677).
    // Bumped from 3 to 4 with multi-track audiobooks (issue #835): a book may
    // carry a track list and "track" media. An older build must refuse such
    // an archive rather than import the book without its audio.
    // Archives with DataVersion 1, 2 and 3 remain supported.
    public const int DataVersion = 4;
    public const string ManifestPath = "manifest.json";
    public const string DataPath = "data/library.json";
    public const string BookMediaKind = "book";
    public const string CoverMediaKind = "cover";

    /// <summary>
    /// One track of a multi-track audiobook. Unlike the other kinds a book may
    /// have many, so a track is identified by its canonical file name
    /// (<c>track-0001.mp3</c>) as well as its book.
    /// </summary>
    public const string TrackMediaKind = "track";

    public static bool IsKnownMediaKind(string kind) =>
        kind is BookMediaKind or CoverMediaKind or TrackMediaKind;

    /// <summary>
    /// The archive path of a media item. Every kind lives directly in its
    /// book's folder under its canonical file name.
    /// </summary>
    public static string MediaPath(Guid bookId, string canonicalFileName) =>
        $"media/books/{bookId:N}/{canonicalFileName.ToLowerInvariant()}";

    /// <summary>
    /// What makes a media item unique within an archive: one book file and one
    /// cover per book, and one track per canonical name.
    /// </summary>
    public static (Guid BookId, string Kind, string Discriminator) MediaKey(
        Guid bookId, string kind, string fileName) =>
        (bookId, kind, kind == TrackMediaKind ? fileName.ToLowerInvariant() : string.Empty);

    /// <summary>
    /// The canonical stored name for a media item, or null when the name is not
    /// valid for its kind (unknown kind, unsupported extension, malformed track
    /// name).
    /// </summary>
    public static string? CanonicalMediaFileName(string kind, string fileName)
    {
        try
        {
            switch (kind)
            {
                case BookMediaKind:
                    return BookMediaKind + BookAssetFormats.RequireBookExtension(fileName);
                case CoverMediaKind:
                    return CoverMediaKind + BookAssetFormats.RequireCoverExtension(fileName);
                case TrackMediaKind:
                    return BookTrackFormats.TryParseCanonicalFileName(fileName.ToLowerInvariant(), out var number)
                        ? BookTrackFormats.CanonicalFileName(number, Path.GetExtension(fileName))
                        : null;
                default:
                    return null;
            }
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

internal sealed record PortableLibraryData(
    int Version,
    List<PortableWork> Works,
    List<PortableBook> Books,
    List<PortableCollection> Collections,
    List<PortableBookCollection> BookCollections,
    List<PortableNote> Notes,
    [property: JsonPropertyName("concepts")] List<PortableTopic> Topics,
    [property: JsonPropertyName("noteConcepts")] List<PortableNoteTopic> NoteTopics,
    List<PortableWriting> Writings,
    List<PortableBookAcquisition> BookAcquisitions,
    PortableAssistantSettings? AssistantSettings,
    List<PortableWritingNote>? WritingNotes = null,
    List<PortableNoteImportBookLink>? NoteImportBookLinks = null);

internal sealed record PortableWork(
    Guid Id,
    string Title,
    string? Author,
    DateTime CreatedAt);

internal sealed record PortableBook(
    Guid Id,
    Guid WorkId,
    string Type,
    string Status,
    string? StatusMessage,
    string Title,
    string? Author,
    PortableBookMetadata Metadata,
    PortableReadingProgress Progress,
    DateTime CreatedAt,
    string? Isbn,
    int? PageCount,
    string? Asin,
    string? Duration,
    string? Narrator,
    string? ChaptersJson,
    // True only for a single primary file. A multi-track audiobook has none:
    // its media is TracksJson plus one "track" media item per track.
    bool HasBookFile,
    bool HasCover,
    // The track list of a multi-track audiobook (data version 4+). Appended
    // and optional so archives written before it existed still read.
    string? TracksJson = null);

internal sealed record PortableBookMetadata(
    string? Subtitle,
    string? Description,
    string? Editor,
    string? Translator,
    string? Publisher,
    string? PlaceOfPublication,
    string? PublishedDate,
    string? Language,
    string? Categories,
    string? Edition,
    string? Series,
    string? VolumeNumber);

internal sealed record PortableReadingProgress(
    string? LastLocation,
    int ProgressPercent,
    int Rating,
    bool IsFavorite,
    string? PersonalReview,
    DateTime? LastReadAt,
    DateTime? FinishedAt);

internal sealed record PortableCollection(
    Guid Id,
    string Name,
    Guid? ParentId);

internal sealed record PortableBookCollection(
    Guid BookId,
    Guid CollectionId,
    DateTime AddedAt);

internal sealed record PortableNote(
    Guid Id,
    string Content,
    string? CfiRange,
    string? SelectedText,
    DateTime CreatedAt,
    Guid BookId,
    string? RawContent,
    string CaptureSource,
    string ProcessingMode,
    string SourceAnchorKind,
    string? SourceAnchorValue,
    bool AnchorVerified);

internal sealed record PortableTopic(
    Guid Id,
    [property: JsonPropertyName("concept")] string Topic);

internal sealed record PortableNoteTopic(
    Guid NoteId,
    [property: JsonPropertyName("conceptId")] Guid TopicId);

internal sealed record PortableWriting(
    Guid Id,
    string Name,
    string Type,
    string? Content,
    Guid? ParentId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

internal sealed record PortableWritingNote(
    Guid WritingId,
    Guid NoteId,
    DateTime AddedAt);

internal sealed record PortableNoteImportBookLink(
    Guid Id,
    string Source,
    string SourceKey,
    Guid BookId,
    DateTime CreatedAtUtc);

internal sealed record PortableBookAcquisition(
    Guid Id,
    Guid BookId,
    string ProviderId,
    string ProviderDisplayName,
    string ExternalId,
    string AssetId,
    string? AssetFormat,
    string? ImportedExtension,
    string? SourceUrl,
    string? RightsStatement,
    DateTime AcquiredAt);

internal sealed record PortableAssistantSettings(
    string? CaptureProcessingMode,
    DateTime UpdatedAtUtc);

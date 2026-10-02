namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// One KoboReader.sqlite covers the whole device, so the report is a list of
/// books rather than the single-book shape a KOReader sidecar produces.
/// </summary>
public sealed record KoboImportReport(
    int BookCount,
    int MatchedBookCount,
    int AnnotationCount,
    int ImportedCount,
    int DuplicateCount,
    int SkippedCount,
    IReadOnlyList<KoboImportBookReport> Books);

public sealed record KoboImportBookReport(
    string Status,
    Guid? BookId,
    string? BookTitle,
    string? SourceTitle,
    string? SourceAuthor,
    int AnnotationCount,
    int ImportedCount,
    int DuplicateCount,
    int SkippedCount,
    string? Message);

internal sealed record KoboVolume(
    string VolumeId,
    string? Title,
    string? Author,
    string? Isbn,
    IReadOnlyList<KoboBookmark> Bookmarks);

internal sealed record KoboBookmark(
    string BookmarkId,
    string? Text,
    string? Annotation);

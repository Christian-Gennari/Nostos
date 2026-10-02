namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// What an e-reader file holds and where each of its books would go, before
/// anything is written. `Match` is how sure the pairing is:
/// <c>exact</c> (same ISBN, or same title and author) and <c>remembered</c>
/// (the owner confirmed it on an earlier import) are safe to import as they
/// stand; <c>suggested</c> is a resemblance the owner must confirm;
/// <c>none</c> has no plausible library book.
/// </summary>
public sealed record HighlightImportPreview(
    string Source,
    IReadOnlyList<HighlightImportPreviewBook> Books);

public sealed record HighlightImportPreviewBook(
    string SourceKey,
    string? Title,
    string? Author,
    int AnnotationCount,
    int NewCount,
    string Match,
    Guid? BookId,
    IReadOnlyList<HighlightImportCandidate> Candidates);

public sealed record HighlightImportCandidate(
    Guid BookId,
    string Title,
    string? Author,
    string Reason);

/// <summary>
/// The owner's answer for one book in the file. A book with no decision is
/// left out. <c>Create</c> adds the book to the library from the device's
/// title and author first.
/// </summary>
public sealed record HighlightImportDecision(
    string SourceKey,
    Guid? BookId = null,
    bool Create = false);

public sealed record HighlightImportResult(
    Guid? BatchId,
    string Source,
    IReadOnlyList<HighlightImportResultBook> Books);

public sealed record HighlightImportResultBook(
    string SourceKey,
    string Status,
    Guid? BookId,
    string? BookTitle,
    string? SourceTitle,
    string? SourceAuthor,
    int AnnotationCount,
    int ImportedCount,
    int DuplicateCount,
    bool Created,
    string? Message);

public sealed record HighlightImportBatchSummary(
    Guid Id,
    string Source,
    string? FileName,
    DateTime CreatedAtUtc,
    int NoteCount,
    int BookCount);

internal sealed record ImportSourceBook(
    string SourceKey,
    string? Title,
    string? Author,
    string? Isbn,
    IReadOnlyList<ImportAnnotation> Annotations);

internal sealed record ImportAnnotation(
    string AnchorKind,
    string? AnchorValue,
    string? Text,
    string Note);

internal sealed record ImportLibraryBook(
    Guid Id,
    string Title,
    string? Author,
    string? NormalizedIsbn);

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// What an e-reader or export file holds and where each of its books would go, before
/// anything is written. `Match` is how sure the pairing is:
/// <c>exact</c> (same ISBN, or same title and author) and <c>remembered</c>
/// (the owner confirmed it on an earlier import) are safe to import as they
/// stand; <c>suggested</c> is a resemblance the owner must confirm;
/// <c>none</c> has no plausible library book.
/// </summary>
/// <param name="Skipped">
/// What the file holds that cannot be imported, by book and reason: a Kindle
/// bookmark, a Readwise web article, an entry in a format Nostos does not read.
/// </param>
public sealed record HighlightImportPreview(
    string Source,
    IReadOnlyList<HighlightImportPreviewBook> Books,
    IReadOnlyList<HighlightImportSkipped>? Skipped = null);

public sealed record HighlightImportSkipped(
    string? Title,
    string? Author,
    int Count,
    string Reason);

/// <param name="Notice">
/// Something the owner should know before choosing, such as a Readwise row
/// that looks like a web article rather than a book.
/// </param>
public sealed record HighlightImportPreviewBook(
    string SourceKey,
    string? Title,
    string? Author,
    int AnnotationCount,
    int NewCount,
    string Match,
    Guid? BookId,
    IReadOnlyList<HighlightImportCandidate> Candidates,
    string? Notice = null,
    IReadOnlyList<HighlightImportPreviewHighlight>? Highlights = null);

/// <summary>
/// One highlight of a book in the file, cut short for the review step. <c>Index</c>
/// is its place in the book, which a decision names to leave it out; <c>IsNew</c>
/// is false for one Nostos already holds (for the book proposed first).
/// </summary>
public sealed record HighlightImportPreviewHighlight(
    int Index,
    string? Text,
    string? Note,
    bool IsNew);

/// <param name="Format">
/// The library book's format, as the app's format concepts name it:
/// <c>audiobook</c>, <c>ebook</c>, <c>pdf</c> or <c>physical</c>. The review
/// step shows it beside each book to pick from, as the Editions dialog does.
/// </param>
public sealed record HighlightImportCandidate(
    Guid BookId,
    string Title,
    string? Author,
    string Reason,
    string Format);

/// <summary>
/// The owner's answer for one book in the file. A book with no decision is
/// left out. <c>Create</c> adds the book to the library from the device's
/// title and author first. <c>ExcludedHighlights</c> are the indexes of the
/// highlights the owner unticked in the review.
/// </summary>
public sealed record HighlightImportDecision(
    string SourceKey,
    Guid? BookId = null,
    bool Create = false,
    IReadOnlyList<int>? ExcludedHighlights = null);

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
    IReadOnlyList<ImportAnnotation> Annotations,
    string? Notice = null);

internal sealed record ImportSourceFile(
    string Source,
    IReadOnlyList<ImportSourceBook> Books,
    IReadOnlyList<HighlightImportSkipped> Skipped)
{
    /// <summary>
    /// "table.column" for each optional column the file did not have, in the
    /// order the reader asked for it. Empty when the file has everything the
    /// readers know; the import behaves the same either way (issue #1523).
    /// </summary>
    public IReadOnlyList<string> MissingOptionalColumns { get; init; } = [];
}

internal sealed record ImportAnnotation(
    string AnchorKind,
    string? AnchorValue,
    string? Text,
    string Note,
    // Shapes an earlier version of the import could have stored this in.
    IReadOnlyList<ImportedNoteShape>? EarlierShapes = null,
    // When the source recorded the highlight (UTC); null => the note gets the import time.
    DateTime? CreatedAt = null);

internal sealed record ImportLibraryBook(
    Guid Id,
    string Title,
    string? Author,
    string? NormalizedIsbn,
    string Format = HighlightImportBookFormat.Ebook);

/// <summary>The format names a <see cref="HighlightImportCandidate"/> carries.</summary>
internal static class HighlightImportBookFormat
{
    public const string Audiobook = "audiobook";
    public const string Ebook = "ebook";
    public const string Pdf = "pdf";
    public const string Physical = "physical";

    public static string Of(bool isAudiobook, bool isPhysical, string? fileName) =>
        isAudiobook ? Audiobook
        : isPhysical ? Physical
        : fileName is not null && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? Pdf
        : Ebook;
}

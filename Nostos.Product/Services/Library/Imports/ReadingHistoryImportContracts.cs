using Nostos.Backend.Services.Notes.Imports;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// A Goodreads or StoryGraph export read before anything is written (issue
/// #1521): every row with the library book it would land in and the values it
/// would set. <c>Match</c> means what it does for the highlight import:
/// <c>exact</c> (same ISBN, or same title and author) is ready; <c>suggested</c>
/// is a resemblance the owner must confirm; <c>none</c> has no plausible book.
/// </summary>
public sealed record ReadingHistoryImportPreview(
    string Source,
    IReadOnlyList<ReadingHistoryPreviewBook> Books,
    IReadOnlyList<ReadingHistorySkipped>? Skipped = null);

public sealed record ReadingHistorySkipped(
    string? Title,
    string? Author,
    int Count,
    string Reason);

/// <param name="Values">
/// What the row holds, as it would be stored: a rating is 0 when the row has
/// none, a review and private notes are null when empty.
/// </param>
/// <param name="HasChanges">
/// False when every value the row holds is already in the library, which is
/// what makes importing the same file again a no-op.
/// </param>
public sealed record ReadingHistoryPreviewBook(
    string SourceKey,
    string? Title,
    string? Author,
    string? Isbn,
    string Match,
    Guid? BookId,
    IReadOnlyList<HighlightImportCandidate> Candidates,
    ReadingHistoryValues Values,
    bool HasChanges,
    string? ExistingTitle = null,
    string? Notice = null);

public sealed record ReadingHistoryValues(
    int Rating,
    string? Review,
    string? PrivateNotes,
    DateTime? FinishedAt,
    bool ToRead,
    string ReadStatus,
    int ReadCount,
    IReadOnlyList<string> Shelves,
    string? RatingNotice = null);

/// <summary>
/// The owner's answer for one row. A row with no decision is left out.
/// <c>Create</c> adds a paper book, or, for a to-read row, a reading-list entry.
/// <c>MapShelves</c> is the one switch that turns shelves into collections; it
/// is never assumed.
/// </summary>
public sealed record ReadingHistoryImportDecision(
    string SourceKey,
    Guid? BookId = null,
    bool Create = false,
    bool MapShelves = false);

public sealed record ReadingHistoryImportResult(
    Guid? BatchId,
    string Source,
    IReadOnlyList<ReadingHistoryImportResultBook> Books);

/// <param name="ChangeCount">How many values were written for this book.</param>
public sealed record ReadingHistoryImportResultBook(
    string SourceKey,
    string Status,
    Guid? BookId,
    string? BookTitle,
    string? SourceTitle,
    string? SourceAuthor,
    int ChangeCount,
    bool Created,
    string? Message);

public sealed record ReadingHistoryImportBatchSummary(
    Guid Id,
    string Source,
    string? FileName,
    DateTime CreatedAtUtc,
    int BookCount,
    int ChangeCount);

/// <summary>The reading status names a source row can carry, normalized.</summary>
internal static class ReadingHistoryStatus
{
    public const string Read = "read";
    public const string ToRead = "to-read";
    public const string CurrentlyReading = "currently-reading";
    public const string DidNotFinish = "did-not-finish";
    public const string Paused = "paused";
    public const string Unknown = "unknown";
}

internal sealed record ReadingHistorySourceBook(
    string SourceKey,
    string? Title,
    string? Author,
    string? Isbn10,
    string? Isbn13,
    int Rating,
    string? Review,
    string? PrivateNotes,
    DateTime? FinishedAt,
    string ReadStatus,
    int ReadCount,
    IReadOnlyList<string> Shelves,
    string? Notice = null)
{
    public bool ToRead => ReadStatus == ReadingHistoryStatus.ToRead;

    public string? Isbn => Isbn13 ?? Isbn10;
}

internal sealed record ReadingHistorySourceFile(
    string Source,
    IReadOnlyList<ReadingHistorySourceBook> Books,
    IReadOnlyList<ReadingHistorySkipped> Skipped);

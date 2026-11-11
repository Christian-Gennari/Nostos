using Nostos.Backend.Services.Notes.Imports;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// A Calibre library read before anything is written (issue #1527): every book
/// with the formats it holds, the existing library book it would join, and what
/// importing it would set. The review screen is the same shape as the other
/// imports: <c>exact</c> is the same ISBN or the same title and author,
/// <c>suggested</c> is a resemblance the owner must confirm, <c>none</c> is a
/// book the library does not have.
/// </summary>
public sealed record CalibreImportPreview(
    string Source,
    CalibrePreviewSummary Summary,
    IReadOnlyList<CalibrePreviewBook> Books,
    IReadOnlyList<CalibreSkipped> Skipped,
    IReadOnlyList<CalibreCustomColumn> CustomColumns,
    IReadOnlyList<string> Warnings);

public sealed record CalibrePreviewSummary(
    int Books,
    int Ready,
    int InLibrary,
    int AlreadyHere,
    int NeedsReview,
    int Skipped,
    int Formats,
    int UnsupportedFormats,
    bool Truncated);

/// <param name="SourceKey">Stable identity of the Calibre book in this library.</param>
/// <param name="ReadAt">The read date from a common custom column, or null.</param>
/// <param name="ReadNotice">Why a read flag could not become an exact read date.</param>
/// <param name="Match">The highlight import's vocabulary: exact, suggested or none.</param>
/// <param name="BookId">The matched library edition when <paramref name="Match"/> is exact.</param>
/// <param name="Formats">The supported files, with the owner's default choice.</param>
/// <param name="UnsupportedFormats">Files Nostos cannot read, listed so nothing is silent.</param>
public sealed record CalibrePreviewBook(
    string SourceKey,
    int CalibreId,
    string Title,
    string? Author,
    string? Isbn,
    string? Series,
    string? VolumeNumber,
    string? Publisher,
    string? PublishedDate,
    int Rating,
    string? Description,
    bool HasCover,
    IReadOnlyList<string> Tags,
    DateTime? ReadAt,
    string? ReadNotice,
    string Match,
    Guid? BookId,
    IReadOnlyList<HighlightImportCandidate> Candidates,
    IReadOnlyList<CalibreFormatChoice> Formats,
    IReadOnlyList<CalibreUnsupportedFormat> UnsupportedFormats,
    string? ExistingTitle,
    string? Notice);

/// <param name="Key">Stable identity of this file inside the Calibre library.</param>
/// <param name="AlreadyHere">
/// An edition of the matched work already holds a file of this format. The
/// format is shown, disabled, so a second import reports what it found.
/// </param>
/// <param name="SelectedByDefault">
/// The best format of a Calibre book is chosen; other formats are offered but
/// never brought in silently.
/// </param>
public sealed record CalibreFormatChoice(
    string Key,
    string Format,
    string FileName,
    long Bytes,
    string Kind,
    bool AlreadyHere,
    Guid? ExistingBookId,
    bool SelectedByDefault);

public sealed record CalibreUnsupportedFormat(string FileName, string Format, string Reason);

/// <summary>Books the preview could not offer, grouped by the reason.</summary>
public sealed record CalibreSkipped(string? Title, string? Author, int Count, string Reason);

/// <param name="Status"><c>used</c> for a read column, <c>skipped</c> otherwise.</param>
public sealed record CalibreCustomColumn(
    string Label,
    string Name,
    string DataType,
    string Status,
    string? Reason);

/// <summary>
/// The owner's answer for one Calibre book. <paramref name="Formats"/> names the
/// file keys to import; an empty list leaves the book out. Collections are only
/// created when <paramref name="CreateCollections"/> is true.
/// </summary>
public sealed record CalibreImportDecision(
    string SourceKey,
    IReadOnlyList<string>? Formats = null,
    Guid? BookId = null,
    bool CreateCollections = false);

public sealed record CalibreImportResult(
    Guid? BatchId,
    string Source,
    IReadOnlyList<CalibreImportResultBook> Books);

/// <param name="Status">imported, unchanged, skipped or failed.</param>
/// <param name="FileCount">How many formats were written for this book.</param>
public sealed record CalibreImportResultBook(
    string SourceKey,
    int CalibreId,
    string Title,
    string Status,
    Guid? BookId,
    string? BookTitle,
    int FileCount,
    bool Created,
    string? Message);

public sealed record CalibreImportBatchSummary(
    Guid Id,
    string Source,
    string? FileName,
    DateTime CreatedAtUtc,
    int BookCount,
    int ChangeCount);

public sealed record CalibreUndoResult(int Removed, int Kept);

/// <summary>Progress reported after each book of a running import.</summary>
public sealed record CalibreCommitProgress(
    int Completed,
    int Total,
    string? Current,
    Guid? BatchId,
    CalibreImportResultBook? LastResult);

/// <summary>
/// The state the Calibre import runs in, kept in memory like the external media
/// scan: the server owns the work, so closing the dialog loses nothing and a
/// reopened one picks the progress up. A restart loses the live progress, not
/// the books already written (each is in the batch).
/// </summary>
public sealed record CalibreImportJobState(
    string Status,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int Total,
    int Completed,
    string? Current,
    Guid? BatchId,
    IReadOnlyList<CalibreImportResultBook> Results,
    string? Error)
{
    public const string Idle = "idle";
    public const string Importing = "importing";
    public const string Done = "done";
    public const string Interrupted = "interrupted";
    public const string Failed = "failed";
}

/// <summary>
/// A Calibre library the import can read. The folder implementation lives in
/// the SelfHosted host; a Cloud source (a zipped library handed over through the
/// library transfer) plugs in behind the same seam later (see #1527).
/// </summary>
public interface ICalibreLibrarySource
{
    /// <summary>A stable name for the source, stored on the batch.</summary>
    string Kind { get; }

    /// <summary>A library is named in configuration, whether or not it is reachable.</summary>
    bool IsConfigured { get; }

    /// <summary>The library and its metadata.db are readable right now.</summary>
    bool IsAvailable { get; }

    Task<CalibreSourceLibrary> ReadAsync(CancellationToken ct);

    /// <summary>Opens one book file for reading, or null when it is gone.</summary>
    Stream? OpenRead(CalibreSourceFormat format);

    /// <summary>Opens the library's cover for a book, or null when it has none.</summary>
    Stream? OpenCover(CalibreSourceBook book);

    /// <summary>The cover's file extension with the dot, for storage naming.</summary>
    string? CoverExtension { get; }
}

/// <summary>The capability seam: Settings offers the feature when a source is registered.</summary>
public interface ICalibreLibraryAvailability
{
    bool IsConfigured { get; }
    bool IsAvailable { get; }
}

/// <summary>
/// A library read by a source. <paramref name="Skipped"/> carries the books the
/// source could not offer (no folder, missing files); the preview adds the ones
/// whose formats Nostos cannot read.
/// </summary>
public sealed record CalibreSourceLibrary(
    IReadOnlyList<CalibreSourceBook> Books,
    IReadOnlyList<CalibreSkipped> Skipped,
    IReadOnlyList<CalibreCustomColumn> CustomColumns,
    int TotalBooks,
    bool Truncated,
    IReadOnlyList<string> Warnings);

/// <param name="Read">The value of a common read column, when the library has one.</param>
/// <param name="ReadAt">The value of a common date-read column, when it has one.</param>
/// <param name="Formats">Every file the library records, supported or not.</param>
public sealed record CalibreSourceBook(
    int Id,
    string Title,
    string? Author,
    string? Isbn,
    string? Series,
    string? VolumeNumber,
    string? Publisher,
    string? PublishedDate,
    int Rating,
    string? Description,
    IReadOnlyList<string> Tags,
    bool? Read,
    DateTime? ReadAt,
    IReadOnlyList<CalibreSourceFormat> Formats,
    string? CoverPath,
    IReadOnlyList<string> Notices);

/// <param name="Format">Upper-case Calibre format name: EPUB, PDF, M4B, M4A, MP3.</param>
/// <param name="Kind">ebook or audiobook, as the library names the two.</param>
/// <param name="Path">The source's own locator. Never leaves the server.</param>
/// <param name="Readable">The file exists and passes the same check an upload would.</param>
public sealed record CalibreSourceFormat(
    string Key,
    string Format,
    string FileName,
    long Bytes,
    string Kind,
    string Path,
    bool Readable,
    string? Problem);

/// <summary>A Calibre library that cannot be read, with a message safe to show.</summary>
public sealed class CalibreLibraryException(string message) : Exception(message);

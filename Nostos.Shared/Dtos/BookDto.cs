using System;
using System.Collections.Generic;

namespace Nostos.Shared.Dtos;

public record PaginatedResponse<T>(IEnumerable<T> Items, int TotalCount, int Page, int PageSize);

public record LibraryStatusCountsDto(
    int All,
    int NotStarted,
    int Reading,
    int Favorites,
    int Finished,
    int Unsorted,
    int Audiobooks = 0,
    int Ebooks = 0,
    int Pdfs = 0);

public record BookChapterDto(string Title, double StartTime);

// Separate DTO for the heavy locations JSON to avoid bloating the main list
public record BookLocationsDto(string Locations);

public record EditionSummaryDto(
    Guid Id,
    string Type,
    string? Format,
    int ProgressPercent,
    DateTime? FinishedAt,
    DateTime? LastReadAt,
    bool HasFile,
    string? FileName,
    string? Narrator,
    string? Duration,
    string? Edition,
    // APPENDED (positional record, so inserting renumbers every existing
    // client's JSON). The multi-edition management UI has to NAME the book it
    // will detach, and a summary that carries only format metadata cannot: it
    // left the unlink row showing the current book's own title for its sibling.
    string Title = "",
    string? Author = null
);

public record BookDto(
    string Type,
    Guid Id,
    string Title,
    string? Subtitle,
    string? Author,
    string? Editor,
    string? Translator,
    string? Narrator,
    string? Description,
    string? Isbn,
    string? Asin,
    string? Duration,
    int? PageCount,
    string? Publisher,
    string? PlaceOfPublication,
    string? PublishedDate,
    string? Edition,
    string? Language,
    string? Categories,
    string? Series,
    string? VolumeNumber,
    DateTime CreatedAt,
    bool HasFile,
    string? FileName,
    string? CoverUrl,
    string? LastLocation,
    int ProgressPercent,
    int Rating,
    bool IsFavorite,
    string? PersonalReview,
    DateTime? LastReadAt,
    DateTime? FinishedAt,
    IEnumerable<BookChapterDto>? Chapters,
    Guid? WorkId = null,
    int EditionCount = 1,
    IEnumerable<EditionSummaryDto>? OtherEditions = null,
    // Multi-collection membership. APPENDED deliberately: BookDto is a
    // positional record, so inserting a field would renumber the JSON of every
    // existing client. This is the sole representation of membership.
    IEnumerable<Guid>? CollectionIds = null,
    // Provenance for a book whose file came from an external provider.
    // APPENDED for the same reason as CollectionIds above. Deliberately small:
    // this is where a file came from, not a licensing surface.
    BookSourceDto? Source = null,
    // Life-cycle status for create-on-confirm imports. Ready = 0, Downloading = 1,
    // Transcoding = 2, Failed = 3, UploadPending = 4. APPENDED to preserve positional record stability.
    Nostos.Shared.Enums.BookStatus Status = Nostos.Shared.Enums.BookStatus.Ready,
    string? StatusMessage = null,
    // The ordered tracks of a multi-track audiobook; null for a single-file
    // book. Such a book reports HasFile = true with no FileName: the tracks are
    // its media. APPENDED to preserve positional record stability.
    IEnumerable<BookTrackDto>? Tracks = null
);

/// <summary>
/// One track of a multi-track audiobook. <paramref name="Duration"/> is in
/// seconds; positions elsewhere (progress, chapters) stay in seconds across the
/// whole book, and a client maps them onto tracks with these durations.
/// </summary>
public sealed record BookTrackDto(
    int Number,
    string Title,
    double Duration,
    long Bytes,
    string ContentType
);

/// <summary>
/// Compact "where did this file come from" indication for an acquired book.
/// The rights field carries the SOURCE's own wording and nothing more — Nostos
/// does not turn it into a claim that the work is unrestricted everywhere.
/// </summary>
public sealed record BookSourceDto(
    string ProviderId,
    string ProviderDisplayName,
    string ExternalId,
    string? SourceUrl,
    string? AssetFormat,
    string? RightsStatement,
    DateTime AcquiredAt
);

public record CreateBookDto(
    string Type,
    string Title,
    string? Subtitle,
    string? Author,
    string? Editor,
    string? Translator,
    string? Narrator,
    string? Description,
    string? Isbn,
    string? Asin,
    string? Duration,
    string? Publisher,
    string? PlaceOfPublication,
    string? PublishedDate,
    string? Edition,
    int? PageCount,
    string? Language,
    string? Categories,
    string? Series,
    string? VolumeNumber,
    int Rating = 0,
    bool IsFavorite = false,
    string? PersonalReview = null,
    DateTime? FinishedAt = null,
    /**
     * Inbound compatibility only: the singular collection is translated into a
     * single-element membership set by the endpoint. Not populated on responses
     * (membership is reported as CollectionIds).
     */
    Guid? CollectionId = null,
    IReadOnlyList<Guid>? CollectionIds = null,
    // Explicit create intent for the local-file flow. A digital record without
    // a file is otherwise legitimate, so incomplete upload state must never be
    // inferred from HasFile/FileName.
    bool FileUploadExpected = false
);

public record UpdateBookDto(
    string? Title,
    string? Subtitle,
    string? Author,
    string? Editor,
    string? Translator,
    string? Narrator,
    string? Description,
    string? Isbn,
    string? Asin,
    string? Duration,
    string? Publisher,
    string? PlaceOfPublication,
    string? PublishedDate,
    string? Edition,
    int? PageCount,
    string? Language,
    string? Categories,
    string? Series,
    string? VolumeNumber,
    int? Rating,
    bool? IsFavorite,
    string? PersonalReview,
    DateTime? FinishedAt,
    bool? IsFinished,
    // Inbound compatibility (see CreateBookDto.CollectionId).
    Guid? CollectionId = null,
    bool ClearCollection = false,
    IReadOnlyList<Guid>? CollectionIds = null
);

public record UpdateProgressDto(string Location, int Percentage);

/// <summary>
/// Full replacement set of collections a book belongs to. An empty list clears
/// every membership — which is how "remove from collection" is finally
/// expressible (the legacy nullable CollectionId could not distinguish
/// "leave unchanged" from "clear").
/// </summary>
public sealed record UpdateBookCollectionsDto(IReadOnlyList<Guid> CollectionIds);

/// <summary>
/// Manual work-membership link: make this book an edition of the same work as
/// <paramref name="TargetBookId"/>, regardless of whether their title/author
/// metadata match. The two work groups are merged, with the target's work as
/// the survivor.
/// </summary>
public sealed record LinkWorkDto(Guid TargetBookId);

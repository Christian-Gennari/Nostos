using System;
using System.Collections.Generic;

namespace Nostos.Shared.Dtos;

// --- STABLE RESPONSE ENVELOPE (library domain) ---
// Mirror of the reading envelope. Every library mutation accepts a client id
// and idempotency key and returns this same semantic result across REST, MCP
// and the UI. `duplicate` means receipt replay ONLY — matching an existing
// book is `outcome: matched`, never `duplicate: true`.
public sealed record LibraryCommandResultDto(
    string Reply,
    object? Data,
    string StateVersion,
    bool Duplicate = false
);

public sealed record LibraryErrorDto(string Code);

/// <summary>
/// Error payload carrying the candidate rows that require a caller decision
/// (outcome: confirmation_required). Serialized as data on the envelope.
/// </summary>
public sealed record LibraryConfirmationErrorDto(string Code, IReadOnlyList<LibraryCandidate> Candidates);

// --- BOOK CREATE / MATCH ---
public sealed record LibraryCreateBookRequest(
    string ClientId,
    string IdempotencyKey,
    string Type,
    string Title,
    string? Subtitle = null,
    string? Author = null,
    string? Editor = null,
    string? Translator = null,
    string? Narrator = null,
    string? Description = null,
    string? Isbn = null,
    string? Asin = null,
    string? Duration = null,
    string? Publisher = null,
    string? PlaceOfPublication = null,
    string? PublishedDate = null,
    string? Edition = null,
    int? PageCount = null,
    string? Language = null,
    string? Categories = null,
    string? Series = null,
    string? VolumeNumber = null,
    Guid? CollectionId = null,
    int Rating = 0,
    bool IsFavorite = false,
    string? PersonalReview = null,
    DateTime? FinishedAt = null,
    Guid? ConfirmedBookId = null,
    bool ForceCreate = false,
    // Multi-collection membership at create time. APPENDED to the record so
    // existing positional callers (REST/MCP) keep compiling. When supplied
    // non-empty it is authoritative and CollectionId above acts as the legacy
    // single-value form.
    IReadOnlyList<Guid>? CollectionIds = null,
    // Local upload intent is explicit so a newly-created digital row can be
    // distinguished from an intentional metadata-only book before /file
    // succeeds. Appended to preserve positional callers.
    bool FileUploadExpected = false
);

// --- BOOK UPDATE ---
// Null = leave unchanged; empty string = clear (repo NullIfEmpty convention).
// CollectionId alone cannot express "clear", so ClearCollection does.
public sealed record LibraryUpdateBookRequest(
    string ClientId,
    string IdempotencyKey,
    Guid BookId,
    string? Title = null,
    string? Subtitle = null,
    string? Author = null,
    string? Editor = null,
    string? Translator = null,
    string? Narrator = null,
    string? Description = null,
    string? Isbn = null,
    string? Asin = null,
    string? Duration = null,
    string? Publisher = null,
    string? PlaceOfPublication = null,
    string? PublishedDate = null,
    string? Edition = null,
    int? PageCount = null,
    string? Language = null,
    string? Categories = null,
    string? Series = null,
    string? VolumeNumber = null,
    Guid? CollectionId = null,
    bool ClearCollection = false,
    int? Rating = null,
    bool? IsFavorite = null,
    string? PersonalReview = null,
    DateTime? FinishedAt = null,
    bool? IsFinished = null,
    // Full replacement membership set. APPENDED so existing positional callers
    // keep compiling. null = leave membership unchanged; an empty list clears
    // every membership. Set semantics rather than add/remove verbs: one contract
    // expresses add, remove and clear-all, and it is naturally idempotent.
    IReadOnlyList<Guid>? CollectionIds = null
);

// --- WORK MEMBERSHIP (multi-edition grouping) ---
// Automatic grouping by normalized title+author stays the default; these two
// mutations are the explicit user override for when the heuristic is wrong.
// Both are receipt-guarded like every other library mutation.

/// <summary>
/// Link <paramref name="BookId"/> into the same work as
/// <paramref name="TargetBookId"/>, deliberately regardless of whether their
/// title/author metadata match — that is the point of the override.
///
/// The two work groups are MERGED, not re-parented: silently pulling one
/// edition out of an already-valid multi-edition group would be a surprising
/// side effect of "link these two books". <paramref name="TargetBookId"/>'s
/// work is the survivor, so the outcome is predictable from the request.
/// </summary>
public sealed record LibraryLinkWorkRequest(
    string ClientId,
    string IdempotencyKey,
    Guid BookId,
    Guid TargetBookId
);

/// <summary>
/// Detach <paramref name="BookId"/> from its current work into a NEW
/// single-book work built from that book's own current title/author identity.
/// A book that is already alone in its work is a successful no-op.
/// </summary>
public sealed record LibraryUnlinkWorkRequest(
    string ClientId,
    string IdempotencyKey,
    Guid BookId
);

/// <summary>
/// Data payload of a work-membership mutation: where the book ended up, how
/// many editions now share that work, and — for a merge — the work that was
/// removed because the merge left it empty.
/// </summary>
public sealed record LibraryWorkMembershipResultDto(
    Guid BookId,
    Guid WorkId,
    int EditionCount,
    Guid? RemovedWorkId = null
);

// --- RESOLVE (read-only identity resolution) ---
public sealed record LibraryResolveBookRequest(
    string? Isbn = null,
    string? Asin = null,
    string? Title = null,
    string? Author = null,
    bool IncludeExternalMetadata = true
);

public enum LibraryResolution
{
    ExactMatch,
    Candidates,
    NotFound,
    IdentityConflict,
}

public sealed record LibraryCandidate(
    Guid BookId,
    string Title,
    string? Author,
    string? Isbn,
    string? Asin,
    string MatchReason
);

public sealed record LibraryResolveResult(
    LibraryResolution Resolution,
    BookDto? MatchedBook = null,
    IReadOnlyList<LibraryCandidate>? Candidates = null,
    CreateBookDto? Prefill = null,
    string? LookupError = null
);

// --- EXTERNAL ACQUISITION ATTACHMENT (issue #166) ---
// Used by the acquisition layer once a downloaded asset has already been
// written to storage. Kept as a library-domain mutation so ILibraryService
// stays the single writer for library state: provider/acquisition code must
// never reach for a repository directly, however convenient it looks.
//
// The file and its provenance are recorded in ONE transaction, so a book can
// never end up claiming a file it has no provenance for, or vice versa.
public sealed record LibraryAttachAcquiredAssetRequest(
    string ClientId,
    string IdempotencyKey,
    Guid BookId,
    /// <summary>
    /// The bare file name storage actually produced (e.g. "book.epub"). A
    /// name rather than an extension so the storage service owns the naming
    /// convention alone; a path is rejected.
    /// </summary>
    string FileName,
    string ProviderId,
    string ProviderDisplayName,
    string ExternalId,
    string AssetId,
    string? AssetFormat = null,
    string? SourceUrl = null,
    /// <summary>The source's own rights wording. Never converted into a Nostos claim.</summary>
    string? RightsStatement = null,
    DateTime? AcquiredAt = null,
    /// <summary>
    /// Total running time as a display string ("13:06:44"), applied to the
    /// audiobook the asset is attached to. Providers that know the duration up
    /// front supply it; without it an imported audiobook would show no length
    /// until something re-read the file.
    /// </summary>
    string? Duration = null,
    /// <summary>Canonical chapters derived from the source, for audiobooks.</summary>
    IReadOnlyList<BookChapterDto>? Chapters = null,
    /// <summary>
    /// The bare file name of the cover the importer stored, when it stored one
    /// (e.g. "cover.jpg"). The book row is what the cover URL is derived from, so
    /// an import that wrote the image but never set this produced a cover nothing
    /// could display. Appended, never inserted: this record is positional.
    /// </summary>
    string? CoverFileName = null,
    /// <summary>
    /// The stored tracks of a multi-track audiobook, in playing order. When
    /// present the book has no primary file and <see cref="FileName"/> must be
    /// empty: a book holds one file or a track list, never both.
    /// </summary>
    IReadOnlyList<LibraryAcquiredTrack>? Tracks = null
);

/// <summary>
/// One track as storage holds it: its bare stored name plus the length, size
/// and CRC-32 measured from the stored bytes.
/// </summary>
public sealed record LibraryAcquiredTrack(
    int Number,
    string FileName,
    string ContentType,
    long DurationMs,
    long Bytes,
    uint Crc32,
    string? Title);

/// <summary>
/// Data payload of an acquisition attachment. Deliberately not
/// <see cref="LibraryCreateOrMatchResultDto"/>: this is not an identity
/// resolution outcome but a statement that the file and its provenance now
/// belong to this book.
/// </summary>
public sealed record LibraryAttachAcquiredAssetResultDto(Guid BookId, BookDto? Book);

// --- CREATE-OR-MATCH OUTCOME (data payload of LibraryCommandResultDto) ---
public sealed record LibraryCreateOrMatchResultDto(
    string Outcome, // created | matched | confirmation_required
    Guid? BookId = null,
    BookDto? Book = null,
    IReadOnlyList<LibraryCandidate> Candidates = null!
);

// --- COLLECTIONS ---
public sealed record LibraryCreateCollectionRequest(
    string ClientId,
    string IdempotencyKey,
    string Name,
    Guid? ParentId = null
);

public sealed record LibraryRenameCollectionRequest(
    string ClientId,
    string IdempotencyKey,
    Guid CollectionId,
    string Name
);

public sealed record LibraryMoveCollectionRequest(
    string ClientId,
    string IdempotencyKey,
    Guid CollectionId,
    Guid? NewParentId = null
);

public sealed record LibraryDeleteCollectionRequest(
    string ClientId,
    string IdempotencyKey,
    Guid CollectionId,
    bool Confirm = false
);

public sealed record LibraryDeleteCollectionResultDto(
    Guid CollectionId,
    int BooksUnlinked,
    int ChildrenAffected
);

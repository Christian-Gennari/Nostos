using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// A collection named either by its id or by the reference of a collection an
/// earlier operation in the same changeset creates. Exactly one is set.
/// </summary>
public readonly record struct LibraryCollectionRef(Guid? Id, string? Ref)
{
    public static LibraryCollectionRef Existing(Guid id) => new(id, null);
    public static LibraryCollectionRef Created(string reference) => new(null, reference);

    public override string ToString() => Id?.ToString() ?? $"ref:{Ref}";
}

/// <summary>
/// A topic kind named either by its id or by the reference of a kind an earlier
/// operation in the same changeset creates. Exactly one is set.
/// </summary>
public readonly record struct TopicKindRef(Guid? Id, string? Ref)
{
    public static TopicKindRef Existing(Guid id) => new(id, null);
    public static TopicKindRef Created(string reference) => new(null, reference);

    public override string ToString() => Id?.ToString() ?? $"ref:{Ref}";
}

/// <summary>
/// One change in a changeset. Operations run in the order given, inside one
/// transaction, through the same canonical code as every other library write.
/// </summary>
public abstract record LibraryChangeOp;

/// <summary>Creates a collection; later operations can name it by <paramref name="Ref"/>.</summary>
public sealed record CreateCollectionOp(string Ref, string Name, LibraryCollectionRef? Parent = null)
    : LibraryChangeOp;

public sealed record RenameCollectionOp(LibraryCollectionRef Collection, string Name) : LibraryChangeOp;

/// <summary>Moves a collection; a null parent moves it to the top level.</summary>
public sealed record MoveCollectionOp(LibraryCollectionRef Collection, LibraryCollectionRef? NewParent)
    : LibraryChangeOp;

/// <summary>
/// Deletes a collection together with its subcollections, deepest first. Books
/// are never deleted: their membership rows go. The cascade is never silent,
/// because the diff names every collection removed and how many books it held.
/// </summary>
public sealed record DeleteCollectionOp(LibraryCollectionRef Collection) : LibraryChangeOp;

/// <summary>Replaces one book's memberships with exactly this set.</summary>
public sealed record SetBookCollectionsOp(Guid BookId, IReadOnlyList<LibraryCollectionRef> Collections)
    : LibraryChangeOp;

/// <summary>Adds books to a collection, keeping every other membership they have.</summary>
public sealed record AddBooksToCollectionOp(LibraryCollectionRef Collection, IReadOnlyList<Guid> BookIds)
    : LibraryChangeOp;

/// <summary>Removes books from a collection; a book not in it is left alone.</summary>
public sealed record RemoveBooksFromCollectionOp(LibraryCollectionRef Collection, IReadOnlyList<Guid> BookIds)
    : LibraryChangeOp;

/// <summary>
/// Creates a topic kind. <paramref name="ColorKey"/> is a preference: when
/// null or already taken, apply/preview takes the first free palette colour.
/// Later operations in the same changeset can name it by <paramref name="Ref"/>.
/// </summary>
public sealed record CreateTopicKindOp(string Ref, string Name, string? ColorKey = null)
    : LibraryChangeOp;

/// <summary>
/// Gives one kind to many topics, or clears their kind when
/// <paramref name="Kind"/> is null. A kind the reader set is kept unless the op
/// says that topic was named: <paramref name="NamedTopicIds"/> names exactly the
/// topics the user named (the apply reads it, so a recomputed apply can never
/// overwrite an unnamed reader kind), and <paramref name="OverwriteReaderKinds"/>
/// is the older whole-batch flag, used only when the set is null.
/// </summary>
public sealed record SetTopicKindOp(
    IReadOnlyList<Guid> TopicIds,
    TopicKindRef? Kind,
    bool OverwriteReaderKinds = false,
    IReadOnlyList<Guid>? NamedTopicIds = null) : LibraryChangeOp;

/// <summary>Edits a book's own fields. A null field is left unchanged.</summary>
public sealed record UpdateBookOp(
    Guid BookId,
    string? Title = null,
    string? Author = null,
    string? Description = null,
    int? Rating = null,
    bool? IsFavorite = null,
    string? PersonalReview = null,
    bool? IsFinished = null,
    // APPENDED. This one edition's own rating, an exception to the book's: 0..5,
    // 0 clears it. BookId names the edition. Rating, IsFavorite and PersonalReview
    // above are the book's and the same on every edition.
    int? EditionRating = null) : LibraryChangeOp;

/// <summary>
/// What running a changeset did, or why it could not run. A failed changeset
/// changed nothing: the transaction is rolled back as a whole.
/// </summary>
public sealed record LibraryChangesetResult(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    int? FailedOperation,
    LibraryChangesetDiffDto? Diff,
    string? DiffHash,
    string StateVersion,
    bool Committed,
    LibraryUndoSnapshot? Undo)
{
    internal LibraryChangesetPreviewState? PreviewState { get; init; }

    public static LibraryChangesetResult Fail(string code, string message, string version, int? operation = null) =>
        new(false, code, message, operation, null, null, version, false, null);
}

internal sealed record LibraryChangesetPreviewState(
    LibraryChangesetDiff.LibrarySnapshot Before,
    LibraryChangesetDiff.LibrarySnapshot After,
    IReadOnlyDictionary<string, Guid> CreatedRefs,
    IReadOnlyDictionary<Guid, string> BookTitles);

/// <summary>
/// Everything needed to take a committed changeset back: each touched row as it
/// was before and as the changeset left it. Undo only restores a row that still
/// looks the way the changeset left it, so it never overwrites a later change.
/// </summary>
public sealed record LibraryUndoSnapshot(
    IReadOnlyList<CollectionSnapshot> Collections,
    IReadOnlyList<MembershipSnapshot> Memberships,
    IReadOnlyList<BookFieldsSnapshot> Books,
    // APPENDED (issue #1770): kinds the changeset created and every topic whose
    // kind it changed, with the source each side carried.
    IReadOnlyList<TopicKindSnapshot> TopicKinds,
    IReadOnlyList<TopicKindAssignmentSnapshot> TopicKindAssignments);

/// <summary>A topic kind row; a null side means it did not exist then.</summary>
public sealed record TopicKindSnapshot(Guid Id, TopicKindState? Before, TopicKindState? After);

public sealed record TopicKindState(string Name, string ColorKey, int SortOrder);

/// <summary>
/// One topic's kind and source before and after the changeset. Restoring the
/// order matters: undo puts every topic back first, then removes a kind the
/// changeset created once nothing uses it.
/// </summary>
public sealed record TopicKindAssignmentSnapshot(
    Guid TopicId,
    string TopicName,
    TopicKindAssignmentState Before,
    TopicKindAssignmentState After);

public sealed record TopicKindAssignmentState(
    string TopicName,
    Guid? KindId,
    string? Source,
    // APPENDED (issue #1770 review): the kind's display name at that moment, so
    // undo can name a kind the reader deleted after the apply.
    string? KindName = null);

/// <summary>A collection row; a null side means it did not exist then.</summary>
public sealed record CollectionSnapshot(Guid Id, CollectionState? Before, CollectionState? After);

public sealed record CollectionState(string Name, Guid? ParentId);

public sealed record MembershipSnapshot(Guid BookId, IReadOnlyList<Guid> Before, IReadOnlyList<Guid> After);

public sealed record BookFieldsSnapshot(Guid BookId, BookFieldsState Before, BookFieldsState After);

/// <summary>
/// The book columns a changeset can change, stored so undo can restore them exactly.
/// <see cref="Rating"/>, <see cref="IsFavorite"/> and <see cref="PersonalReview"/> are the
/// book's values and live on its work, so undo restores them there.
/// </summary>
public sealed record BookFieldsState(
    string Title,
    string? Author,
    string? Description,
    int Rating,
    bool IsFavorite,
    string? PersonalReview,
    DateTime? FinishedAt,
    int ProgressPercent,
    // APPENDED. The edition's own rating; null when the snapshot did not capture it.
    int? EditionRating = null);

/// <summary>What an undo restored, and what it left alone because it changed since.</summary>
public sealed record LibraryRevertResult(
    bool Success,
    int Restored,
    IReadOnlyList<string> Conflicts,
    string StateVersion);

/// <summary>
/// Library changes as one unit: previewed without committing, applied
/// atomically, and taken back by restoring what they replaced.
/// </summary>
public interface ILibraryChangesetService
{
    /// <summary>Runs the changeset and rolls it back, returning the exact diff it produced.</summary>
    Task<LibraryChangesetResult> PreviewAsync(IReadOnlyList<LibraryChangeOp> ops, CancellationToken ct = default);

    /// <summary>
    /// Runs and commits the changeset. When <paramref name="expectedDiffHash"/> is
    /// given and the library has changed so that the same operations would now
    /// do something different, nothing is committed and the result carries the
    /// new diff with <c>changeset_diff_changed</c>.
    /// </summary>
    Task<LibraryChangesetResult> ApplyAsync(
        IReadOnlyList<LibraryChangeOp> ops,
        string? expectedDiffHash,
        CancellationToken ct = default);

    /// <summary>Restores what a committed changeset replaced, row by row.</summary>
    Task<LibraryRevertResult> RevertAsync(LibraryUndoSnapshot undo, CancellationToken ct = default);
}

public static class LibraryChangesetErrorCodes
{
    public const string Empty = "changeset_empty";
    public const string TooLarge = "changeset_too_large";
    public const string UnknownReference = "changeset_unknown_reference";
    public const string DuplicateReference = "changeset_duplicate_reference";
    public const string DiffChanged = "changeset_diff_changed";
    public const string BookNotFound = "book_not_found";
    public const string CollectionNotFound = "collection_not_found";
    public const string TopicNotFound = "topic_not_found";
    public const string TopicKindNotFound = "topic_kind_not_found";
    public const string TopicKindLimit = "topic_kind_limit";
    public const string TopicKindDuplicate = "topic_kind_duplicate";
}

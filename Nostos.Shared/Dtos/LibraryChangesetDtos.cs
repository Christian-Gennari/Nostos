namespace Nostos.Shared.Dtos;

// --- LIBRARY CHANGESETS ---
// What a set of library changes does, as the user reviews it: named, counted
// and grouped. Produced by running the changes in a transaction that is then
// rolled back (a preview) or committed (an apply), so it describes what the
// canonical library service actually did, not what anyone intended.

/// <summary>The complete effect of one changeset.</summary>
public sealed record LibraryChangesetDiffDto(
    IReadOnlyList<LibraryCollectionChangeDto> Collections,
    IReadOnlyList<LibraryMembershipChangeDto> Memberships,
    IReadOnlyList<LibraryBookEditDto> BookEdits,
    LibraryChangesetCountsDto Counts,
    // APPENDED (issue #1770): topic kinds the changeset creates and the topics
    // whose kind it gives, changes or clears. They default to null so JSON from
    // a host that predates them still deserialises; the properties below turn
    // that null into an empty list once, so every reader (the hash, the stored
    // changeset, the review note and the policy) can treat them as plain lists.
    IReadOnlyList<LibraryTopicKindChangeDto>? TopicKinds = null,
    IReadOnlyList<LibraryTopicKindAssignmentDto>? TopicKindAssignments = null)
{
    public IReadOnlyList<LibraryTopicKindChangeDto> TopicKinds { get; init; } = TopicKinds ?? [];

    public IReadOnlyList<LibraryTopicKindAssignmentDto> TopicKindAssignments { get; init; } =
        TopicKindAssignments ?? [];

    public bool IsEmpty => Collections.Count == 0 && Memberships.Count == 0 && BookEdits.Count == 0
        && TopicKinds.Count == 0 && TopicKindAssignments.Count == 0;
}

/// <summary>
/// One collection the changeset creates, deletes, renames or moves.
/// <paramref name="Kind"/> is <c>created</c>, <c>deleted</c> or <c>updated</c>;
/// an update carries whichever of the previous name and parent changed.
/// <paramref name="BookCount"/> is the number of books in it before a delete,
/// or after any other change.
/// </summary>
public sealed record LibraryCollectionChangeDto(
    Guid Id,
    string Kind,
    string Name,
    string? PreviousName,
    string? ParentName,
    string? PreviousParentName,
    bool ParentChanged,
    int BookCount);

/// <summary>One book whose collection membership changes.</summary>
public sealed record LibraryMembershipChangeDto(
    Guid BookId,
    string Title,
    string? Author,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed);

/// <summary>One book whose own fields change.</summary>
public sealed record LibraryBookEditDto(
    Guid BookId,
    string Title,
    IReadOnlyList<LibraryFieldChangeDto> Fields);

/// <summary>
/// One topic kind the changeset creates. <paramref name="Kind"/> is
/// <c>created</c> (a changeset never edits or deletes a kind).
/// <paramref name="TopicCount"/> is how many topics <em>this changeset</em>
/// assigns to the kind, not the kind's library-wide total: the review card
/// counts the steps it can leave out, not the whole library.
/// </summary>
public sealed record LibraryTopicKindChangeDto(
    Guid Id,
    string Kind,
    string Name,
    string? PreviousName,
    string ColorKey,
    int TopicCount,
    // APPENDED: the review step that creates this kind, so the card can keep
    // the create out of the step list when its group already says "New".
    int? StepIndex = null);

/// <summary>
/// One topic whose kind the changeset changes. <paramref name="KindId"/> and
/// <paramref name="KindName"/> are what it becomes; null clears the kind.
/// <paramref name="PreviousKindName"/> is what it had before, for the review
/// card's "was …" note. <paramref name="StepIndex"/> names the review step this
/// topic belongs to, so the card can leave one topic out without the rest.
/// </summary>
public sealed record LibraryTopicKindAssignmentDto(
    Guid TopicId,
    string TopicName,
    Guid? KindId,
    string? KindName,
    string? PreviousKindName,
    string? Source,
    int? StepIndex = null,
    // APPENDED (issue #1770 review): the target kind's palette key, so the
    // review card shows an existing kind's colour as well as a new one's.
    string? KindColorKey = null,
    // APPENDED (issue #1770 review, round 2): who set the kind before this
    // changeset. A reader claiming a staged topic changes only this, and the
    // pinned diff hash must notice it or an apply could overwrite the claim.
    string? PreviousSource = null);

/// <summary>One field of a book, before and after, as display text.</summary>
public sealed record LibraryFieldChangeDto(string Field, string? Before, string? After);

/// <summary>Totals the review card and the risk policy both read.</summary>
public sealed record LibraryChangesetCountsDto(
    int CollectionsCreated,
    int CollectionsDeleted,
    int CollectionsUpdated,
    int BooksTouched,
    int MembershipsAdded,
    int MembershipsRemoved,
    int BooksLosingMembership,
    int BookEdits,
    // APPENDED (issue #1770). TopicKindChanges counts every topic whose kind
    // the changeset gives, changes or clears; the three below split it so the
    // card and the review reason can say which of the three happened.
    int TopicKindsCreated = 0,
    int TopicKindChanges = 0,
    int TopicKindsGiven = 0,
    int TopicKindsChanged = 0,
    int TopicKindsCleared = 0);

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
    LibraryChangesetCountsDto Counts)
{
    public bool IsEmpty => Collections.Count == 0 && Memberships.Count == 0 && BookEdits.Count == 0;
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
    int BookEdits);

using System;
using System.Collections.Generic;

namespace Nostos.Shared.Dtos;

/// <summary>
/// A book the user wants to read and does not have yet.
/// <paramref name="NextPosition"/> is the slot on the ordered "Next up" shelf,
/// or null for "Someday". <paramref name="OwnedBookId"/> names a library book
/// that looks like the same title, so the list can say "you already have this".
/// </summary>
public sealed record ReadingListEntryDto(
    Guid Id,
    string Title,
    string? Author,
    string? Isbn,
    string? PublishedDate,
    string? Why,
    string? RecommendedBy,
    int? NextPosition,
    DateTime CreatedAt,
    Guid? OwnedBookId = null
);

/// <summary>
/// Create or fully replace an entry's own fields. <paramref name="ReadNext"/>
/// only applies on create; the shelf is otherwise set with
/// <see cref="SetReadingListNextDto"/>.
/// </summary>
public sealed record SaveReadingListEntryDto(
    string? Title,
    string? Author = null,
    string? Isbn = null,
    string? PublishedDate = null,
    string? Why = null,
    string? RecommendedBy = null,
    bool ReadNext = false
);

/// <summary>
/// The whole "Next up" shelf, in reading order. Entries not named go to
/// "Someday", so promote, demote and reorder are all this one replacement.
/// </summary>
public sealed record SetReadingListNextDto(IReadOnlyList<Guid>? Ids);

/// <summary>The library book an entry turned into.</summary>
public sealed record ReadingListAcquiredDto(Guid BookId);

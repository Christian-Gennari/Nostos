using System.Text.Json;
using System.Text.Json.Serialization;
using Nostos.Backend.Integrations.Assistant;

namespace Nostos.Backend.Integrations.Mcp;

// The JSON result of every MCP tool. Property names and their order are the
// wire contract a connected app reads: append, never insert, and keep a null
// that a client already sees (NostosMcp.Json omits nulls, so a null that must
// stay present says so on the property).

public sealed record McpSearchHit(string Id, string Kind, string Title, string Text, string Url, McpCitation? Citation, IReadOnlyList<string>? Topics);

public sealed record McpSearchResult(IReadOnlyList<McpSearchHit> Results, string? Note, IReadOnlyList<string>? StillIndexing);

/// <summary>
/// One result of <c>fetch</c>. <see cref="Metadata"/> is the kind's own record:
/// <see cref="McpNoteDocument"/>, <see cref="McpTopicDocument"/> or
/// <see cref="McpBookTextDocument"/>, each serialized under <c>metadata</c>.
/// </summary>
public sealed record McpDocument(string Id, string Kind, string Title, string Text, string Url, McpCitation? Citation, object Metadata);

/// <summary>A note's metadata: the quote and the thought, and where it came from.</summary>
public sealed record McpNoteDocument(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? BookId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Quote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Topics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime CreatedAt);

/// <summary>A topic's metadata: how many notes it connects.</summary>
public sealed record McpTopicDocument(int Notes);

/// <summary>A book-text passage's metadata: its book and the headings it sits under.</summary>
public sealed record McpBookTextDocument(Guid BookId, IReadOnlyList<string>? Headings);

public sealed record McpNamedCount(string Name, int Count, string Id);

public sealed record McpBrainOverview(int Notes, int UnsortedNotes, int Topics, IReadOnlyList<McpNamedCount> TopTopics, IReadOnlyList<McpNamedCount> BooksWithMostNotes);

/// <summary>
/// The whole-library read, passed through exactly as the library answers it so
/// MCP and Ask Nostos never disagree about the same overview.
/// </summary>
public sealed record McpOverview(JsonElement Library, McpBrainOverview Brain);

public sealed record McpBookSummary(Guid Id, string Title, string? Author, string? Year, string Type, int ProgressPercent, string Url);

public sealed record McpBookList(IReadOnlyList<McpBookSummary> Books, int Total, int Page, string? Note);

public sealed record McpBook(
    Guid Id,
    string Title,
    string? Subtitle,
    string? Author,
    string? Year,
    string Type,
    string? Publisher,
    string? Isbn,
    string? Description,
    int ProgressPercent,
    int? Rating,
    string? Review,
    string Url,
    McpCitation Citation);

public sealed record McpNote(string Id, string Kind, string? Quote, string? Note, string? PassageTitle, DateTime CreatedAt, McpCitation Citation);

public sealed record McpNoteList(string Book, IReadOnlyList<McpNote> Notes, int Total, int? NextOffset);

public sealed record McpTopic(string Id, string Name, int Notes, string Url);

public sealed record McpTopicList(IReadOnlyList<McpTopic> Topics);

public sealed record McpTopicDetail(string Id, string Name, string? Description, int Notes, string Url, IReadOnlyList<McpNote> LinkedNotes);

public sealed record McpSavedNote(string Id, string Book, string Url, McpCitation Citation);

/// <summary>
/// What <c>add_book</c> did: <c>created</c>, <c>matched</c> (already in the
/// library, <c>duplicate</c> true) or <c>choose</c> (nothing added; the
/// <c>candidates</c> may be the same book).
/// </summary>
public sealed record McpAddedBook(
    string Outcome,
    bool Duplicate,
    McpBookSummary? Book,
    IReadOnlyList<McpMovedFromToRead>? MovedFromToRead,
    IReadOnlyList<McpBookCandidate>? Candidates,
    string? Message);

public sealed record McpBookCandidate(Guid Id, string Title, string? Author, string? Isbn, string Url);

/// <summary>A To read entry that moved to the library; <c>ReasonKeptAsNote</c> when its why or recommender is now a note on the book.</summary>
public sealed record McpMovedFromToRead(Guid EntryId, string Title, bool ReasonKeptAsNote);

/// <summary>What <c>update_book</c> changed, kept and left, and the arguments that undo it.</summary>
public sealed record McpUpdatedBook(
    Guid Id,
    string Title,
    string Url,
    IReadOnlyList<BookFieldChange> Changed,
    IReadOnlyList<BookFieldKept>? Kept,
    IReadOnlyList<string>? Unchanged,
    IReadOnlyDictionary<string, object?>? Undo,
    string Message);

/// <summary>
/// <c>find_quote</c>'s answer. <see cref="Citation"/> is present only for a
/// passage the book resolves to: an unresolvable book has nothing to cite.
/// </summary>
internal sealed record McpQuoteFindResult(
    string Status,
    string Message,
    int TotalMatches,
    IReadOnlyList<McpQuoteCandidate> Candidates);

internal sealed record McpQuoteCandidate(
    int Number,
    string QuoteRef,
    string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Chapter,
    int PositionPercent,
    int Words,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Preview,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpCitation? Citation = null);

/// <summary>The Studio documents <c>list_drafts</c> returns; a folder has no link to open.</summary>
internal sealed record McpWritingList(int Total, IReadOnlyList<McpWritingListItem> Items);

internal sealed record McpWritingListItem(
    Guid Id,
    string Name,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ParentId,
    string Path,
    DateTime UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Link);

/// <summary>One Studio document as <c>read_draft</c> returns it.</summary>
internal sealed record McpWritingDocument(
    Guid Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ParentId,
    string Path,
    DateTime UpdatedAt,
    string Link,
    int Characters,
    bool Truncated,
    string Content,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Duplicate = null);

/// <summary>What <c>create_draft</c> made.</summary>
internal sealed record McpWritingCreated(
    Guid Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? ParentId,
    string Path,
    string Link,
    int Citations,
    int Characters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Duplicate = null);

/// <summary>The To read list as <c>reading_list</c> returns it.</summary>
internal sealed record McpReadingList(
    int Total,
    int NextUpCount,
    int Returned,
    string Link,
    IReadOnlyList<McpReadingListEntry> Entries);

/// <summary>One To read entry, on Next up or on Someday.</summary>
internal sealed record McpReadingListEntry(
    Guid Id,
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Author,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Isbn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PublishedDate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Why,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RecommendedBy,
    string Shelf,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? NextPosition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? OwnedBookId,
    DateTime AddedAt);

/// <summary>What <c>add_to_reading_list</c> did: the entry, and whether it was already there.</summary>
internal sealed record McpReadingListAdded(bool Duplicate, string Link, McpReadingListEntry Entry);

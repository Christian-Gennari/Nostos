using System.Text.Json.Serialization;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

// What the capabilities answer, as the MCP tools read it. The capabilities in
// Integrations/Assistant serialize their own result shapes; these records are
// the typed view of those shapes, so a tool never probes a JsonElement for
// "data", "book" or "candidates" (issue #1599). A property here must match the
// camelCase name the capability writes.

/// <summary>
/// The library envelope every library read and mutation answers with
/// (<c>reply</c>, <c>data</c>, <c>stateVersion</c>, <c>duplicate</c>).
/// </summary>
internal sealed record McpLibraryEnvelope<T>(string Reply, T? Data, string StateVersion, bool Duplicate = false);

/// <summary>One page of books, as <c>library_list_books</c> answers it.</summary>
internal sealed record McpBookPage(IReadOnlyList<BookDto> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// What <c>library_create_or_match_book</c> carries in <c>data</c>: the outcome,
/// and either the book or the candidates a caller must choose between.
/// </summary>
internal sealed record McpBookCreateOutcome(
    string? Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? BookId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] BookDto? Book,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<LibraryCandidate>? Candidates);

/// <summary><c>library_resolve_book</c>'s answer: the resolution and the book it found.</summary>
internal sealed record McpResolvedBook(
    LibraryResolution Resolution,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] BookDto? MatchedBook,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<LibraryCandidate>? Candidates);

/// <summary><c>topics_get</c>'s answer: the topic, its own words, and its linked notes.</summary>
internal sealed record McpTopicRead(
    Guid Id,
    string? Name,
    string? Description,
    int? NoteCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<McpTopicLinkedNote>? Notes);

/// <summary>One note <c>topics_get</c> links to the topic.</summary>
internal sealed record McpTopicLinkedNote(
    Guid NoteId,
    Guid BookId,
    string? BookTitle,
    string? BookAuthor,
    string? Kind,
    string? Content,
    string? SelectedText,
    string? Location,
    string? LocationKind,
    DateTime CreatedAt);

/// <summary>
/// The <c>knowledge_read_evidence</c> arguments an evidence id names, so
/// <c>fetch</c> passes a typed handle instead of a property bag.
/// </summary>
internal sealed record McpEvidenceAddress(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? NoteId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TopicId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? BookId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ordinal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExtractorVersion = null);

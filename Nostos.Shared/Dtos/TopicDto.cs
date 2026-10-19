namespace Nostos.Shared.Dtos;

// For the list
public record TopicDto(
    Guid Id,
    string Name,
    int UsageCount,
    int NoteMatchCount = 0,
    string? NoteMatchSnippet = null,
    // APPENDED: the reader's own words about the topic.
    string? Description = null);

// For the detail view
public record TopicDetailDto(Guid Id, string Name, List<NoteContextDto> Notes);

public record TopicStatsDto(
    int TotalTopics,
    int TotalReferences,
    int SingleNoteTopics,
    string? MostUsedName,
    int MostUsedCount
);

public record RelatedTopicDto(Guid Id, string Name, int SharedNotes, List<Guid> SharedNoteIds);

// UPDATED: Added SelectedText and CfiRange
public record NoteContextDto(
    Guid NoteId,
    string Content,
    string? SelectedText,
    string? CfiRange,
    Guid BookId,
    string BookTitle,
    DateTime CreatedAt,
    string SourceAnchorKind = "unknown",
    string? SourceAnchorValue = null,
    bool AnchorVerified = false,
    // APPENDED (issue #861): the note's highlighter pen, by name.
    string? HighlightColour = null,
    // APPENDED (issue #1014): a passage. SelectedText is only its opening.
    string Kind = "note",
    string? PassageTitle = null,
    string? PassageEnd = null,
    int? PassageWordCount = null,
    // APPENDED (issue #1038): the book's author, so a quote can name who said it.
    string? BookAuthor = null
);

// For the whole-brain knowledge graph
public record TopicGraphNodeDto(Guid Id, string Name, int UsageCount);
public record TopicGraphEdgeDto(Guid SourceId, Guid TargetId, int SharedNotes);
public record TopicGraphDto(List<TopicGraphNodeDto> Nodes, List<TopicGraphEdgeDto> Edges);

public record CreateTopicDto(string Topic);
public record UpdateTopicDto(string Topic);
public record MergeTopicDto(Guid TargetId);

/// <summary>Body of <c>PUT /api/topics/{id}/description</c>; empty or null clears it.</summary>
public record UpdateTopicDescriptionDto(string? Description);

/// <summary>
/// Body of <c>POST /api/topics/link</c> and <c>/unlink</c>: add or remove the
/// named topics on every listed note. A topic linked by name is created if new.
/// </summary>
public record TopicLinkRequestDto(IReadOnlyList<string> Topics, IReadOnlyList<Guid> NoteIds);

/// <summary>The notes a link or unlink changed, and every listed note as it now is.</summary>
public record TopicLinkResultDto(IReadOnlyList<Guid> ChangedNoteIds, IReadOnlyList<NoteSearchHitDto> Notes);

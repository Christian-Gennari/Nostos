namespace Nostos.Shared.Dtos;

// For the list
public record TopicDto(Guid Id, string Name, int UsageCount, int NoteMatchCount = 0, string? NoteMatchSnippet = null);

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

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
    DateTime CreatedAt
);

// For the whole-brain knowledge graph
public record TopicGraphNodeDto(Guid Id, string Name, int UsageCount);
public record TopicGraphEdgeDto(Guid SourceId, Guid TargetId, int SharedNotes);
public record TopicGraphDto(List<TopicGraphNodeDto> Nodes, List<TopicGraphEdgeDto> Edges);

public record CreateTopicDto(string Topic);
public record UpdateTopicDto(string Topic);
public record MergeTopicDto(Guid TargetId);

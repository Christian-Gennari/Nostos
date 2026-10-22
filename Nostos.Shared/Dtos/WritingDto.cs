using System;

namespace Nostos.Shared.Dtos;

public record WritingDto(
    Guid Id,
    string Name,
    string Type,       // "Folder" or "Document"
    Guid? ParentId,
    DateTime UpdatedAt
);

public record WritingContentDto(
    Guid Id,
    string Name,
    string Content,
    DateTime UpdatedAt
);

public record CreateWritingDto(
    string Name,
    string Type,
    Guid? ParentId
);

public record UpdateWritingDto(
    string Name,
    string? Content
);

public record MoveWritingDto(
    Guid? NewParentId
);

/// <summary>
/// How a writing is laid out when exported (issue #1164). Saved per writing or
/// folder so the next export remembers it. The font and citation style are the
/// frontend's identifiers (Studio fonts, CSL style ids); the server checks only
/// their shape.
/// </summary>
public record WritingPageSetupDto(
    string Paper,          // "a4" or "letter"
    double MarginMm,
    string Font,
    double LineSpacing,
    string CitationStyle
);

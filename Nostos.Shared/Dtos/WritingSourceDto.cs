using System;

namespace Nostos.Shared.Dtos;

public record AddWritingSourceDto(Guid NoteId);

/// <summary>
/// A source note kept available for a writing document.
/// <paramref name="Id"/> is the note's id (matching NoteDto.Id). Every note field is the live
/// canonical value read at request time; <paramref name="AddedAt"/> is the only value owned
/// by the membership. Nothing is copied into storage — no excerpt snapshot column, no cached text.
/// </summary>
public record WritingSourceDto(
    Guid Id,
    Guid BookId,
    string? BookTitle,
    string Content,
    string? SelectedText,
    string? CfiRange,
    DateTime CreatedAt,
    DateTime AddedAt,
    string SourceAnchorKind,
    string? SourceAnchorValue,
    bool AnchorVerified,
    // APPENDED (issue #1014): a passage. SelectedText is then only its opening.
    string Kind = "note",
    string? PassageTitle = null,
    string? PassageEnd = null,
    int? PassageWordCount = null,
    // APPENDED (issue #1038): the book's author, so a quote can name who said it.
    string? BookAuthor = null
);

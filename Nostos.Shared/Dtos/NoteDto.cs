using System;

namespace Nostos.Shared.Dtos;

public record NoteDto(
    Guid Id,
    Guid BookId,
    string Content,
    string? CfiRange,
    string? SelectedText,
    DateTime CreatedAt,
    string? BookTitle,
    // APPENDED (positional record, so inserting renumbers every existing
    // client's JSON). Assistant capture provenance (issue #260 §2, §4): the
    // raw capture kept alongside the polished Content, and the anchor that
    // records where the note came from and whether it was verified.
    string? RawContent = null,
    string CaptureSource = "text",
    string ProcessingMode = "verbatim",
    string SourceAnchorKind = "unknown",
    string? SourceAnchorValue = null,
    bool AnchorVerified = false
);

public record CreateNoteDto(
    string Content,
    string? CfiRange = null,
    string? SelectedText = null,
    // APPENDED (positional record — a new client field must never renumber the
    // existing ones). Optional assistant capture provenance (issue #260 §2, §4):
    // callers that send only Content keep the typed-note defaults on the model.
    string? RawContent = null,
    string CaptureSource = "text",
    string ProcessingMode = "verbatim",
    string SourceAnchorKind = "unknown",
    string? SourceAnchorValue = null,
    bool AnchorVerified = false
);

public record UpdateNoteDto(string Content, string? SelectedText = null);

/// <summary>
/// A note found by searching note text, or listed because it belongs to no concept
/// (issue #158). `Snippet` is the fragment around the match, so the index can show
/// why a note matched without shipping the whole note to every row.
/// </summary>
public record NoteSearchHitDto(
    Guid Id,
    Guid BookId,
    string? BookTitle,
    string Content,
    string? SelectedText,
    string? Snippet,
    IReadOnlyList<string> ConceptNames,
    DateTime CreatedAt,
    // APPENDED (#562): exact note/source provenance for unified knowledge
    // retrieval. These fields describe the canonical note; they never grant
    // mutation authority.
    string? CfiRange = null,
    string SourceAnchorKind = "unknown",
    string? SourceAnchorValue = null,
    bool AnchorVerified = false,
    // Capture state belongs to the canonical note, independently of chat history.
    // Original text is fetched explicitly through /raw, not repeated in every row.
    string CaptureSource = "text",
    string ProcessingMode = "verbatim",
    bool HasRawContent = false
);

/// <summary>
/// One bounded page of the unlinked-note review queue, plus the total still
/// waiting. The review mode traverses the whole set, so the total is what keeps
/// `Items` from reading as the whole set — 50 of 63 unlinked notes must never be
/// presented as "the unlinked notes" (issue #256).
/// </summary>
public record NoteSearchPageDto(
    IReadOnlyList<NoteSearchHitDto> Items,
    int TotalCount,
    int Offset,
    int Limit
);

/// <summary>
/// Body of <c>POST /api/notes/{id}/reprocess</c> (issue #262 §7, §8): re-derive
/// the stored text from the note's raw transcript in this mode. The raw
/// transcript is always the source, never the current (possibly processed) text.
/// </summary>
public record ReprocessNoteDto(string ProcessingMode);

/// <summary>
/// The raw transcript of one note alongside the text and mode it currently
/// reflects (issue #262 §8). This is what makes the original capture retrievable
/// after any mode processed it, and what the restore route reads from.
/// </summary>
public record NoteRawTranscriptDto(
    Guid Id,
    string? RawContent,
    string Content,
    string ProcessingMode
);

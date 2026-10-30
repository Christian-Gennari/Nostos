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
    bool AnchorVerified = false,
    // APPENDED (issue #861): the highlighter pen, by name (amber | sage | blue |
    // rose). Null = none recorded; a reader draws that in amber.
    string? HighlightColour = null,
    // APPENDED (issue #1014): a passage — a stretch of the book saved to be
    // read again. It starts at SourceAnchorValue and ends at PassageEnd; for a
    // passage SelectedText is only its opening, and the full text comes from
    // GET /api/notes/{id}/passage.
    string Kind = "note",
    string? PassageTitle = null,
    string? PassageEnd = null,
    int? PassageWordCount = null,
    // APPENDED (issue #1038): the book's author, so a quote can name who said it.
    string? BookAuthor = null
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
    bool AnchorVerified = false,
    // APPENDED (issue #861): the pen a highlight is made with, by name.
    string? HighlightColour = null,
    // APPENDED (issue #1014): a passage. Its start is the typed anchor, its end
    // PassageEnd in the same terms; SelectedText is its plain text and
    // PassageHtml the same text with its reading markup (EPUB only).
    string Kind = "note",
    string? PassageTitle = null,
    string? PassageEnd = null,
    string? PassageHtml = null
);

// HighlightColour APPENDED (issue #861). Null leaves the stored pen unchanged,
// so a client that only edits text never clears a colour.
// PassageTitle APPENDED (issue #1014): null leaves it unchanged. A passage's
// text and ends are fixed when it is saved.
public record UpdateNoteDto(
    string Content,
    string? SelectedText = null,
    string? HighlightColour = null,
    string? PassageTitle = null);

public record NoteLocationDto(string? CfiRange);

/// <summary>
/// A note found by searching note text, or listed because it belongs to no topic
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
    IReadOnlyList<string> TopicNames,
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
    bool HasRawContent = false,
    // APPENDED (issue #1014): a passage. SelectedText is only its opening.
    string Kind = "note",
    string? PassageTitle = null,
    string? PassageEnd = null,
    int? PassageWordCount = null,
    // APPENDED (issue #1038): the book's author, so a quote can name who said it.
    string? BookAuthor = null,
    // APPENDED (Brain rebuild): the highlighter pen, by name, so a listed note
    // draws its quote in the colour it was highlighted with.
    string? HighlightColour = null,
    // APPENDED: the format of the edition the note was made in, to draw its glyph:
    // "audiobook", "pdf", "ebook" or "physical". Null when the book row is gone.
    string? BookFormat = null,
    // APPENDED (#1302): how many editions the note's book has, so a card names
    // the edition's format only where there is more than one. 1 when the book
    // is alone or its row is gone.
    int EditionCount = 1,
    // APPENDED (#1302): the note's book (work), what the Brain's Book filter keys on.
    Guid? WorkId = null
);

/// <summary>
/// One passage in full, for the passage viewer (issue #1014). The list DTOs
/// carry only a passage's opening, so a page of notes never ships whole
/// chapters; the viewer fetches this when it opens.
/// </summary>
public record PassageDto(
    Guid Id,
    Guid BookId,
    string? BookTitle,
    string Title,
    // epub_cfi | pdf_page | audio_timestamp: the terms Start and End are in.
    string AnchorKind,
    string Start,
    string End,
    string? Html,
    string? Text,
    int WordCount,
    string Content,
    string? HighlightColour,
    DateTime CreatedAt,
    // APPENDED (issue #1038): the book's author, for the quote's attribution.
    string? BookAuthor = null
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

/// <summary>How many notes each of the Brain's fixed places holds.</summary>
public record NoteSummaryDto(int Total, int Passages, int Unsorted);

/// <summary>
/// A book that has notes in one place of the Brain, with how many. A book with
/// several editions is one entry, its notes counted across all of them:
/// <c>BookId</c> is then one of its editions and <c>WorkId</c> is what the
/// Brain filters by. <c>WorkId</c> is null for a note whose book row is gone.
/// </summary>
public record NoteBookFacetDto(
    Guid BookId,
    string BookTitle,
    string? BookAuthor,
    int NoteCount,
    // APPENDED (#1302)
    Guid? WorkId = null);

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

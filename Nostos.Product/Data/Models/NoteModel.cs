using System.ComponentModel.DataAnnotations;
using Nostos.Backend.Services.Notes;

namespace Nostos.Backend.Data.Models;

public class NoteModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Content { get; set; } = string.Empty; // User's written note (if any)

    public string? CfiRange { get; set; } // e.g. "epubcfi(/6/4...)"
    public string? SelectedText { get; set; }

    // The highlighter pen this highlight was made with: amber | sage | blue | rose
    // (see NoteHighlightColours). A NAME, never a hex. Null = no pen was ever
    // recorded (a highlight older than this field, or a note that is not a
    // highlight); readers draw that in the default pen, amber.
    [MaxLength(16)]
    public string? HighlightColour { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid BookId { get; set; }
    public BookModel? Book { get; set; }

    public ICollection<NoteTopicModel> NoteTopics { get; set; } = new List<NoteTopicModel>();

    // Assistant capture provenance. These fields record where a captured note
    // came from and how it was processed, while allowing RawContent to preserve
    // the user's original words alongside processed Content.

    // Null = no separate raw capture was kept.
    public string? RawContent { get; set; }

    // text | voice | import
    public string CaptureSource { get; set; } = "text";

    // verbatim | light_polish | clarify
    public string ProcessingMode { get; set; } = "verbatim";

    // epub_cfi | pdf_page | audio_timestamp | physical_page | external_audio_timestamp | unknown
    public string SourceAnchorKind { get; set; } = "unknown";

    public string? SourceAnchorValue { get; set; }

    public bool AnchorVerified { get; set; }

    // A passage (issue #1014) is a very big highlight: a stretch of the book
    // between two ends, saved to be read again on its own. note | passage.
    // A passage starts at its typed anchor (SourceAnchorKind/Value: epub_cfi,
    // pdf_page or audio_timestamp) and ends at PassageEnd, in the same terms.
    // CfiRange stays null, so no reader paints it as a highlight.
    [MaxLength(16)]
    public string Kind { get; set; } = NoteKinds.Note;

    [MaxLength(300)]
    public string? PassageTitle { get; set; }

    [MaxLength(1024)]
    public string? PassageEnd { get; set; }

    // The passage's text with its reading markup (sanitised to an allow-list on
    // save), for the passage viewer. SelectedText holds the same text plain, so
    // search and retrieval read a passage like any highlight. Null for a PDF or
    // audio passage, whose viewer shows the pages or plays the clip instead.
    public string? PassageHtml { get; set; }
}

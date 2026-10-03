using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

public class NoteModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string Content { get; set; } = string.Empty; // User's written note (if any)

    public string? CfiRange { get; set; } // e.g. "epubcfi(/6/4...)"
    public string? SelectedText { get; set; }

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
}

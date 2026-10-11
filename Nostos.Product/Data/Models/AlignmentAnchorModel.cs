namespace Nostos.Backend.Data.Models;

/// <summary>
/// Regeneratable audiobook-to-EPUB correspondence.
/// Valid only for the exact media revisions recorded here.
/// Never included in portable library archives.
/// </summary>
public sealed class AlignmentAnchorModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AudioBookId { get; set; }
    public BookModel AudioBook { get; set; } = null!;

    public Guid TextBookId { get; set; }
    public BookModel TextBook { get; set; } = null!;

    public string AudioRevision { get; set; } = string.Empty;
    public string TextRevision { get; set; } = string.Empty;

    public long AudioPositionMs { get; set; }
    public int TextGlobalOffset { get; set; }

    public int SpineIndex { get; set; }
    public string ResourceHref { get; set; } = string.Empty;
    public string? Cfi { get; set; }
    public int? TextOffset { get; set; }

    public double Score { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

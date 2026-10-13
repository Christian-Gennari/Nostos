using System.ComponentModel.DataAnnotations;

namespace Nostos.Backend.Data.Models;

/// <summary>
/// A book the user wants to read and does not have yet.
///
/// Deliberately NOT a <see cref="BookModel"/>: a wish has no format, no file
/// and no reading state, and the library means "books I have". Keeping wishes
/// in their own table means no library query, count or export has to remember
/// to leave them out. An entry becomes a book only when the user says they got
/// it, at which point the entry is removed.
/// </summary>
public class ReadingListEntryModel
{
    public const int MaxTitleLength = 500;
    public const int MaxAuthorLength = 500;
    public const int MaxPublishedDateLength = 32;
    public const int MaxWhyLength = 2000;
    public const int MaxRecommendedByLength = 300;

    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(MaxTitleLength)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(MaxAuthorLength)]
    public string? Author { get; set; }

    /// <summary>Normalized ISBN (see <c>BookIdentityNormalizer</c>), or null.</summary>
    [MaxLength(32)]
    public string? Isbn { get; set; }

    [MaxLength(MaxPublishedDateLength)]
    public string? PublishedDate { get; set; }

    /// <summary>The user's own reason for wanting to read it.</summary>
    [MaxLength(MaxWhyLength)]
    public string? Why { get; set; }

    /// <summary>Who or what recommended it, in the user's words.</summary>
    [MaxLength(MaxRecommendedByLength)]
    public string? RecommendedBy { get; set; }

    /// <summary>
    /// The entry's slot on the short, ordered "Next up" shelf, lowest first.
    /// Null means "Someday": the long tail, which has no order to maintain.
    /// One nullable column so an entry cannot be on the shelf without a slot.
    /// </summary>
    public int? NextPosition { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

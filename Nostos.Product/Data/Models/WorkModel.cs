namespace Nostos.Backend.Data.Models;

public class WorkModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string NormalizedTitle { get; set; } = string.Empty;
    public string? NormalizedAuthor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // --- What you say about the book ---
    // These belong to the book, not to one copy of it, so they live here and
    // every edition shows the same values. A single edition can still carry its
    // own rating or review (ReadingProgress.Rating / PersonalReview) as an
    // exception; 0 / null there means "none". Reading status is never stored:
    // it is calculated from the editions (see WorkReadingStatus).
    [System.ComponentModel.DataAnnotations.Range(0, 5)]
    public int Rating { get; set; } = 0;
    public bool IsFavorite { get; set; } = false;
    public string? PersonalReview { get; set; }

    public ICollection<BookModel> Books { get; set; } = new List<BookModel>();
}

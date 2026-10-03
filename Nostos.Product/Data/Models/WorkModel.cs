namespace Nostos.Backend.Data.Models;

public class WorkModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string NormalizedTitle { get; set; } = string.Empty;
    public string? NormalizedAuthor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<BookModel> Books { get; set; } = new List<BookModel>();
}

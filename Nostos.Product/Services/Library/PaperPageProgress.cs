using System.Text.Json;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Where a reader is in a paper book (issue #1091). The book detail page stores
/// the page in <see cref="ReadingProgress.LastLocation"/> as <c>{"page":n}</c>
/// and the percentage in <see cref="ReadingProgress.ProgressPercent"/>, through
/// the readers' progress endpoint. Without a page count the percentage is 1, so
/// the Library still counts the book as Reading.
/// </summary>
public static class PaperPageProgress
{
    /// <summary>The page in a paper book's location, or null when it holds none.</summary>
    public static int? ReadPage(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || !location.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(location);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("page", out var page) &&
                   page.ValueKind == JsonValueKind.Number &&
                   page.TryGetInt32(out var value) && value >= 1
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The PDF reader's rule: floor(page / pageCount * 100), and 1 without a count.</summary>
    public static int Percent(int page, int? pageCount)
    {
        if (pageCount is not > 0)
            return 1;
        var percent = (int)Math.Floor(Math.Min(page, pageCount.Value) * 100.0 / pageCount.Value);
        return Math.Clamp(percent, 1, 100);
    }

    /// <summary>
    /// A changed page count changes what the stored page means. A finished book
    /// stays finished, and a book without a stored page is left alone. Last read
    /// does not move: editing details is not reading.
    /// </summary>
    public static void RecomputeAfterPageCountChange(PhysicalBookModel book, DateTime now)
    {
        if (book.Progress.FinishedAt is not null)
            return;
        var page = ReadPage(book.Progress.LastLocation);
        if (page is null)
            return;

        book.Progress.ProgressPercent = Percent(page.Value, book.PageCount);
        if (book.Progress.ProgressPercent >= 100)
            book.Progress.FinishedAt = now;
    }
}

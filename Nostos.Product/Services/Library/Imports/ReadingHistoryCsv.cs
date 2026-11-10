using System.Globalization;
using System.Text;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// What both reading-history exports share: an RFC 4180 CSV with a header row
/// whose columns are found by name, and dates written <c>YYYY/MM/DD</c>.
/// </summary>
internal static class ReadingHistoryCsv
{
    internal const string EmptyReason = "The row has no book title.";

    public static int Column(IReadOnlyList<string> header, params string[] names)
    {
        for (var i = 0; i < header.Count; i++)
        {
            foreach (var name in names)
            {
                if (string.Equals(header[i].Trim().TrimStart('\uFEFF'), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    public static string? Cell(IReadOnlyList<string> record, int index) =>
        index >= 0 && index < record.Count && !string.IsNullOrWhiteSpace(record[index])
            ? record[index].Trim()
            : null;

    /// <summary>Goodreads wraps identifiers as <c>="9780000000019"</c>.</summary>
    public static string? UnwrapIdentifier(string? value)
    {
        var text = value?.Trim();
        if (text is null)
            return null;
        text = text.TrimStart('=');
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// <c>YYYY/MM/DD</c> in both services' public examples; StoryGraph's help
    /// tables sometimes show <c>YYYY-MM-DD</c>, so both separators read.
    /// </summary>
    public static DateTime? Date(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        string[] formats = ["yyyy/MM/dd", "yyyy-MM-dd", "yyyy/M/d", "yyyy-M-d"];
        return DateTime.TryParseExact(
                text,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    /// <summary>
    /// The finish date of a row that may name several reads, as StoryGraph's
    /// <c>Dates Read</c> does: <c>2023/08/27-2023/09/14, 2024/01/01-2024/01/02</c>.
    /// The last range's end (a lone date is its own end) is the last read.
    /// </summary>
    public static DateTime? LastRead(IEnumerable<string?> ranges)
    {
        DateTime? last = null;
        foreach (var raw in ranges)
        {
            if (raw is null)
                continue;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // A range may come with or without a start; the end is what
                // matters here.
                var ends = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var date = ends.Length == 0 ? null : Date(ends[^1]);
                if (date is { } parsed && (last is null || parsed > last))
                    last = parsed;
            }
        }

        return last;
    }

    /// <summary>
    /// A comma-separated shelf or tag list, trimmed, deduplicated and with the
    /// exclusive shelf removed: Goodreads sometimes repeats "read" there.
    /// </summary>
    public static IReadOnlyList<string> Shelves(string? value, string? exclusive = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shelves = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (exclusive is not null && string.Equals(part, exclusive, StringComparison.OrdinalIgnoreCase))
                continue;
            if (seen.Add(part))
                shelves.Add(part);
        }

        return shelves;
    }

    /// <summary>
    /// Nostos stores a whole-star rating (<see cref="Data.Models.WorkModel.Rating"/>).
    /// StoryGraph's quarter stars are rounded to the nearest star; the preview
    /// says what happened so the owner is not surprised.
    /// </summary>
    public static (int Rating, string? Notice) Rating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (0, null);
        var text = value.Trim();
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            return (0, null);
        if (parsed <= 0)
            return (0, null);

        var clamped = Math.Clamp(parsed, 0m, 5m);
        var rounded = (int)Math.Round(clamped, MidpointRounding.AwayFromZero);
        var notice = rounded == clamped
            ? null
            : $"{clamped:0.##} stars is stored as {rounded}.";
        return (rounded, notice);
    }

    public static int Count(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;

    public static string Status(string? value)
    {
        var text = value?.Trim().ToLowerInvariant().Replace('_', ' ');
        return text switch
        {
            "read" => ReadingHistoryStatus.Read,
            "to-read" or "to read" or "want to read" => ReadingHistoryStatus.ToRead,
            "currently-reading" or "currently reading" => ReadingHistoryStatus.CurrentlyReading,
            "did-not-finish" or "did not finish" or "dnf" => ReadingHistoryStatus.DidNotFinish,
            "paused" => ReadingHistoryStatus.Paused,
            _ => ReadingHistoryStatus.Unknown,
        };
    }

    /// <summary>
    /// The column layout the parser expects, by name. Missing columns are not an
    /// error: an export with more, fewer or reordered columns still reads.
    /// </summary>
    public static string HeaderText(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).TrimStart('\uFEFF');
    }

    /// <summary>
    /// A bounded source identity inside one export: the service's own book id
    /// when it has one, else the ISBN, else title and author. Stable across
    /// re-imports of the same file, which is what undo and result rows key on.
    /// </summary>
    public static string Key(string source, string? serviceId, string? isbn, string? title, string? author)
    {
        if (!string.IsNullOrWhiteSpace(serviceId))
            return $"{source}:id:{serviceId.Trim()}";
        if (!string.IsNullOrWhiteSpace(isbn))
            return $"{source}:isbn:{BookIdentityNormalizer.NormalizeIsbn(isbn) ?? isbn.Trim().ToUpperInvariant()}";
        return $"{source}:meta:{BookIdentityNormalizer.NormalizeTitle(title)}|{BookIdentityNormalizer.NormalizeAuthor(author)}";
    }

    public static string Hash(string canonical) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

/// <summary>Rows a file holds that cannot become a book, tallied by reason.</summary>
internal sealed class ReadingHistorySkipTally
{
    private readonly Dictionary<(string? Title, string? Author, string Reason), int> _counts = [];
    private readonly List<(string? Title, string? Author, string Reason)> _order = [];

    public void Add(string? title, string? author, string reason)
    {
        var key = (title, author, reason);
        if (_counts.TryGetValue(key, out var count))
        {
            _counts[key] = count + 1;
            return;
        }

        _counts[key] = 1;
        _order.Add(key);
    }

    public List<ReadingHistorySkipped> ToList() =>
        _order.Select(key => new ReadingHistorySkipped(key.Title, key.Author, _counts[key], key.Reason)).ToList();
}

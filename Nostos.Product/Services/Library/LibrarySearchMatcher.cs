using System.Globalization;
using System.Text;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Pure, deterministic matching for the Library search box. No EF, no IO.
/// It runs in memory on purpose: SQL <c>LIKE</c> is case-sensitive on
/// PostgreSQL and folds only ASCII on SQLite, so the same search gave
/// different answers per deployment and missed "tolkien" for "Tolkien".
/// </summary>
public static class LibrarySearchMatcher
{
    /// <summary>Shorter words are matched literally; a typo in "war" is another word.</summary>
    private const int MinTypoTokenLength = 4;

    /// <summary>From this length a word may carry two typos instead of one.</summary>
    private const int TwoTypoTokenLength = 8;

    /// <summary>
    /// Returns the items whose title/author match every word of
    /// <paramref name="search"/>, in their original order. Words may appear in
    /// any order and in either field. Typo tolerance is a fallback: it is used
    /// only when no item contains the words as typed, so a correctly spelled
    /// search never gains near-miss noise.
    /// </summary>
    public static IReadOnlyList<T> Match<T>(
        string? search,
        IEnumerable<T> items,
        Func<T, string?> title,
        Func<T, string?> author)
    {
        var tokens = Fold(search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return [];

        var folded = items
            .Select(item => (Item: item, Text: Fold(title(item)) + " " + Fold(author(item))))
            .ToList();

        var literal = folded
            .Where(f => tokens.All(t => f.Text.Contains(t, StringComparison.Ordinal)))
            .Select(f => f.Item)
            .ToList();
        if (literal.Count > 0)
            return literal;

        return folded
            .Where(f =>
            {
                var words = f.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return tokens.All(t =>
                    f.Text.Contains(t, StringComparison.Ordinal) || MatchesWithTypos(t, words));
            })
            .Select(f => f.Item)
            .ToList();
    }

    /// <summary>
    /// Lower-cased, accent-stripped text with every run of punctuation or
    /// whitespace collapsed to one space, so "Brontë" and "bronte", or
    /// "Catch-22" and "catch 22", compare equal.
    /// </summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (!char.IsLetterOrDigit(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private static bool MatchesWithTypos(string token, string[] words)
    {
        if (token.Length < MinTypoTokenLength)
            return false;

        var allowed = token.Length >= TwoTypoTokenLength ? 2 : 1;
        foreach (var word in words)
        {
            // People rarely mistype the first letter, and requiring it keeps
            // "king" from matching "ring".
            if (word[0] != token[0])
                continue;

            if (EditDistance(token, word) <= allowed)
                return true;

            // The search runs while the user is still typing, so a mistyped
            // beginning of a longer word has to match too.
            if (word.Length > token.Length && EditDistance(token, word[..token.Length]) <= allowed)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Edit distance where a swap of two neighbouring letters counts as one
    /// edit (optimal string alignment), because "hobbti" is one slip, not two.
    /// </summary>
    private static int EditDistance(string a, string b)
    {
        var previous2 = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var best = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    best = Math.Min(best, previous2[j - 2] + 1);
                current[j] = best;
            }

            (previous2, previous, current) = (previous, current, previous2);
        }

        return previous[b.Length];
    }
}

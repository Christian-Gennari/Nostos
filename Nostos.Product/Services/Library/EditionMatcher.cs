using System.Globalization;
using System.Text;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Decides when two books are editions of one work. Pure and deterministic.
///
/// This is deliberately separate from <see cref="BookIdentityNormalizer"/>:
/// that one produces the frozen values stored in <c>Work.NormalizedTitle</c>
/// and mirrored in migrations. This one only compares, so it can be looser
/// without rewriting stored data.
///
/// Two levels:
/// <list type="bullet">
/// <item><see cref="IsAutoMatch"/> joins books on its own: the title agrees
/// ignoring case, accents, punctuation and a subtitle after ";" or ":", and the
/// author agrees ignoring case, accents and punctuation. Nothing fuzzier.</item>
/// <item><see cref="IsSuggestion"/> only OFFERS a link: the title agrees the
/// same way and the authors look like one person written differently (a
/// missing middle word, a different order, a one-letter spelling slip).
/// A person confirms it; it is never applied silently.</item>
/// </list>
/// </summary>
public static class EditionMatcher
{
    public static bool IsAutoMatch(string? titleA, string? authorA, string? titleB, string? authorB)
    {
        var a = AuthorTokens(authorA);
        var b = AuthorTokens(authorB);
        return a.Count > 0 && a.SequenceEqual(b, StringComparer.Ordinal) && SameTitle(titleA, titleB);
    }

    public static bool IsSuggestion(string? titleA, string? authorA, string? titleB, string? authorB)
    {
        var a = AuthorTokens(authorA);
        var b = AuthorTokens(authorB);
        return a.Count > 0 && b.Count > 0 && RelatedTokens(a, b) && SameTitle(titleA, titleB);
    }

    /// <summary>
    /// Same main title, ignoring case, accents and punctuation. A subtitle (the
    /// text after the first ";" or ":") is ignored when either side has none;
    /// when both have one, the two subtitles must agree, so "Dune: Messiah" and
    /// "Dune: Chapterhouse" stay two books.
    /// </summary>
    public static bool SameTitle(string? a, string? b)
    {
        var (mainA, subA) = SplitTitle(a);
        var (mainB, subB) = SplitTitle(b);
        if (mainA.Length == 0 || !string.Equals(mainA, mainB, StringComparison.Ordinal))
            return false;
        return subA.Length == 0 || subB.Length == 0 || string.Equals(subA, subB, StringComparison.Ordinal);
    }

    internal static (string Main, string Subtitle) SplitTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return (string.Empty, string.Empty);

        var cut = title.IndexOfAny([';', ':']);
        if (cut > 0)
        {
            var main = Fold(title[..cut]);
            if (main.Length > 0)
                return (main, Fold(title[(cut + 1)..]));
        }

        return (Fold(title), string.Empty);
    }

    internal static List<string> AuthorTokens(string? author) =>
        Fold(author).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>
    /// Upper-cased letters and digits only, accents removed, every other run
    /// of characters one space.
    /// </summary>
    internal static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        var gap = false;
        foreach (var ch in value.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (!char.IsLetterOrDigit(ch))
            {
                gap = true;
                continue;
            }

            if (gap && sb.Length > 0)
                sb.Append(' ');
            gap = false;
            sb.Append(char.ToUpperInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Every word of the shorter name has a counterpart in the longer one,
    /// equal or within a small spelling distance. Covers "Marcus Aurelius" in
    /// "Emperor of Rome Marcus Aurelius", "Last First" order, and "Shelly" for
    /// "Shelley" or "Dostoievski" for "Dostoyevsky".
    /// </summary>
    private static bool RelatedTokens(List<string> a, List<string> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var free = new List<string>(large);
        foreach (var token in small)
        {
            var index = free.FindIndex(other => SimilarToken(token, other));
            if (index < 0)
                return false;
            free.RemoveAt(index);
        }

        return true;
    }

    private static bool SimilarToken(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        // Initials and short words must match exactly.
        if (Math.Min(a.Length, b.Length) < 4)
            return false;
        var allowed = Math.Min(a.Length, b.Length) >= 8 ? 2 : 1;
        return Distance(a, b, allowed) <= allowed;
    }

    /// <summary>Levenshtein distance, giving up (returning limit + 1) past <paramref name="limit"/>.</summary>
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
            return limit + 1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > limit)
                return limit + 1;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

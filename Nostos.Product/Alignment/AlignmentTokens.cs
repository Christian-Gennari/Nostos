using System.Text.RegularExpressions;

namespace Nostos.Product.Alignment;

/// <summary>One matchable unit of text and where it sits in its source string.</summary>
public readonly record struct AlignmentToken(string Value, int Index, int Length);

/// <summary>
/// Splits text into the units the passage matcher compares. A run of letters
/// and digits is one token. In scripts written without spaces between words
/// (Chinese, Japanese, Thai, Lao, Khmer, Burmese) every character is its own
/// token: there is no word boundary to find, and a transcript and a book that
/// agree character by character are the same passage.
///
/// Chinese is printed in two character sets, and a transcript rarely uses the
/// one the book does. Every Traditional character is therefore compared as its
/// Simplified form; nothing shown to a reader is changed.
/// </summary>
public static partial class AlignmentTokens
{
    private const string Unspaced =
        @"\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}" +
        @"\p{IsCJKCompatibilityIdeographs}\p{IsHiragana}\p{IsKatakana}" +
        @"\p{IsThai}\p{IsLao}\p{IsKhmer}\p{IsMyanmar}";

    [GeneratedRegex(
        "[" + Unspaced + @"-[\P{L}]]|[\p{L}\p{N}-[" + Unspaced + "]]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex Units();

    private static readonly Lazy<Dictionary<char, string>> Simplified = new(LoadSimplified);

    private static Dictionary<char, string> LoadSimplified()
    {
        var map = new Dictionary<char, string>();

        using var stream = typeof(AlignmentTokens).Assembly.GetManifestResourceStream(
            "Nostos.Product.Alignment.han-traditional-to-simplified.txt")
            ?? throw new InvalidOperationException(
                "The Chinese character fold is missing from the assembly.");

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            // "繁<TAB>简"; comment lines start with '#'.
            if (line.Length == 3 && line[1] == '\t')
                map[line[0]] = line[2].ToString();
        }

        return map;
    }

    public static List<AlignmentToken> Split(string text)
    {
        var tokens = new List<AlignmentToken>();

        foreach (Match unit in Units().Matches(text))
        {
            var value = unit.Length == 1 &&
                Simplified.Value.TryGetValue(unit.ValueSpan[0], out var folded)
                    ? folded
                    : unit.Value.ToLowerInvariant();

            tokens.Add(new AlignmentToken(value, unit.Index, unit.Length));
        }

        return tokens;
    }

    /// <summary>True for a token that is one character of an unspaced script.</summary>
    public static bool IsUnspaced(string token) =>
        token.Length == 1 && IsUnspaced(token[0]);

    public static bool IsUnspaced(char character) =>
        character is
            (>= '一' and <= '鿿') or   // CJK unified ideographs
            (>= '㐀' and <= '䶿') or   // extension A
            (>= '豈' and <= '﫿') or   // compatibility ideographs
            (>= '぀' and <= 'ヿ') or   // hiragana, katakana
            (>= '฀' and <= '໿') or   // Thai, Lao
            (>= 'ក' and <= '៿') or   // Khmer
            (>= 'က' and <= '႟');     // Myanmar

    /// <summary>True when most of the tokens are characters of an unspaced script.</summary>
    public static bool MostlyUnspaced(IReadOnlyCollection<AlignmentToken> tokens) =>
        tokens.Count > 0 &&
        tokens.Count(token => IsUnspaced(token.Value)) * 2 > tokens.Count;
}

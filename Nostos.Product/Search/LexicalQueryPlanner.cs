using System.Text.RegularExpressions;

namespace Nostos.Backend.Search;

/// <summary>
/// Deterministic, provider-free lexical decomposition shared by note, topic
/// and unified knowledge retrieval.
///
/// The original phrase stays first. A small number of content-word and adjacent
/// content-word variants follow so a new wording can still surface material
/// that shares only part of the phrase. This is deliberately lexical: semantic
/// expansion remains an optional future contributor, not a hidden LLM call.
/// </summary>
public static partial class LexicalQueryPlanner
{
    public const int DefaultMaxVariants = 8;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English question/function words.
        "the", "and", "that", "this", "with", "from", "into", "about", "what",
        "when", "where", "which", "who", "why", "how", "does", "did", "have",
        "has", "had", "was", "were", "are", "for", "you", "your", "our", "their",
        "there", "here", "can", "could", "would", "should", "than", "then", "also",
        "not", "but", "all", "any", "some", "more", "most", "very", "just",

        // Common Swedish equivalents; the app is routinely used bilingually.
        "och", "att", "det", "den", "som", "med", "från", "till", "om", "vad",
        "när", "var", "vilken", "vilket", "hur", "har", "hade", "är", "var",
        "för", "jag", "du", "min", "mitt", "mina", "din", "ditt", "dina", "inte",
        "men", "alla", "någon", "något", "mer", "mest", "bara",
    };

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public static IReadOnlyList<LexicalQueryVariant> Build(
        string? query,
        int maxVariants = DefaultMaxVariants)
    {
        maxVariants = Math.Clamp(maxVariants, 1, 16);
        var normalized = Normalize(query);
        if (normalized.Length == 0)
            return [];

        var variants = new List<LexicalQueryVariant>(maxVariants);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string text, int weight, LexicalQueryVariantKind kind)
        {
            text = Normalize(text);
            if (text.Length == 0 || variants.Count >= maxVariants || !seen.Add(text))
                return;
            variants.Add(new LexicalQueryVariant(text, weight, kind));
        }

        Add(normalized, 4, LexicalQueryVariantKind.Phrase);

        var contentTokens = TokenRegex()
            .Matches(normalized)
            .Select(match => match.Value.Trim('\'', '’', '-'))
            .Where(token => token.Length >= 3 && !StopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        // Recall comes first: individual content words must get a chance
        // before precision-oriented pairs consume the bounded variant budget.
        // This is especially important when the user's wording only overlaps a
        // stored note on a later term in a longer question.
        foreach (var token in contentTokens)
            Add(token, 1, LexicalQueryVariantKind.Token);

        // Use any remaining budget for adjacent content-word pairs. They add
        // precision only after every retained independent content word has had
        // a chance to contribute recall; bounded pair expansion must not crowd
        // a later query term out of the candidate set.
        for (var i = 0; i + 1 < contentTokens.Count && variants.Count < maxVariants; i++)
            Add($"{contentTokens[i]} {contentTokens[i + 1]}", 2, LexicalQueryVariantKind.Pair);

        return variants;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                ' ',
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public enum LexicalQueryVariantKind
{
    Phrase,
    Pair,
    Token,
}

public sealed record LexicalQueryVariant(
    string Text,
    int Weight,
    LexicalQueryVariantKind Kind);

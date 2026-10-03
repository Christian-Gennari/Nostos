namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Small deterministic language policy for turn-level retrieval truth.
///
/// This does not choose tools or answer questions. It only distinguishes:
/// 1) an explicit request to locate the user's own material from an incidental
///    opportunistic search, and
/// 2) clearly bibliographic book questions from source-content questions.
///
/// Keep this conservative. A false "metadata is enough" decision is worse than
/// surfacing a truthful no-evidence state for a source-content request.
/// </summary>
internal static class AssistantRetrievalIntentPolicy
{
    private static readonly HashSet<string> BookTextRecoveryFillers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "mean",
            "meant",
            "actually",
            "basically",
            "really",
        };

    public static bool IsExplicitMaterialLookup(string? message)
    {
        var normalized = Normalize(message);
        if (normalized.Length == 0)
            return false;

        return normalized.StartsWith("find ", StringComparison.Ordinal)
            || normalized.StartsWith("search ", StringComparison.Ordinal)
            || normalized.StartsWith("hitta ", StringComparison.Ordinal)
            || normalized.StartsWith("sök ", StringComparison.Ordinal)
            || normalized.Contains("my notes", StringComparison.Ordinal)
            || normalized.Contains("my library", StringComparison.Ordinal)
            || normalized.Contains("my books", StringComparison.Ordinal)
            || normalized.Contains("what did i write", StringComparison.Ordinal)
            || normalized.Contains("what have i written", StringComparison.Ordinal)
            || normalized.Contains("mina anteckningar", StringComparison.Ordinal)
            || normalized.Contains("mitt bibliotek", StringComparison.Ordinal)
            || normalized.Contains("mina böcker", StringComparison.Ordinal)
            || normalized.Contains("vad skrev jag", StringComparison.Ordinal)
            || normalized.Contains("vad har jag skrivit", StringComparison.Ordinal);
    }

    public static bool IsClearlyBookMetadataRequest(string? message)
    {
        var normalized = Normalize(message);
        if (normalized.Length == 0)
            return false;

        return normalized.Contains("what book", StringComparison.Ordinal)
            || normalized.Contains("which book", StringComparison.Ordinal)
            || normalized.Contains("book is this", StringComparison.Ordinal)
            || normalized.Contains("book was that", StringComparison.Ordinal)
            || normalized.Contains("who wrote", StringComparison.Ordinal)
            || normalized.Contains("author of", StringComparison.Ordinal)
            || normalized.Contains("book title", StringComparison.Ordinal)
            || normalized.Contains("isbn", StringComparison.Ordinal)
            || normalized.Contains("which edition", StringComparison.Ordinal)
            || normalized.Contains("what edition", StringComparison.Ordinal)
            || normalized.Contains("book format", StringComparison.Ordinal)
            || normalized.Contains("vilken bok", StringComparison.Ordinal)
            || normalized.Contains("vad är det för bok", StringComparison.Ordinal)
            || normalized.Contains("vem skrev", StringComparison.Ordinal)
            || normalized.Contains("författaren", StringComparison.Ordinal)
            || normalized.Contains("bokens titel", StringComparison.Ordinal)
            || normalized.Contains("vilken utgåva", StringComparison.Ordinal)
            || normalized.Contains("bokformat", StringComparison.Ordinal);
    }

    public static bool IsBookTextRecoveryFiller(string token) =>
        BookTextRecoveryFillers.Contains(token);

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                ' ',
                value.Trim()
                    .ToLowerInvariant()
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

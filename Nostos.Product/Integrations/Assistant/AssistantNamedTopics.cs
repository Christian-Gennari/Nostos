using System.Text.RegularExpressions;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Which topics the reader named in their own words (issue #1770). A topic kind
/// the reader set is theirs, so only a topic the reader actually named may have
/// it replaced or cleared.
///
/// A whole-phrase match on word boundaries, not a substring one: a topic named
/// “Man” is not named by “many”, “Art” is not named by “particular”, and “US” is
/// not named by “use”. Both sides are folded exactly as the library folds topic
/// names (<see cref="TopicModel.NormalizeName"/>: trimmed, NFC, invariant upper
/// case), so “Café” matches “café” and “CAFÉ” but not “cafe”.
/// </summary>
internal static class AssistantNamedTopics
{
    /// <summary>
    /// Ordinary words a request for this kind of work uses about itself, not
    /// about a topic. “Give a kind to my topics” contains the word “kind”, so a
    /// topic called Kind must not be counted as named by it. The model's own
    /// named-topic list is held to the same rule: it cannot talk the server
    /// into treating the request's own vocabulary as a name.
    /// </summary>
    private static readonly HashSet<string> ScaffoldingWords = new(StringComparer.Ordinal)
    {
        "KIND", "KINDS", "TOPIC", "TOPICS", "NEW", "ONLY", "MANY", "USE",
        "ALL", "ANY", "SOME", "MORE", "MOST", "ONE", "THE", "AND",
    };

    /// <summary>
    /// The topics the model says the reader named, kept only where the reader's
    /// own words really contain the name as a whole token. Unknown ids are
    /// dropped. There is no fall back to reading the prose here: an omitted
    /// list means the reader named nothing, so every reader kind is protected.
    ///
    /// There is no minimum length: the model has already identified the topic
    /// by id, so “AI” or “1984” is verified whenever it occurs in the text as a
    /// whole token. The scaffolding-word rule still applies, so a model that
    /// lists a topic called Kind cannot make the request's own vocabulary a name.
    /// </summary>
    public static IReadOnlySet<Guid> Verified<T>(
        IEnumerable<Guid>? namedTopicIds,
        IReadOnlyDictionary<Guid, T> topics,
        Func<T, string?> nameOf,
        string? userText)
    {
        if (namedTopicIds is null || string.IsNullOrWhiteSpace(userText))
            return new HashSet<Guid>();

        return namedTopicIds
            .Distinct()
            .Where(topics.ContainsKey)
            .Where(topicId =>
            {
                var name = TopicModel.NormalizeName(nameOf(topics[topicId]));
                return name.Length > 0
                    && !ScaffoldingWords.Contains(name)
                    && OccursAsWholeToken(userText, name);
            })
            .ToHashSet();
    }

    /// <summary>
    /// The name as it appears in the reader's words: a whole phrase, not a
    /// substring, and with any whitespace between its words. “Paulo Freire” is
    /// named by “Paulo  Freire” and by “Paulo\nFreire” as well as by one space,
    /// because a reader who wraps a line or types two spaces still means the
    /// same topic.
    /// </summary>
    private static bool OccursAsWholeToken(string userText, string name)
    {
        var text = TopicModel.NormalizeName(userText);
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape);
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){string.Join(@"\s+", words)}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant);
    }
}

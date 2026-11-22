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
/// names (<see cref="TopicModel.NormalizeName"/>: trimmed, NFC, invariant
/// upper case), so “Café” matches “café” and “CAFÉ” but not “cafe”.
/// </summary>
internal static class AssistantNamedTopics
{
    /// <summary>
    /// A name with fewer letters than this is never “named”: “US”, “AI” and “A”
    /// are too easy to hit by accident in ordinary prose.
    /// </summary>
    internal const int MinimumLetters = 3;

    /// <summary>
    /// Ordinary words a request for this kind of work uses about itself, not
    /// about a topic. “Give a kind to my topics” contains the word “kind”, so a
    /// topic called Kind must not be counted as named by it. The model's own
    /// named-topic list is not subject to this: it can name a topic called Kind
    /// when the reader really did.
    /// </summary>
    private static readonly HashSet<string> ScaffoldingWords = new(StringComparer.Ordinal)
    {
        "KIND", "KINDS", "TOPIC", "TOPICS", "NEW", "ONLY", "MANY", "USE",
        "ALL", "ANY", "SOME", "MORE", "MOST", "ONE", "THE", "AND",
    };

    /// <summary>
    /// True when <paramref name="userText"/> names this topic as a topic. The
    /// conservative rule: a scaffolding word is not a name.
    /// </summary>
    public static bool Names(string? userText, string? topicName) =>
        Matches(userText, topicName, scaffoldingCountsAsName: false);

    private static bool Matches(string? userText, string? topicName, bool scaffoldingCountsAsName)
    {
        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(topicName))
            return false;

        var name = TopicModel.NormalizeName(topicName);
        if (name.Count(char.IsLetter) < MinimumLetters)
            return false;
        if (!scaffoldingCountsAsName && ScaffoldingWords.Contains(name))
            return false;

        var text = TopicModel.NormalizeName(userText);
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The topics the model says the reader named, kept only where the reader's
    /// own words really name them. Unknown ids are dropped, and the same
    /// conservative rule applies: a model that lists a scaffolding word cannot
    /// talk the server into treating the request's own vocabulary as a name.
    /// </summary>
    public static IReadOnlySet<Guid> Verified(
        IEnumerable<Guid>? namedTopicIds,
        IReadOnlyDictionary<Guid, TopicModel> topics,
        string? userText)
    {
        if (namedTopicIds is null || string.IsNullOrWhiteSpace(userText))
            return new HashSet<Guid>();

        return namedTopicIds
            .Distinct()
            .Where(topics.ContainsKey)
            .Where(topicId => Names(userText, topics[topicId].Topic))
            .ToHashSet();
    }
}

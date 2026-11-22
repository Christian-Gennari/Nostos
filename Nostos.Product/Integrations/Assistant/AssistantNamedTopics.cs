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
    /// A name with fewer letters than this is never <em>guessed</em>: “US”,
    /// “AI” and “A” are too easy to hit by accident in ordinary prose. This
    /// only applies when Nostos itself reads the prose; a topic id the model
    /// passed is verified as an exact whole token, however short or numeric.
    /// </summary>
    internal const int MinimumLetters = 3;

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
    /// True when <paramref name="userText"/> is enough on its own to guess that
    /// this topic was named: a whole word, at least three letters, and not one
    /// of the request's scaffolding words. Production no longer guesses from the
    /// prose — an omitted named-topic list means no topic was named — so this
    /// is the conservative rule the model's list is checked against, kept
    /// separate and tested as such.
    /// </summary>
    public static bool Names(string? userText, string? topicName) =>
        Matches(userText, topicName, minimumLetters: MinimumLetters);

    private static bool Matches(string? userText, string? topicName, int minimumLetters)
    {
        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(topicName))
            return false;

        var name = TopicModel.NormalizeName(topicName);
        if (name.Count(char.IsLetter) < minimumLetters)
            return false;
        if (ScaffoldingWords.Contains(name))
            return false;

        return OccursAsWholeToken(userText, name);
    }

    private static bool OccursAsWholeToken(string userText, string name)
    {
        var text = TopicModel.NormalizeName(userText);
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])";
        return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The topics the model says the reader named, kept only where the reader's
    /// own words really contain the name as a whole token. Unknown ids are
    /// dropped. There is no fall back to reading the prose here: an omitted
    /// list means the reader named nothing, so every reader kind is protected.
    ///
    /// Unlike <see cref="Names"/>, this does not impose the three-letter
    /// guessing minimum: the model has already identified the topic by id, so
    /// “AI” or “1984” is verified whenever it occurs in the text as a whole
    /// token. The scaffolding-word rule still applies, so a model that lists a
    /// topic called Kind cannot make the request's own vocabulary a name.
    /// </summary>
    public static IReadOnlySet<Guid> Verified(
        IEnumerable<Guid>? namedTopicIds,
        IReadOnlyDictionary<Guid, TopicModel> topics,
        string? userText) =>
        Verified(namedTopicIds, topics, topic => topic.Topic, userText);

    /// <summary>
    /// The same rule for a read model that is not a <see cref="TopicModel"/>
    /// (the assistant's compact <c>TopicDto</c> page), so the apply path and a
    /// capability both verify ids the same way.
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
}

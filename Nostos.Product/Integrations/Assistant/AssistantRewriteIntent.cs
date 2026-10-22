using System.Text.RegularExpressions;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The user's own request to have a thought tidied up, read from their message.
/// A capture is kept word for word unless the user asked for a rewrite: the
/// model may ask for one (<c>rewrite: true</c>), but only this check, on the
/// user's actual words, lets it through. A thought that merely mentions
/// rewriting ("Mann seems to rewrite Faust") still needs the model to ask, and
/// a model that asks on its own is refused because the user never did.
/// </summary>
internal static partial class AssistantRewriteIntent
{
    [GeneratedRegex(
        @"\b(?:(?:re-?phrase|re-?word|re-?write|re-?formulate|paraphrase)(?:\s+(?:it|this|that|them|these|those|my|the\s+note|please)\b|\s*(?:[.,!?]|$))|clean\s+(?:it|this|that|them|these|the\s+note|my\s+note|my\s+thoughts?)?\s*up|tidy\s+(?:it|this|that|them|the\s+note|my\s+note)?\s*up|polish\s+(?:it|this|that|the\s+note|my\s+note|up)|make\s+(?:it|this|that|the\s+note|them)\s+(?:a\s+bit\s+|more\s+|less\s+)*(?:coherent|clearer|clear|readable|concise|tidier|tidy|cleaner|nicer|better|rambly|rambling)|into\s+(?:something\s+)?(?:more\s+)?(?:coherent|readable|clear)|formulera\s+om|skriv\s+om|omformulera|snygga\s+till|städa\s+upp|gör\s+(?:det|den|om\s+det)\s+(?:mer\s+)?(?:tydlig|tydligare|sammanhängande|läsbar))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RewriteRequestRegex();

    [GeneratedRegex(
        @"\b(?:do\s+not|don['’]?t|dont|never)\s+(?:please\s+)?(?:re-?phrase|re-?word|re-?write|re-?formulate|paraphrase|clean|tidy|polish|make\s+(?:it|this|that))\b|\b(?:inte\s+(?:formulera\s+om|skriv\s+om|omformulera|snygga\s+till|städa\s+upp)|(?:formulera|skriv)\s+inte\s+om)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RewriteRefusalRegex();

    [GeneratedRegex(
        @"\b(?:topics?|tags?|tagga|ämnen?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TopicRequestRegex();

    /// <summary>True when the user's message asks for their words to be rewritten.</summary>
    public static bool Requested(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && !RewriteRefusalRegex().IsMatch(message)
        && RewriteRequestRegex().IsMatch(message);

    /// <summary>True when the user's message mentions topics or tags at all.</summary>
    public static bool TopicsMentioned(string? message) =>
        !string.IsNullOrWhiteSpace(message) && TopicRequestRegex().IsMatch(message);
}

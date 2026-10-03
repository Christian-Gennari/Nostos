using System.Text.RegularExpressions;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The deterministic backstop behind <see cref="AssistantSoul.Prompt"/>. The
/// assistant is routed through a pool of different vendors, each with its own
/// identity training and its own injected system prompt, so "no vendor or model
/// is ever named in a reply" cannot be guaranteed by a prompt alone. This guard
/// enforces it on the final conversational reply: if the assistant asserts a
/// vendor or model identity about itself, the whole reply is replaced with
/// <see cref="CanonicalIdentityLine"/>.
///
/// A bare first-person marker next to a vendor term is deliberately NOT enough
/// to fire. The assistant speaks in the first person about its own actions all
/// the time ("I am linking this note to your Claude Shannon collection"), and
/// replacing a correct, helpful reply with a canned identity line is worse than
/// the leak being guarded against. Firing requires one of three identity shapes:
/// direct self-naming, a vendor-made passive construction, or a creator
/// assertion. A reply that merely mentions a vendor term — a quotation, a note
/// title, a search result — is left untouched. The guard applies to the
/// conversational reply only; note content, quotes, acknowledgements and plan
/// summaries are the user's own words and are never rewritten.
/// </summary>
public static class AssistantIdentityGuard
{
    /// <summary>
    /// The reviewed identity line the guard substitutes when it fires. Exported
    /// so the guard and its tests share one source.
    /// </summary>
    public const string CanonicalIdentityLine =
        "I am the Nostos assistant, here to help you read, think, and work with your library.";

    // Vendor/model terms. GPT- ends in a non-word character, so it is matched as
    // a prefix (GPT-4, GPT-3.5); every other term must end on a word boundary so
    // "Google" does not match "Googling".
    private const string VendorTerm =
        @"(?:Gemini\b|Google\b|OpenAI\b|ChatGPT\b|Claude\b|Anthropic\b|DeepSeek\b|Llama\b|Mistral\b|Copilot\b|Microsoft\b|GPT-)";

    // Sentence boundaries: the brief's set is sufficient for prose.
    private static readonly Regex SentenceSeparator = new(
        @"[.!?\n]",
        RegexOptions.CultureInvariant);

    // Shape 1 — direct self-naming: the copula, an optional article, a term.
    // "I am Gemini", "I'm a Claude". It does not match "I am linking … to your
    // Claude Shannon collection", because the word after the copula is a verb,
    // not an article and not a term.
    private static readonly Regex DirectSelfNaming = new(
        $@"\b(?:I am|I'm)\s+(?:(?:a|an|the)\s+)?(?<vendor>{VendorTerm})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Shape 2 — vendor-made: a passive maker phrase.
    private static readonly Regex VendorMade = new(
        $@"\b(?:built|made|created|trained|developed|powered)\s+by\s+(?<vendor>{VendorTerm})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Shape 2 also requires the sentence to refer to the assistant in the first
    // person ("As an AI built by Anthropic, I can't …").
    private static readonly Regex FirstPersonReference = new(
        @"\b(?:I|I'm|my)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Shape 3 — creator assertion: "My creator is Google."
    private static readonly Regex CreatorAssertion = new(
        $@"\bmy\s+(?:creator|maker|developer|provider|company)\s+is\s+(?<vendor>{VendorTerm})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns the vendor/model term the reply self-asserts, or null when the
    /// reply makes no identity claim involving one. Used to log the matched term
    /// without ever logging the reply text.
    /// </summary>
    public static string? MatchVendorSelfAssertion(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var sentence in SentenceSeparator.Split(text))
        {
            var direct = DirectSelfNaming.Match(sentence);
            if (direct.Success)
            {
                return direct.Groups["vendor"].Value;
            }

            var made = VendorMade.Match(sentence);
            if (made.Success && FirstPersonReference.IsMatch(sentence))
            {
                return made.Groups["vendor"].Value;
            }

            var creator = CreatorAssertion.Match(sentence);
            if (creator.Success)
            {
                return creator.Groups["vendor"].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the reply unchanged, or <see cref="CanonicalIdentityLine"/> when
    /// the reply asserts a vendor/model identity about itself.
    /// </summary>
    public static string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return MatchVendorSelfAssertion(text) is null ? text : CanonicalIdentityLine;
    }
}

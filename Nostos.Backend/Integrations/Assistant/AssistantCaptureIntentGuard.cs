using System.Text.RegularExpressions;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Conservative server-owned veto for turns that must never become durable
/// notes. This is intentionally narrower than the model's positive capture
/// classifier: it protects explicit negative intent and obvious source-scope
/// continuations without disabling frictionless capture of genuine thoughts.
/// </summary>
internal static partial class AssistantCaptureIntentGuard
{
    private static readonly HashSet<string> ScopePrefixes =
        new(StringComparer.OrdinalIgnoreCase) { "i", "ur", "från", "in", "from" };

    private static readonly HashSet<string> FirstPersonContinuationWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "am", "feel", "have", "mean", "think", "want", "believe", "guess",
            "my", "our", "jag", "mig", "min", "mitt", "mina",
        };

    [GeneratedRegex(
        @"^(?:(?:please\s+)?(?:do\s+not|don't)\s+(?:save|record|capture|note)\b|save\s+nothing\b|(?:spara|anteckna|lagra)\s+(?:inte|inget|ingenting)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitNoCaptureRegex();

    [GeneratedRegex(
        @"[\p{L}\p{N}][\p{L}\p{N}'’\-]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    public static bool SuppressesCapture(
        string? message,
        IReadOnlyList<AssistantHistoryMessageDto>? history)
    {
        var current = message?.Trim();
        if (string.IsNullOrWhiteSpace(current))
            return false;

        if (ExplicitNoCaptureRegex().IsMatch(current))
            return true;

        var previousQuestion = history?
            .LastOrDefault(entry =>
                string.Equals(entry.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Text));

        return previousQuestion is not null
            && previousQuestion.Text.TrimEnd().EndsWith('?', StringComparison.Ordinal)
            && IsShortScopeClarification(current);
    }

    private static bool IsShortScopeClarification(string message)
    {
        if (message.Length > 100 || message.Contains('\n'))
            return false;

        var words = WordRegex()
            .Matches(message)
            .Select(match => match.Value)
            .ToList();

        if (words.Count is < 2 or > 10 || !ScopePrefixes.Contains(words[0]))
            return false;

        // Swedish "I <book>" and English "In/From <book>" are useful narrow
        // forms. Avoid treating ordinary English first-person continuations
        // ("I mean...", "I think...") as source scope.
        if (string.Equals(words[0], "I", StringComparison.Ordinal)
            && words.Count > 1
            && FirstPersonContinuationWords.Contains(words[1]))
        {
            return false;
        }

        return !words.Skip(1).Any(FirstPersonContinuationWords.Contains);
    }
}

using System.Text.RegularExpressions;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

internal sealed record AssistantCaptureIntentDecision(
    bool SuppressCapture,
    AssistantResolvedBookDto? ResolvedBook = null,
    string? RejectionMessage = null);

/// <summary>
/// Conservative server-owned veto for turns that must never become durable
/// notes. Explicit negative intent is enough on its own. A reader selection
/// in context is read-only unless the user's message actually asks to save,
/// capture or note something. A short source-scope fragment is treated as
/// conversational control only when the remainder actually resolves to local
/// library state, which keeps ordinary first-person statements such as "I love
/// this book" capturable.
/// </summary>
internal sealed partial class AssistantCaptureIntentGuard(ILibraryService library)
{
    internal const string SelectionReadOnlyRejectionMessage =
        "A reader selection is read-only unless the user asks to save, capture or note it. Nothing was saved. Answer the user's request directly without calling notes_capture.";

    [GeneratedRegex(
        @"(?:^|[.!?;,]\s*)(?:(?:(?:actually|no|nope|wait|please|nej|vänta)\s*[,.:;-]?\s*)*)(?:(?:do\s+not|don['’]?t|never)\s+(?:save|keep|record|capture|note)\b|save\s+nothing\b|(?:spara|anteckna|lagra)\s+(?:inte|inget|ingenting)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitNoCaptureRegex();

    [GeneratedRegex(
        @"^(?:stop|stopp|cancel|avbryt|never\s+mind|nevermind|glöm\s+det|forget\s+(?:it|that)|discard\s+(?:it|that))\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExactControlRegex();

    [GeneratedRegex(
        @"^(?:i|ur|från|in|from)\s+(.+?)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScopeClarificationRegex();

    [GeneratedRegex(
        @"(?<!\b(?:did|have|has|had|do)\s+(?:i|we|you)\s+(?:already\s+)?)\b(?:save|capture|dictate|jot(?:\s+down)?|spara|anteckna|notera|diktera)\b"
        + @"|\b(?:write\s+(?:this|that|it)?\s*down|add\s+(?:a\s+|this\s+|an\s+)?note|take\s+(?:a\s+|this\s+)?note|make\s+(?:a\s+|this\s+)?note|write\s+(?:a\s+|this\s+)?note|create\s+(?:a\s+|this\s+)?note|leave\s+(?:a\s+|this\s+)?note|post\s+(?:a\s+|this\s+)?note|(?:lägg\s+till|skriv|gör|ta)\s+en\s+anteckning)\b"
        + @"|\bnote\s+(?:this|that|it|down|the\s+(?:quote|passage|selection|highlight|line|excerpt|thought))\b|\b(?:my\s+)?note\s*:"
        + @"|\b(?:keep|remember|record|highlight|bookmark|mark|log)\s+(?:this|that|it|the\s+(?:quote|passage|selection|highlight|line|excerpt|thought|words?))\b"
        + @"|\b(?:behåll|kom\s+ihåg|bokmärk)\s+(?:detta|det\s+här|citatet|passagen|tanken|det)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitCaptureIntentRegex();

    public static bool HasExplicitCaptureIntent(string? message)
    {
        var current = message?.Trim();
        if (string.IsNullOrWhiteSpace(current))
            return false;

        if (ExplicitNoCaptureRegex().IsMatch(current)
            || ExactControlRegex().IsMatch(current))
        {
            return false;
        }

        return ExplicitCaptureIntentRegex().IsMatch(current);
    }

    public Task<AssistantCaptureIntentDecision> EvaluateAsync(
        string? message,
        CancellationToken ct) =>
        EvaluateAsync(message, context: null, ct);

    public async Task<AssistantCaptureIntentDecision> EvaluateAsync(
        string? message,
        AssistantContextDto? context,
        CancellationToken ct)
    {
        var current = message?.Trim();
        if (string.IsNullOrWhiteSpace(current))
            return new AssistantCaptureIntentDecision(false);

        if (ExplicitNoCaptureRegex().IsMatch(current)
            || ExactControlRegex().IsMatch(current))
        {
            return new AssistantCaptureIntentDecision(true);
        }

        if (!string.IsNullOrWhiteSpace(context?.SelectedText)
            && !HasExplicitCaptureIntent(current))
        {
            return new AssistantCaptureIntentDecision(
                SuppressCapture: true,
                RejectionMessage: SelectionReadOnlyRejectionMessage);
        }

        var match = ScopeClarificationRegex().Match(current);
        if (!match.Success)
            return new AssistantCaptureIntentDecision(false);

        var title = match.Groups[1].Value.Trim();
        if (title.Length is < 2 or > 100)
            return new AssistantCaptureIntentDecision(false);

        var resolved = await library.ResolveBookAsync(
            new LibraryResolveBookRequest(
                Title: title,
                IncludeExternalMetadata: false),
            ct);

        if (resolved.Resolution == LibraryResolution.ExactMatch
            && resolved.MatchedBook is { } exact)
        {
            return new AssistantCaptureIntentDecision(
                true,
                new AssistantResolvedBookDto(exact.Id, exact.Title));
        }

        if (resolved.Resolution == LibraryResolution.Candidates
            && resolved.Candidates is { Count: 1 } one)
        {
            return new AssistantCaptureIntentDecision(
                true,
                new AssistantResolvedBookDto(one[0].BookId, one[0].Title));
        }

        // More than one local match is still clearly a book-scope refinement,
        // but it is not safe to pick one. Suppress capture and leave resolution
        // to the normal read/clarification path.
        if (resolved.Resolution == LibraryResolution.Candidates
            && resolved.Candidates is { Count: > 1 })
        {
            return new AssistantCaptureIntentDecision(true);
        }

        return new AssistantCaptureIntentDecision(false);
    }
}

using System.Text.RegularExpressions;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

internal sealed record AssistantCaptureIntentDecision(
    bool SuppressCapture,
    AssistantResolvedBookDto? ResolvedBook = null);

/// <summary>
/// Conservative server-owned veto for turns that must never become durable
/// notes. Explicit negative intent is enough on its own. A short source-scope
/// fragment is treated as conversational control only when the remainder
/// actually resolves to local library state, which keeps ordinary first-person
/// statements such as "I love this book" capturable.
/// </summary>
internal sealed partial class AssistantCaptureIntentGuard(ILibraryService library)
{
    [GeneratedRegex(
        @"^(?:(?:please\s+)?(?:do\s+not|don't)\s+(?:save|record|capture|note)\b|save\s+nothing\b|(?:spara|anteckna|lagra)\s+(?:inte|inget|ingenting)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitNoCaptureRegex();

    [GeneratedRegex(
        @"^(?:stop|stopp|cancel|avbryt|never\s+mind|nevermind|glöm\s+det)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExactControlRegex();

    [GeneratedRegex(
        @"^(?:i|ur|från|in|from)\s+(.+?)\s*[.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScopeClarificationRegex();

    public async Task<AssistantCaptureIntentDecision> EvaluateAsync(
        string? message,
        IReadOnlyList<AssistantHistoryMessageDto>? history,
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

        var latestUser = history?
            .LastOrDefault(entry =>
                string.Equals(entry.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Text));

        if (latestUser is null
            || !latestUser.Text.TrimEnd().EndsWith("?", StringComparison.Ordinal))
        {
            return new AssistantCaptureIntentDecision(false);
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

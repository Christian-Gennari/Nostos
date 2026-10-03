namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Response-boundary truth guard for the narrow failure mode where a model
/// narrates a completed Nostos action or search result that the current turn
/// never successfully executed.
/// </summary>
internal static class AssistantCompletionClaimGuard
{
    private const string CaptureCapability = "notes_capture";
    private const string KnowledgeSearchCapability = "knowledge_search";
    private const string BookTextSearchCapability = "book_text_search";

    internal const string UnverifiedCaptureReply =
        "I could not verify that this was saved, so I will not claim that it was. Please try again.";

    internal const string UnverifiedSearchReply =
        "I could not verify that search result because no search completed in this turn. Please try again.";

    internal static AssistantCompletionClaimViolation? Evaluate(
        string? reply,
        IReadOnlySet<string> successfulCapabilities)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;

        var text = reply.Trim();

        if (!successfulCapabilities.Contains(CaptureCapability)
            && ClaimsCompletedCapture(text))
        {
            return new AssistantCompletionClaimViolation(
                "capture",
                UnverifiedCaptureReply);
        }

        if (!successfulCapabilities.Contains(KnowledgeSearchCapability)
            && !successfulCapabilities.Contains(BookTextSearchCapability)
            && ClaimsEmptySearch(text))
        {
            return new AssistantCompletionClaimViolation(
                "search",
                UnverifiedSearchReply);
        }

        return null;
    }

    private static bool ClaimsCompletedCapture(string reply)
    {
        var normalized = reply.Trim().ToLowerInvariant();

        if (normalized.Contains("nothing was saved", StringComparison.Ordinal)
            || normalized.Contains("was not saved", StringComparison.Ordinal)
            || normalized.Contains("wasn't saved", StringComparison.Ordinal)
            || normalized.Contains("not saved", StringComparison.Ordinal))
        {
            return false;
        }

        return StartsWithClaim(normalized, "saved")
            || StartsWithClaim(normalized, "noted")
            || StartsWithClaim(normalized, "captured")
            || StartsWithClaim(normalized, "stored")
            || StartsWithClaim(normalized, "i saved")
            || StartsWithClaim(normalized, "i've saved")
            || StartsWithClaim(normalized, "i have saved")
            || StartsWithClaim(normalized, "i noted")
            || StartsWithClaim(normalized, "i've noted")
            || StartsWithClaim(normalized, "i have noted");
    }

    private static bool ClaimsEmptySearch(string reply)
    {
        var normalized = reply.Trim().ToLowerInvariant();
        var claimsSearch = normalized.StartsWith("the search ", StringComparison.Ordinal)
            || normalized.StartsWith("search ", StringComparison.Ordinal)
            || normalized.StartsWith("i searched ", StringComparison.Ordinal);

        if (!claimsSearch)
            return false;

        return normalized.Contains("no evidence", StringComparison.Ordinal)
            || normalized.Contains("no results", StringComparison.Ordinal)
            || normalized.Contains("nothing", StringComparison.Ordinal)
            || normalized.Contains("didn't find", StringComparison.Ordinal)
            || normalized.Contains("did not find", StringComparison.Ordinal);
    }

    private static bool StartsWithClaim(string text, string claim) =>
        text.Equals(claim, StringComparison.Ordinal)
        || text.StartsWith(claim + ".", StringComparison.Ordinal)
        || text.StartsWith(claim + "!", StringComparison.Ordinal)
        || text.StartsWith(claim + ",", StringComparison.Ordinal)
        || text.StartsWith(claim + " ", StringComparison.Ordinal);
}

internal sealed record AssistantCompletionClaimViolation(
    string Kind,
    string ReplacementReply);

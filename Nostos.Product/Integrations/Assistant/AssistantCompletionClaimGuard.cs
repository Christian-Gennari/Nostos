using System.Text.RegularExpressions;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Response-boundary truth guard for the failure modes where a model narrates
/// a completed Nostos action, search result, or staged review that does not
/// match what the current turn actually produced.
/// </summary>
internal static partial class AssistantCompletionClaimGuard
{
    private const string CaptureCapability = "notes_capture";
    private const string KnowledgeSearchCapability = "knowledge_search";
    private const string BookTextSearchCapability = "book_text_search";

    internal const string UnverifiedCaptureReply =
        "I could not verify that this was saved, so I will not claim that it was. Please try again.";

    internal const string UnverifiedSearchReply =
        "I could not verify that search result because no search completed in this turn. Please try again.";

    internal const string NoChangeLibraryMatchesReply =
        "Nothing was changed because the library already matches that.";

    internal const string UnverifiedReviewReply =
        "Nothing was changed because no changes were prepared. Please try again.";

    internal const string AppliedChangesFallbackReply =
        "The changes have been applied.";

    internal static AssistantCompletionClaimViolation? Evaluate(
        string? reply,
        IReadOnlySet<string> successfulCapabilities) =>
        Evaluate(reply, successfulCapabilities, AssistantChangesetOutcome.None, draftHadEmptyDiff: false);

    internal static AssistantCompletionClaimViolation? Evaluate(
        string? reply,
        IReadOnlySet<string> successfulCapabilities,
        AssistantChangesetOutcome changeOutcome,
        bool draftHadEmptyDiff)
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

        if (!changeOutcome.AwaitingReview)
        {
            if (changeOutcome.Changeset is null
                && ClaimsStagedOrPendingReview(text, allowBareStaged: true))
            {
                return draftHadEmptyDiff
                    ? new AssistantCompletionClaimViolation(
                        "review_no_change",
                        NoChangeLibraryMatchesReply,
                        IsError: false)
                    : new AssistantCompletionClaimViolation(
                        "review",
                        UnverifiedReviewReply,
                        IsError: true);
            }

            if (changeOutcome.Applied
                && ClaimsStagedOrPendingReview(text, allowBareStaged: false))
            {
                return new AssistantCompletionClaimViolation(
                    "review_already_applied",
                    AppliedChangesFallbackReply,
                    IsError: false);
            }
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

    private static bool ClaimsStagedOrPendingReview(string reply, bool allowBareStaged)
    {
        var normalized = reply
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"');

        foreach (var rawClause in SentenceSplitRegex().Split(normalized))
        {
            var clause = rawClause.Trim();
            if (clause.Length == 0)
                continue;

            if (NegatedStagedOrReviewRegex().IsMatch(clause))
                continue;

            if (CapabilityDisclosureReviewRegex().IsMatch(clause))
                continue;

            if (ExplicitPendingReviewRegex().IsMatch(clause))
                return true;

            if (allowBareStaged && BareStagedClaimRegex().IsMatch(clause))
                return true;
        }

        return false;
    }

    private static bool StartsWithClaim(string text, string claim) =>
        text.Equals(claim, StringComparison.Ordinal)
        || text.StartsWith(claim + ".", StringComparison.Ordinal)
        || text.StartsWith(claim + "!", StringComparison.Ordinal)
        || text.StartsWith(claim + ",", StringComparison.Ordinal)
        || text.StartsWith(claim + " ", StringComparison.Ordinal);

    [GeneratedRegex(@"[\r\n.!?;]+")]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(
        @"\b(?:no\s+(?:library\s+)?(?:changes?|edits?|updates?|modifications?|steps?|items?|review(?:\s+card)?)|nothing|none)\s+(?:is|are|was|were|has\s+been|have\s+been)\s+(?:staged|waiting|awaiting|pending|held|ready|prepared)\b"
        + @"|\b(?:not|never|n't)\s+(?:been\s+)?(?:staged|stage|waiting\s+for\s+(?:your\s+)?review|awaiting\s+(?:your\s+)?review|held\s+for\s+(?:your\s+)?review|pending\s+(?:your\s+)?review|ready\s+(?:for\s+(?:your\s+)?review|for\s+you\s+to\s+review|to\s+review)|prepared\s+(?:a\s+)?review)\b"
        + @"|\b(?:did\s+not|didn't|have\s+not|haven't|has\s+not|hasn't)\s+(?:stage|staged)\b"
        + @"|\b(?:without\s+(?:staging|a\s+review)|there\s+(?:is|are)\s+no\s+(?:changes?\s+(?:staged|waiting|pending|to\s+review)|pending\s+review|review(?:\s+card)?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedStagedOrReviewRegex();

    [GeneratedRegex(
        @"\bstaged\b[^.!?;]*\bor\s+(?:applied\s+with\s+undo|can\s+be\s+undone)\b"
        + @"|\b(?:can|will|may|would)\s+(?:also\s+)?be\s+staged\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityDisclosureReviewRegex();

    [GeneratedRegex(
        @"\bstaged\b(?:\s+\w+){0,4}\s+for\s+(?:your\s+)?review\b"
        + @"|\bstaged\b(?:\s+\w+){0,4}\s+for\s+you\s+to\s+review\b"
        + @"|\bstaged\b(?:\s+\w+){0,3}\s+(?:and\s+)?(?:waiting|awaiting|ready|pending)\s+(?:for\s+)?(?:your\s+|you\s+to\s+)?review\b"
        + @"|\b(?:is|are|now)\s+(?:waiting|awaiting|held)\s+(?:for\s+)?(?:your\s+)?review\b"
        + @"|\b(?:is|are)\s+(?:now\s+)?pending\s+(?:your\s+)?review\b"
        + @"|\b(?:is|are)\s+(?:now\s+)?ready\s+(?:for\s+(?:your\s+)?review|for\s+you\s+to\s+review|to\s+review)\b"
        + @"|\b(?:in|on|from|via|using|use|see|check)\s+the\s+review\s+card\b"
        + @"|\breview\s+and\s+apply\s+(?:the|these|those|them)\b"
        + @"|\bapply\s+(?:the|these)\s+staged\s+changes?\b"
        + @"|\b(?:i(?:'ve|\s+have)?\s+prepared|prepared)\s+a\s+review(?:\s+card)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPendingReviewRegex();

    [GeneratedRegex(
        @"^staged\b"
        + @"|\bi(?:'ve|\s+have)?\s+staged\b"
        + @"|\b(?:the|these|your|those|all)\s+(?:library\s+)?(?:changes?|updates?|edits?|steps?)\s+(?:are|were|have\s+been)\s+staged\b"
        + @"|\b(?:changes?|updates?|steps?)\s+(?:have|has)\s+been\s+staged\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareStagedClaimRegex();
}

internal sealed record AssistantCompletionClaimViolation(
    string Kind,
    string ReplacementReply,
    bool IsError = true);

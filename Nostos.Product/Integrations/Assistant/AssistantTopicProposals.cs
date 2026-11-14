using System.Text.Json;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// <c>topics_propose_links</c>: the one capability that produces clickable
/// suggestions, and the server-owned evidence rule that gates it (issue #1598).
///
/// Both halves used to be string-keyed <see cref="JsonElement"/> probes split
/// between the orchestration loop — <c>"candidates"</c>, <c>"noteId"</c>,
/// <c>"topicId"</c>, <c>"name"</c>, <c>"reason"</c>, <c>"id"</c> — with the
/// same property names read twice. Reads are typed here once.
///
/// Only an explicit, validated proposal capability can produce suggestions.
/// Ordinary topic reads are evidence, never implicit clickable actions.
/// </summary>
internal static class AssistantTopicProposals
{
    public const string Capability = "topics_propose_links";

    /// <summary>
    /// The capability's answer: the note it proposed for, and the proposals it
    /// accepted after checking them against the library.
    /// </summary>
    public sealed record Result(string? NoteId, IReadOnlyList<Candidate>? Candidates);

    /// <summary>One accepted proposal. <see cref="Id"/> is the topic's id, as a string.</summary>
    public sealed record Candidate(string? Id, string? Name, string? Reason);

    /// <summary>The model's call: the note it read, and the topics it proposes.</summary>
    public sealed record Arguments(Guid? NoteId, IReadOnlyList<ArgumentCandidate>? Candidates);

    /// <summary>One proposed topic in the model's arguments.</summary>
    public sealed record ArgumentCandidate(Guid? TopicId);

    public static Result? ReadResult(JsonElement? data) =>
        AssistantCapabilityPayloads.Read<Result>(data);

    public static Arguments? ReadArguments(JsonElement args) =>
        AssistantCapabilityPayloads.Read<Arguments>(args);

    /// <summary>
    /// True when a proposal was made from material this turn actually read:
    /// the target note was reviewed in this turn and every proposed topic was
    /// inspected through a topic read in this turn. Without that, the model is
    /// proposing ids it never looked at.
    /// </summary>
    public static bool UsesReadEvidence(
        JsonElement args,
        string? currentReviewNoteId,
        HashSet<Guid> reviewedNoteIds,
        HashSet<Guid> inspectedTopicIds,
        bool topicsInspected)
    {
        if (ReadArguments(args) is not { } proposal
            || proposal.NoteId is not { } noteId
            || !reviewedNoteIds.Contains(noteId)
            || (Guid.TryParse(currentReviewNoteId, out var currentNoteId) && currentNoteId != noteId)
            || !topicsInspected
            || proposal.Candidates is not { } candidates)
        {
            return false;
        }

        return candidates.All(candidate =>
            candidate.TopicId is { } topicId && inspectedTopicIds.Contains(topicId));
    }

    /// <summary>The clickable suggestions a proposal result carries, or none.</summary>
    public static IEnumerable<AssistantSuggestionDto> ExtractSuggestions(JsonElement? data)
    {
        if (ReadResult(data) is not { Candidates: { } candidates } proposal)
        {
            yield break;
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(candidate.Reason)) continue;

            yield return new AssistantSuggestionDto(
                "topic",
                candidate.Name,
                candidate.Reason,
                candidate.Id,
                proposal.NoteId);
        }
    }

    /// <summary>
    /// Adds incoming suggestions to the response, de-duplicated by identity and
    /// capped for topics. The cap is what makes the review a small set rather
    /// than a dump of the library; de-duplication handles repeated explicit
    /// proposals in one turn. Nothing here creates or mutates anything.
    /// </summary>
    public static void Merge(
        List<AssistantSuggestionDto> target,
        IEnumerable<AssistantSuggestionDto> incoming,
        int maxTopicSuggestions)
    {
        foreach (var suggestion in incoming)
        {
            var key = suggestion.Value ?? suggestion.Label;
            if (target.Any(existing =>
                    string.Equals(existing.Kind, suggestion.Kind, StringComparison.Ordinal)
                    && string.Equals(existing.Value ?? existing.Label, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (string.Equals(suggestion.Kind, "topic", StringComparison.Ordinal)
                && target.Count(existing => string.Equals(existing.Kind, "topic", StringComparison.Ordinal))
                    >= maxTopicSuggestions)
            {
                continue;
            }

            target.Add(suggestion);
        }
    }
}

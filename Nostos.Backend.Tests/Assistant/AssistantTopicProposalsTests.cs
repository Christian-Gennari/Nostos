using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// <c>topics_propose_links</c> is the one capability that produces clickable
/// suggestions, and the server-owned rule gates it on what this turn actually
/// read (issue #1598). Both used to be string-keyed probes split across the
/// loop; these tests pin the behaviour they replaced.
/// </summary>
public sealed class AssistantTopicProposalsTests
{
    private static readonly Guid NoteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TopicId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherTopicId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void A_proposal_backed_by_this_turns_reads_is_allowed()
    {
        var allowed = AssistantTopicProposals.UsesReadEvidence(
            Arguments(NoteId, TopicId),
            currentReviewNoteId: null,
            reviewedNoteIds: [NoteId],
            inspectedTopicIds: [TopicId],
            topicsInspected: true);

        allowed.Should().BeTrue();
    }

    [Fact]
    public void A_note_this_turn_did_not_review_blocks_the_proposal()
    {
        AssistantTopicProposals.UsesReadEvidence(
            Arguments(NoteId, TopicId),
            currentReviewNoteId: null,
            reviewedNoteIds: [],
            inspectedTopicIds: [TopicId],
            topicsInspected: true).Should().BeFalse();
    }

    [Fact]
    public void A_topic_this_turn_did_not_inspect_blocks_the_proposal()
    {
        AssistantTopicProposals.UsesReadEvidence(
            Arguments(NoteId, TopicId),
            currentReviewNoteId: null,
            reviewedNoteIds: [NoteId],
            inspectedTopicIds: [OtherTopicId],
            topicsInspected: true).Should().BeFalse();
    }

    [Fact]
    public void Proposing_from_a_different_review_note_blocks_the_proposal()
    {
        AssistantTopicProposals.UsesReadEvidence(
            Arguments(NoteId, TopicId),
            currentReviewNoteId: OtherTopicId.ToString(),
            reviewedNoteIds: [NoteId],
            inspectedTopicIds: [TopicId],
            topicsInspected: true).Should().BeFalse();
    }

    [Fact]
    public void Without_a_topic_read_or_candidates_the_proposal_is_blocked()
    {
        AssistantTopicProposals.UsesReadEvidence(
            Arguments(NoteId, TopicId),
            currentReviewNoteId: null,
            reviewedNoteIds: [NoteId],
            inspectedTopicIds: [TopicId],
            topicsInspected: false).Should().BeFalse();

        AssistantTopicProposals.UsesReadEvidence(
            Payload("""{"noteId":"11111111-1111-1111-1111-111111111111"}"""),
            currentReviewNoteId: null,
            reviewedNoteIds: [NoteId],
            inspectedTopicIds: [TopicId],
            topicsInspected: true).Should().BeFalse();
    }

    [Fact]
    public void An_empty_proposal_still_needs_the_note_read_and_a_topic_read()
    {
        var empty = Arguments(NoteId);

        AssistantTopicProposals.UsesReadEvidence(
            empty,
            currentReviewNoteId: null,
            reviewedNoteIds: [NoteId],
            inspectedTopicIds: [],
            topicsInspected: true).Should().BeTrue();

        AssistantTopicProposals.UsesReadEvidence(
            empty,
            currentReviewNoteId: null,
            reviewedNoteIds: [NoteId],
            inspectedTopicIds: [],
            topicsInspected: false).Should().BeFalse();
    }

    [Fact]
    public void Accepted_candidates_become_topic_suggestions_for_the_note()
    {
        var result = Payload(
            """
            {"noteId":"11111111-1111-1111-1111-111111111111","candidates":[{"id":"22222222-2222-2222-2222-222222222222","name":"Homecoming","reason":"Same idea."}]}
            """);

        AssistantTopicProposals.ExtractSuggestions(result)
            .Should().ContainSingle()
            .Which.Should().Be(new AssistantSuggestionDto(
                "topic",
                "Homecoming",
                "Same idea.",
                "22222222-2222-2222-2222-222222222222",
                "11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void A_candidate_without_a_name_or_reason_is_not_a_suggestion()
    {
        var result = Payload(
            """
            {"noteId":"11111111-1111-1111-1111-111111111111","candidates":[{"id":"1","name":"","reason":"x"},{"id":"2","name":"Ok","reason":""},{"id":"3","name":"Fine","reason":"Because."}]}
            """);

        AssistantTopicProposals.ExtractSuggestions(result)
            .Should().ContainSingle()
            .Which.Label.Should().Be("Fine");
    }

    [Fact]
    public void Suggestions_are_deduplicated_by_identity_and_capped_for_topics()
    {
        var target = new List<AssistantSuggestionDto>();

        AssistantTopicProposals.Merge(
            target,
            [
                new AssistantSuggestionDto("topic", "Homecoming", "Same idea.", "2"),
                new AssistantSuggestionDto("topic", "homecoming", "Same idea again.", "2"),
                new AssistantSuggestionDto("topic", "Memory", "Same idea.", "3"),
                new AssistantSuggestionDto("topic", "Loss", "Same idea.", "4"),
                new AssistantSuggestionDto("topic", "Exile", "Same idea.", "5"),
            ],
            maxTopicSuggestions: 3);

        target.Should().HaveCount(3);
        target.Select(suggestion => suggestion.Label)
            .Should().Equal("Homecoming", "Memory", "Loss");
    }

    private static JsonElement Arguments(Guid noteId, params Guid[] topicIds)
    {
        var candidates = string.Join(
            ",",
            topicIds.Select(id => $"{{\"topicId\":\"{id}\",\"reason\":\"Because.\"}}"));

        return Payload($"{{\"noteId\":\"{noteId}\",\"candidates\":[{candidates}]}}");
    }

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

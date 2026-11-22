using FluentAssertions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The change policy's topic-kind rules (issue #1770): a classification larger
/// than Assistant:AutoApplyTopicKinds waits for review, and creating a kind
/// always waits, however small the changeset.
/// </summary>
public sealed class AssistantChangePolicyTopicKindTests
{
    private static readonly AssistantOptions Options = new();

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(150, true)]
    public void Setting_kinds_reviews_only_above_the_configured_threshold(int topics, bool review)
    {
        var decision = AssistantChangePolicy.Evaluate(Diff(topics), Options, false, false);

        decision.RequiresReview.Should().Be(review);
        if (review)
            decision.Reasons.Should().Contain(reason => reason.Contains($"{topics} topics"));
    }

    [Fact]
    public void The_threshold_is_the_configured_setting_not_a_constant()
    {
        Options.AutoApplyTopicKinds.Should().Be(10);

        var higher = new AssistantOptions { AutoApplyTopicKinds = 1000 };
        AssistantChangePolicy.Evaluate(Diff(150), higher, false, false)
            .RequiresReview.Should().BeFalse();
    }

    [Fact]
    public void Creating_a_kind_always_waits_for_review()
    {
        var diff = Diff(1, createdKinds: [new LibraryTopicKindChangeDto(
            Guid.NewGuid(), "created", "School of thought", null, "kind-7", 1)]);

        var decision = AssistantChangePolicy.Evaluate(diff, Options, false, false);

        decision.RequiresReview.Should().BeTrue();
        decision.Reasons.Should().Contain(reason => reason.Contains("School of thought"));
    }

    [Fact]
    public void A_kind_only_change_on_a_few_topics_applies_at_once()
    {
        AssistantChangePolicy.Evaluate(Diff(3), Options, false, false)
            .RequiresReview.Should().BeFalse();
    }

    private static LibraryChangesetDiffDto Diff(
        int topics,
        IReadOnlyList<LibraryTopicKindChangeDto>? createdKinds = null)
    {
        createdKinds ??= [];
        var assignments = Enumerable.Range(0, topics)
            .Select(index => new LibraryTopicKindAssignmentDto(
                Guid.NewGuid(),
                $"Topic {index}",
                Guid.NewGuid(),
                "Idea",
                null,
                "assistant"))
            .ToList();
        return new LibraryChangesetDiffDto(
            [], [], [],
            new LibraryChangesetCountsDto(
                0, 0, 0, 0, 0, 0, 0, 0,
                TopicKindsCreated: createdKinds.Count,
                TopicKindChanges: assignments.Count),
            createdKinds,
            assignments);
    }
}

using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

public sealed class AssistantTurnLifecycleTests
{
    [Fact]
    public void Stale_cancellation_cannot_affect_a_newer_turn()
    {
        var registry = new AssistantTurnExecutionRegistry();

        using (var first = registry.TryBegin("conversation", "turn-1", CancellationToken.None))
        {
            first.Should().NotBeNull();
        }

        using var second = registry.TryBegin("conversation", "turn-2", CancellationToken.None);
        second.Should().NotBeNull();

        registry.TryCancel("conversation", "turn-1").Should().BeFalse();
        second!.Token.IsCancellationRequested.Should().BeFalse();

        registry.TryCancel("conversation", "turn-2").Should().BeTrue();
        second.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_active_turn_identity_is_not_started_twice()
    {
        var registry = new AssistantTurnExecutionRegistry();

        using var first = registry.TryBegin("conversation", "turn-1", CancellationToken.None);
        using var duplicate = registry.TryBegin("conversation", "turn-1", CancellationToken.None);

        first.Should().NotBeNull();
        duplicate.Should().BeNull();
    }

    [Theory]
    [InlineData("knowledge_search", AssistantTrustClass.Suggest, AssistantCapabilityCategory.KnowledgeRetrieval, "searching_material", "Searching your notes and books…")]
    [InlineData("knowledge_read_evidence", AssistantTrustClass.Suggest, AssistantCapabilityCategory.SourceNavigation, "opening_evidence", "Opening the matching passage…")]
    [InlineData("notes_capture", AssistantTrustClass.Capture, AssistantCapabilityCategory.Capture, "saving_note", "Saving your note…")]
    [InlineData("library_create_collection", AssistantTrustClass.Act, AssistantCapabilityCategory.Organization, "updating_collection", "Updating the collection…")]
    public void Activity_is_fixed_product_language_not_tool_payload(
        string name,
        AssistantTrustClass trust,
        AssistantCapabilityCategory category,
        string expectedCode,
        string expectedMessage)
    {
        var capability = new AssistantCapability(
            name,
            trust,
            category,
            "contains-private-summary-marker",
            """{"type":"object","properties":{"secret":{"type":"string"}}}""",
            (_, _, _) => Task.FromResult(AssistantToolResult.Ok()));

        var activity = AssistantTurnActivities.ForCapability(capability);

        activity.Should().Be(new AssistantTurnActivityDto(expectedCode, expectedMessage));
        activity!.Message.Should().NotContain("private");
        activity.Message.Should().NotContain("secret");
        activity.Message.Should().NotContain("reason");
        activity.Message.Should().NotContain("argument");
    }
}

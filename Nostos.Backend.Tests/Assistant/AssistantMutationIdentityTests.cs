using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The canonical mutation identity is the logical operation, independent of how
/// the model formats or orders its JSON arguments, and fixed-length so it can
/// never exceed the canonical services' 128-character receipt-key cap.
/// </summary>
public sealed class AssistantMutationIdentityTests
{
    [Fact]
    public void Equivalent_argument_values_with_different_property_order_and_whitespace_share_one_identity()
    {
        using var first = JsonDocument.Parse("""{"bookId":"b1","content":"A thought"}""");
        using var second = JsonDocument.Parse("{  \"content\" : \"A thought\",\n  \"bookId\" : \"b1\" }");

        AssistantMutationIdentity.Key("turn-1", "notes_capture", first.RootElement)
            .Should().Be(AssistantMutationIdentity.Key("turn-1", "notes_capture", second.RootElement));
    }

    [Fact]
    public void Changing_the_turn_capability_or_an_argument_value_changes_the_identity()
    {
        using var baseline = JsonDocument.Parse("""{"collectionId":"collection-1"}""");
        using var otherCollection = JsonDocument.Parse("""{"collectionId":"collection-2"}""");

        var key = AssistantMutationIdentity.Key(
            "turn-1",
            "library_delete_empty_collection",
            baseline.RootElement);

        key.Should().NotBe(AssistantMutationIdentity.Key(
            "turn-2",
            "library_delete_empty_collection",
            baseline.RootElement));
        key.Should().NotBe(AssistantMutationIdentity.Key(
            "turn-1",
            "library_rename_collection",
            baseline.RootElement));
        key.Should().NotBe(AssistantMutationIdentity.Key(
            "turn-1",
            "library_delete_empty_collection",
            otherCollection.RootElement));
    }

    [Fact]
    public void The_identity_is_a_fixed_length_lowercase_hex_digest_within_the_receipt_key_cap()
    {
        var emptyArguments = JsonSerializer.SerializeToElement(new { });
        var longestTurnId = new string('t', 128);

        var keys = new[]
        {
            AssistantMutationIdentity.Key("turn-1", "notes_capture", emptyArguments),
            AssistantMutationIdentity.Key("turn-1", "library_delete_empty_collection", emptyArguments),
            AssistantMutationIdentity.Key(longestTurnId, "notes_capture", emptyArguments),
        };

        foreach (var key in keys)
        {
            key.Should().HaveLength(64);
            key.Should().MatchRegex("^[0-9a-f]{64}$");
            key.Length.Should().BeLessThanOrEqualTo(128);
        }
    }
}

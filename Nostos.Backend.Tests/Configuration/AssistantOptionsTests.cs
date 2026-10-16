using FluentAssertions;
using Nostos.Backend.Configuration;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

/// <summary>
/// The assistant's model is a load-bearing choice: its whole job — capture a
/// thought, read the library, propose topics — runs through tool calls, and a
/// model that ignores the tool definitions answers as though it had done the
/// work. A pooled free model did exactly that. Model names also differ per
/// provider, so no built-in name suits every owner; the owner chooses one.
/// </summary>
public sealed class AssistantOptionsTests
{
    [Fact]
    public void There_is_no_built_in_model_the_owner_did_not_choose()
    {
        new AssistantOptions().Model.Should().BeEmpty();
        new SpeechOptions().Model.Should().BeEmpty();
        new EmbeddingOptions().Model.Should().BeEmpty();
    }
}

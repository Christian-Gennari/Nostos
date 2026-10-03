using FluentAssertions;
using Nostos.Backend.Configuration;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

/// <summary>
/// The assistant's default model is a load-bearing choice, not a preference: its
/// whole job — capture a thought, read the library, propose topics — runs
/// through tool calls, and a model that ignores the tool definitions answers as
/// though it had done the work. The 9Router free pool did exactly that, which is
/// why it is not the default any more. These tests exist so nobody quietly puts
/// it back.
/// </summary>
public sealed class AssistantOptionsTests
{
    [Fact]
    public void The_default_model_is_the_verified_tool_calling_model()
    {
        new AssistantOptions().Model.Should().Be("gemini/gemini-3.5-flash-lite");
    }

    [Fact]
    public void The_default_model_is_not_a_pooled_multi_vendor_id()
    {
        var model = new AssistantOptions().Model;

        model.Should().NotContain("pooled");
        model.Should().NotContain("free_frontier");
    }
}

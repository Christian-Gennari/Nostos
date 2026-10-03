using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

public sealed class AssistantToolLoopDetectorTests
{
    [Fact]
    public void Equivalent_json_arguments_are_an_immediate_repeat_even_when_property_order_differs()
    {
        var detector = new AssistantToolLoopDetector();

        detector.IsImmediateRepeat([
            new LlmToolCall("call-1", "library_update_book", """{"bookId":"1","title":"A"}"""),
        ]).Should().BeFalse();

        detector.IsImmediateRepeat([
            new LlmToolCall("call-2", "library_update_book", """{ "title": "A", "bookId": "1" }"""),
        ]).Should().BeTrue();
    }

    [Fact]
    public void A_changed_argument_breaks_the_repeat_sequence()
    {
        var detector = new AssistantToolLoopDetector();

        detector.IsImmediateRepeat([
            new LlmToolCall("call-1", "topics_search", """{"query":"one"}"""),
        ]).Should().BeFalse();

        detector.IsImmediateRepeat([
            new LlmToolCall("call-2", "topics_search", """{"query":"two"}"""),
        ]).Should().BeFalse();

        detector.IsImmediateRepeat([
            new LlmToolCall("call-3", "topics_search", """{"query":"two"}"""),
        ]).Should().BeTrue();
    }
}

using FluentAssertions;
using Nostos.Backend.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// A scriptable <see cref="IThoughtProcessor"/> so the service tests can assert
/// the mode that was requested without touching the real provider. It records
/// every call, so a test can prove a verbatim capture never processed at all.
/// </summary>
public sealed class FakeThoughtProcessor : IThoughtProcessor
{
    /// <summary>Every (text, mode) pair the service handed to the processor, in order.</summary>
    public List<(string Text, string Mode)> Calls { get; } = new();

    /// <summary>When set, shapes the outcome; otherwise the text passes through in the requested mode.</summary>
    public Func<string, string, ThoughtProcessingResult>? Responder { get; set; }

    /// <summary>The text of the last call, for a "started from RawContent" assertion.</summary>
    public string? LastText => Calls.Count == 0 ? null : Calls[^1].Text;

    public Task<ThoughtProcessingResult> ProcessAsync(
        string text,
        string mode,
        CancellationToken ct = default)
    {
        Calls.Add((text, mode));

        var result = Responder is not null
            ? Responder(text, mode)
            : new ThoughtProcessingResult(text, ThoughtProcessingModes.Normalize(mode), ProviderCalled: true);

        return Task.FromResult(result);
    }
}

/// <summary>
/// Unit coverage for <see cref="ThoughtProcessor"/> against the scriptable
/// <see cref="FakeLlmProvider"/>. A real provider is never called
/// here; nothing in this suite spends pool quota.
///
/// The "clarify invents nothing" property is a prompt-and-pipeline property:
/// these tests prove the raw text and the mode-specific instruction travel to the
/// provider (a), and that whatever comes back is stored verbatim with nothing
/// added (b). What the model does with the prompt is model-dependent and is
/// spot-checked once with a real call, not pinned here (c).
/// </summary>
public sealed class ThoughtProcessorTests
{
    [Fact]
    public async Task Verbatim_short_circuits_without_a_provider_call()
    {
        var llm = new FakeLlmProvider().Returns("SHOULD NOT BE USED");
        var processor = new ThoughtProcessor(llm);

        var result = await processor.ProcessAsync("so anyway I was thinking", "verbatim");

        result.Text.Should().Be("so anyway I was thinking");
        result.Mode.Should().Be(ThoughtProcessingModes.Verbatim);
        result.ProviderCalled.Should().BeFalse();
        llm.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task An_absent_or_unknown_mode_is_a_verbatim_no_op()
    {
        var llm = new FakeLlmProvider().Returns("SHOULD NOT BE USED");
        var processor = new ThoughtProcessor(llm);

        var absent = await processor.ProcessAsync("raw words", string.Empty);
        var unknown = await processor.ProcessAsync("raw words", "make_it_fancy");

        absent.Text.Should().Be("raw words");
        absent.ProviderCalled.Should().BeFalse();
        unknown.Text.Should().Be("raw words");
        unknown.ProviderCalled.Should().BeFalse();
        llm.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Light_polish_sends_the_raw_text_and_its_own_instruction()
    {
        var llm = new FakeLlmProvider().Returns("I was thinking about the snow.");
        var processor = new ThoughtProcessor(llm);

        var result = await processor.ProcessAsync("so anyway i was like thinking bout the snow", "light_polish");

        llm.CallCount.Should().Be(1);
        var request = llm.LastRequest;
        request.Messages.Should().HaveCount(2);
        request.Messages[0].Role.Should().Be("system");
        request.Messages[0].Content.Should().Be(ThoughtProcessor.LightPolishSystemPrompt);
        request.Messages[1].Role.Should().Be("user");
        request.Messages[1].Content.Should().Be("so anyway i was like thinking bout the snow");
        request.Tools.Should().BeEmpty();
        request.MaxTokens.Should().BeGreaterThanOrEqualTo(ThoughtProcessor.MaxTokensFloor);

        result.Text.Should().Be("I was thinking about the snow.");
        result.Mode.Should().Be(ThoughtProcessingModes.LightPolish);
        result.ProviderCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Clarify_sends_the_clarify_instruction_and_the_guiding_rule()
    {
        var llm = new FakeLlmProvider().Returns("A tightened thought.");
        var processor = new ThoughtProcessor(llm);

        var result = await processor.ProcessAsync("raw thought", "clarify");

        llm.LastRequest.Messages[0].Content.Should().Be(ThoughtProcessor.ClarifySystemPrompt);
        ThoughtProcessor.ClarifySystemPrompt.Should().Contain(
            "clarify the user's thought; do not improve the user's argument");
        result.Mode.Should().Be(ThoughtProcessingModes.Clarify);
    }

    [Fact]
    public async Task Stores_exactly_what_the_provider_returned_without_adding_anything()
    {
        // Leading whitespace, an internal newline and no trailing newline: the
        // stored text must be byte-for-byte what the provider answered.
        const string providerText = "  The snow was general.\nAll over Ireland.  ";
        var llm = new FakeLlmProvider().Returns(providerText);
        var processor = new ThoughtProcessor(llm);

        var result = await processor.ProcessAsync("raw thought", "light_polish");

        result.Text.Should().Be(providerText);
    }

    [Fact]
    public async Task An_empty_provider_answer_falls_back_to_the_raw_transcript()
    {
        // Measured free-pool behaviour: empty content with finish_reason
        // "length" means the budget went to reasoning. The capture must survive.
        var llm = new FakeLlmProvider().Enqueue(new LlmCompletion(string.Empty, "length", []));
        var processor = new ThoughtProcessor(llm);

        var result = await processor.ProcessAsync("the raw words", "clarify");

        result.Text.Should().Be("the raw words");
        result.Mode.Should().Be(ThoughtProcessingModes.Verbatim);
        result.ProviderCalled.Should().BeTrue();
        result.FellBackToRaw.Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_input_never_calls_the_provider()
    {
        var llm = new FakeLlmProvider().Returns("nope");
        var processor = new ThoughtProcessor(llm);

        var result = await processor.ProcessAsync("   ", "clarify");

        result.Text.Should().Be("   ");
        result.Mode.Should().Be(ThoughtProcessingModes.Verbatim);
        llm.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_token_budget_is_generous_and_clamped_to_the_provider_ceiling()
    {
        var llm = new FakeLlmProvider().Returns("ok");
        var processor = new ThoughtProcessor(llm);

        await processor.ProcessAsync("short", "light_polish");
        llm.LastRequest.MaxTokens.Should().Be(ThoughtProcessor.MaxTokensFloor);

        llm.Responder = _ => new LlmCompletion("ok", "stop", []);
        await processor.ProcessAsync(new string('x', 5000), "light_polish");
        llm.LastRequest.MaxTokens.Should().Be(ThoughtProcessor.MaxTokensCeiling);
    }
}

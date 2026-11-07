using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// A scriptable <see cref="ILlmProvider"/> for every test on the bridge path.
///
/// A real provider is NEVER called by a test — each call spends
/// pool quota and must remain measurable. This fake records the exact request it
/// was handed, so the orchestrator tests can assert the tool surface, the
/// conversation, and the iteration ceiling without touching a network.
/// </summary>
public sealed class FakeLlmProvider : ILlmProvider
{
    private readonly Queue<LlmCompletion> _scripted = new();

    /// <summary>Every request the orchestrator sent, in order.</summary>
    public List<LlmCompletionRequest> Requests { get; } = new();

    /// <summary>When set, thrown instead of returning a scripted completion.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Optional dynamic responder, keyed on the 1-based call number.</summary>
    public Func<int, LlmCompletion>? Responder { get; set; }

    /// <summary>
    /// Optional asynchronous responder that observes the caller cancellation token.
    /// Used by budget tests to prove an in-flight provider call is cancelled by the
    /// turn deadline rather than merely checked after it returns.
    /// </summary>
    public Func<int, CancellationToken, Task<LlmCompletion>>? AsyncResponder { get; set; }

    public int CallCount => Requests.Count;

    public LlmCompletionRequest LastRequest => Requests[^1];

    public FakeLlmProvider Enqueue(params LlmCompletion[] completions)
    {
        foreach (var completion in completions)
        {
            _scripted.Enqueue(completion);
        }

        return this;
    }

    /// <summary>Scripts a plain assistant reply.</summary>
    public FakeLlmProvider Returns(string content) =>
        Enqueue(new LlmCompletion(content, "stop", []));

    /// <summary>Scripts one tool call (with optional accompanying prose).</summary>
    public FakeLlmProvider CallsTool(string name, string argumentsJson = "{}", string? content = null) =>
        Enqueue(new LlmCompletion(
            content,
            "tool_calls",
            [new LlmToolCall(Guid.NewGuid().ToString("N"), name, argumentsJson)]));

    public async Task<LlmCompletion> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken ct = default)
    {
        Requests.Add(request);

        if (Failure is not null)
        {
            throw Failure;
        }

        if (AsyncResponder is not null)
        {
            return await AsyncResponder(Requests.Count, ct);
        }

        if (Responder is not null)
        {
            return Responder(Requests.Count);
        }

        // An unscripted call is a plain "nothing more to do" answer rather than
        // an exception, so a test that overshoots still fails on its assertions.
        return _scripted.Count > 0
            ? _scripted.Dequeue()
            : new LlmCompletion(string.Empty, "stop", []);
    }
}

/// <summary>
/// The fake is load-bearing for every orchestrator test, so its contract is
/// asserted rather than assumed.
/// </summary>
public sealed class FakeLlmProviderTests
{
    [Fact]
    public async Task Returns_scripted_completions_in_order_and_records_the_request()
    {
        var provider = new FakeLlmProvider()
            .Returns("first")
            .CallsTool("topics_list", """{"limit":5}""");

        var request = new LlmCompletionRequest(
            [LlmMessage.System("sys"), LlmMessage.User("hello")],
            [new LlmToolDefinition("topics_list", "Lists topics.", """{"type":"object"}""")],
            1234);

        var first = await provider.CompleteAsync(request);
        var second = await provider.CompleteAsync(request);

        first.Content.Should().Be("first");
        second.ToolCalls.Should().ContainSingle().Which.Name.Should().Be("topics_list");
        second.ToolCalls[0].ArgumentsJson.Should().Be("""{"limit":5}""");

        provider.CallCount.Should().Be(2);
        provider.LastRequest.MaxTokens.Should().Be(1234);
        provider.LastRequest.Messages.Should().HaveCount(2);
        provider.LastRequest.Tools.Should().ContainSingle();
    }

    [Fact]
    public async Task Throws_the_configured_failure()
    {
        var expected = new LlmException(LlmErrorCodes.Provider, "pool exploded");
        var provider = new FakeLlmProvider { Failure = expected };

        var act = () => provider.CompleteAsync(new LlmCompletionRequest([], [], 16));

        (await act.Should().ThrowAsync<LlmException>()).Which.Should().BeSameAs(expected);
    }
}

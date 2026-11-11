// Scripted + metering LLM providers for the quality bed (issue #566).
//
// QualityScriptedProvider is an owned copy of the FakeLlmProvider pattern
// (see Nostos.Backend.Tests/Services/Ai/FakeLlmProviderTests.cs): the shared
// support file is deliberately not modified. It adds per-turn scripts, a
// per-turn request log, and a shared gate so cancellation scenarios can
// coordinate a blocked provider call with POST /turn/cancel.
//
// MeteredLlmProvider wraps ANY ILlmProvider (scripted or the real
// OpenAiCompatibleLlmProvider) and records per upstream call: model id,
// started/finished timestamps, prompt/completion/thinking tokens, finish
// reason, requested tool-call names (name only), and error/exception type.
// It never records prompts, replies, arguments, or key material.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Diagnostics;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

/// <summary>Coordinates one blocked provider call with the runner's cancel hook.</summary>
internal sealed class QualityGate
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void SignalEntered() => _entered.TrySetResult();
}

internal sealed record QualityScriptContext(
    string ScenarioId,
    int TurnIndex,
    int CallIndex,
    LlmCompletionRequest Request,
    IReadOnlyList<AssistantTurnResponse> PriorResponses,
    QualityGate Gate,
    CancellationToken CancellationToken);

internal sealed class QualityScriptedProvider : ILlmProvider
{
    private Func<QualityScriptContext, Task<LlmCompletion>> _script =
        _ => Task.FromResult(QualityScript.Reply(string.Empty));

    private QualityGate _gate = new();
    private int _callIndex;

    public string ScenarioId { get; private set; } = string.Empty;

    public int TurnIndex { get; private set; }

    public IReadOnlyList<AssistantTurnResponse> PriorResponses { get; private set; } = [];

    /// <summary>Requests seen during the current turn (reset by BeginTurn).</summary>
    public List<LlmCompletionRequest> TurnRequests { get; } = new();

    public QualityGate Gate => _gate;

    public void BeginTurn(
        string scenarioId,
        int turnIndex,
        Func<QualityScriptContext, Task<LlmCompletion>> script,
        IReadOnlyList<AssistantTurnResponse> priorResponses)
    {
        ScenarioId = scenarioId;
        TurnIndex = turnIndex;
        _script = script;
        PriorResponses = priorResponses;
        _gate = new QualityGate();
        _callIndex = 0;
        TurnRequests.Clear();
    }

    public async Task<LlmCompletion> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken ct = default)
    {
        TurnRequests.Add(request);
        var context = new QualityScriptContext(
            ScenarioId, TurnIndex, _callIndex++, request, PriorResponses, _gate, ct);
        return await _script(context);
    }
}

/// <summary>Small builders for deterministic tool-call/Replay scripts.</summary>
internal static class QualityScript
{
    public static LlmCompletion ToolCall(string name, string argumentsJson = "{}", string? content = null) =>
        new(content, "tool_calls", [new LlmToolCall(Guid.NewGuid().ToString("N"), name, argumentsJson)]);

    public static LlmCompletion Reply(string content) =>
        new(content, "stop", []);

    /// <summary>Plays one completion per upstream call; extra calls get an empty stop.</summary>
    public static Func<QualityScriptContext, Task<LlmCompletion>> Play(params LlmCompletion[] steps) =>
        context => Task.FromResult(
            context.CallIndex < steps.Length ? steps[context.CallIndex] : Reply(string.Empty));

    public static Func<QualityScriptContext, Task<LlmCompletion>> Dynamic(
        Func<QualityScriptContext, Task<LlmCompletion>> fn) => fn;

    public static Func<QualityScriptContext, Task<LlmCompletion>> Throw(Exception exception) =>
        _ => Task.FromException<LlmCompletion>(exception);
}

/// <summary>
/// Records the completion requests one live turn actually sent, so the live
/// lane can read the tool results the model read exactly as deterministic
/// mode does. Only the request's messages are kept, and only the last request
/// after a turn boundary is read: chat history is cumulative, so that request
/// already carries every tool result of the turn. The key never reaches a
/// completion request, and this sink never sees an HTTP request.
/// </summary>
internal sealed class QualityProviderRequestCapture
{
    private readonly List<IReadOnlyList<LlmMessage>> _requests = new();
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _requests.Count; }
    }

    public void Record(LlmCompletionRequest request)
    {
        lock (_lock) _requests.Add(request.Messages);
    }

    public IReadOnlyList<QualityToolResultLog> ToolResultsSince(int count)
    {
        IReadOnlyList<LlmMessage>? last;
        lock (_lock) last = _requests.Count > count ? _requests[^1] : null;
        return last is null ? [] : QualityToolResultLog.From(last);
    }
}

internal sealed record QualityUpstreamCall(
    string Model,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    double DurationMs,
    int? PromptTokens,
    int? CompletionTokens,
    int? ThinkingTokens,
    string? FinishReason,
    IReadOnlyList<string> ToolNames,
    string? ErrorType);

internal sealed class QualityMetricsSink
{
    private readonly List<QualityUpstreamCall> _calls = new();
    private readonly object _lock = new();

    public int Count
    {
        get { lock (_lock) return _calls.Count; }
    }

    public void Add(QualityUpstreamCall call)
    {
        lock (_lock) _calls.Add(call);
    }

    public IReadOnlyList<QualityUpstreamCall> Since(int count)
    {
        lock (_lock) return _calls.Skip(count).ToList();
    }
}

internal sealed class MeteredLlmProvider(
    ILlmProvider inner,
    QualityMetricsSink sink,
    string modelId,
    QualityBudget? budget = null,
    // Optional: the live lane records the requests it sent so the revision
    // check can read the tool results the model read (#1573).
    QualityProviderRequestCapture? capture = null) : ILlmProvider
{
    public async Task<LlmCompletion> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken ct = default)
    {
        // The spend cap: no paid call starts once it is reached.
        budget?.ThrowIfExhausted();
        capture?.Record(request);
        var started = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var completion = await inner.CompleteAsync(request, ct);
            budget?.Record(request, completion);
            sink.Add(new QualityUpstreamCall(
                modelId, started, DateTimeOffset.UtcNow, elapsed.Elapsed.TotalMilliseconds,
                completion.PromptTokens, completion.CompletionTokens, completion.ThinkingTokens,
                completion.FinishReason,
                completion.ToolCalls.Select(call => call.Name).ToList(),
                ErrorType: null));
            return completion;
        }
        catch (Exception exception)
        {
            sink.Add(new QualityUpstreamCall(
                modelId, started, DateTimeOffset.UtcNow, elapsed.Elapsed.TotalMilliseconds,
                null, null, null, null, [],
                exception is LlmException llm
                    ? $"{exception.GetType().Name}:{llm.Code}"
                    : exception.GetType().Name));
            throw;
        }
    }
}

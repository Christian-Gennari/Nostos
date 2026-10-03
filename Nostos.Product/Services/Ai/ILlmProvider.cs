namespace Nostos.Backend.Services.Ai;

/// <summary>
/// The one operation the assistant bridge needs from an LLM: send a conversation
/// plus a set of tools and receive either prose or tool calls (issue #261 §3).
///
/// Deliberately this narrow — no streaming session, no vendor capability matrix,
/// no response-format zoo. The single cut this milestone supports is
/// non-streaming chat with tools; the 261-S2 bridge loops over tool calls in
/// process and never opens an SSE stream of its own. A provider that needs a
/// second shape can be added against a real caller then.
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// One chat completion. Failure is reported as a typed
    /// <see cref="LlmException"/> so the endpoint can map it to data; the
    /// credential is read at call time and never returned.
    /// </summary>
    Task<LlmCompletion> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken ct = default);
}

/// <summary>One message in the conversation sent to the provider.</summary>
public sealed record LlmMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ProviderState = null)
{
    public static LlmMessage System(string content) => new("system", content);

    public static LlmMessage User(string content) => new("user", content);

    public static LlmMessage Assistant(
        string? content,
        IReadOnlyList<LlmToolCall>? toolCalls = null,
        string? providerState = null) =>
        new("assistant", content, toolCalls, null, providerState);

    public static LlmMessage Tool(string toolCallId, string content) =>
        new("tool", content, null, toolCallId);
}

/// <summary>
/// A tool call the model asked for. <see cref="ArgumentsJson"/> is the raw
/// argument object as a JSON string — the orchestrator parses it and never
/// trusts it blindly.
/// </summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// One tool advertised to the model. The bridge generates these from the
/// capability registry's <c>Summary</c>; the parameter schema is intentionally
/// permissive because the registry's canonical argument readers already accept
/// the canonical camelCase request field names.
/// </summary>
public sealed record LlmToolDefinition(
    string Name,
    string Description,
    string ParametersJsonSchema);

/// <summary>
/// A single non-streaming completion request. <see cref="MaxTokens"/> is a
/// caller decision, not a provider default: the free pool spends reasoning
/// tokens even on a two-word answer, and a provider default would truncate.
/// </summary>
public sealed record LlmCompletionRequest(
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    int MaxTokens);

/// <summary>
/// A completion or an explicit "the pool spent its budget reasoning" outcome.
///
/// <see cref="Content"/> may be null and <see cref="FinishReason"/> may be
/// <c>"length"</c> — measured free-pool behaviour. That is data, not an error:
/// the bridge answers with whatever it has instead of surfacing a 500.
/// </summary>
public sealed record LlmCompletion(
    string? Content,
    string? FinishReason,
    IReadOnlyList<LlmToolCall> ToolCalls,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? ThinkingTokens = null,
    string? ProviderState = null)
{
    /// <summary>True when the pool spent the whole budget and returned no text.</summary>
    public bool IsLengthTruncated =>
        string.IsNullOrWhiteSpace(Content)
        && string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Stable codes for every way the LLM bridge can fail, so a failure is data the
/// endpoint maps to a status and the client can branch on, never an exception
/// the global handler turns into a 500.
/// </summary>
public static class LlmErrorCodes
{
    /// <summary>The integration is switched off (<c>Assistant:Enabled=false</c>).</summary>
    public const string Disabled = "assistant_disabled";

    /// <summary>Enabled, but the configured environment variable holds no key, or the gateway URL/model is missing.</summary>
    public const string NotConfigured = "assistant_not_configured";

    /// <summary>The host policy does not allow assistant access.</summary>
    public const string AccessDenied = "assistant_access_denied";

    /// <summary>The provider rejected the configured credential.</summary>
    public const string Permission = "assistant_permission_denied";

    /// <summary>The provider is temporarily rate limiting requests.</summary>
    public const string RateLimited = "assistant_rate_limited";

    /// <summary>The provider did not answer within its request timeout.</summary>
    public const string Timeout = "assistant_provider_timeout";

    /// <summary>The provider is unreachable or returned an unexpected status.</summary>
    public const string Provider = "assistant_provider_error";

    /// <summary>The provider answered with a body this provider cannot read.</summary>
    public const string InvalidResponse = "assistant_response_invalid";
}

/// <summary>
/// The only exception <see cref="ILlmProvider"/> is allowed to throw for a
/// provider problem. Cancellation is deliberately NOT wrapped, so it stays an
/// <see cref="OperationCanceledException"/> and is never mistaken for a 500.
/// </summary>
public sealed class LlmException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static LlmException Disabled() => new(
        LlmErrorCodes.Disabled,
        "The assistant is disabled. Set Assistant:Enabled=true and configure a provider key to use it.");

    public static LlmException NotConfigured(string environmentVariable) => new(
        LlmErrorCodes.NotConfigured,
        $"The assistant is enabled but environment variable '{environmentVariable}' is not set or empty.");

    public static LlmException NotConfiguredSetting(string setting) => new(
        LlmErrorCodes.NotConfigured,
        $"The assistant is enabled but '{setting}' is not set.");

    public static LlmException PermissionDenied() => new(
        LlmErrorCodes.Permission,
        "The assistant provider rejected the configured credential.");

    public static LlmException RateLimited() => new(
        LlmErrorCodes.RateLimited,
        "Ask Nostos is temporarily busy. Try again shortly.");

    public static LlmException TimedOut() => new(
        LlmErrorCodes.Timeout,
        "Ask Nostos timed out while contacting the AI service.");

    public static LlmException ProviderFailure(string detail) => new(
        LlmErrorCodes.Provider,
        $"The assistant provider failed: {detail}");

    public static LlmException InvalidResponse(string detail) => new(
        LlmErrorCodes.InvalidResponse,
        $"The assistant provider returned an unexpected response: {detail}");
}

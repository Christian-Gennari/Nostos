using Nostos.Backend.Services.Ai;

namespace Nostos.Product.Services.Ai;

/// <summary>Provider-neutral usage categories exposed to host implementations.</summary>
public enum AiUsageKind
{
    Llm,
    Stt,
    Tts,
}

/// <summary>Generic host policy outcomes for AI usage.</summary>
public enum AiUsageBlockReason
{
    AccessDenied,
    RateLimited,
    LimitReached,
    Disabled,
    TemporarilyUnavailable,
}

public sealed class AiUsageException(AiUsageBlockReason reason, string message) : Exception(message)
{
    public AiUsageBlockReason Reason { get; } = reason;

    /// <summary>The stable error code every endpoint reports this block under.</summary>
    public string Code => Reason switch
    {
        AiUsageBlockReason.AccessDenied => "ai_access_denied",
        AiUsageBlockReason.RateLimited => "ai_rate_limited",
        AiUsageBlockReason.LimitReached => "ai_usage_limit_reached",
        AiUsageBlockReason.Disabled => "ai_disabled",
        _ => "ai_temporarily_unavailable",
    };

    /// <summary>The HTTP status every endpoint answers this block with.</summary>
    public int StatusCode => Reason switch
    {
        AiUsageBlockReason.AccessDenied => 403,
        AiUsageBlockReason.RateLimited or AiUsageBlockReason.LimitReached => 429,
        _ => 503,
    };
}

public sealed record AiUsageLease(Guid ReservationId, AiUsageKind Kind)
{
    /// <summary>
    /// The host's cost bound for the provider calls this LLM lease pays for, or
    /// null when the host sets none (SelfHosted). When present, every provider
    /// call under the lease asks it for an output-token limit first and reports
    /// its completion afterwards, so a host whose reservation is a fixed amount
    /// can keep the call's worst-case cost inside it (Nostos-Cloud issue 654).
    /// </summary>
    public ILlmCallBudget? LlmBudget { get; init; }
}

/// <summary>
/// A host-owned cost bound for the provider calls of one metered LLM lease: one
/// Ask Nostos turn (up to <c>Assistant:MaxToolIterations</c> calls with a
/// growing conversation) or one note rewrite (a single call).
///
/// Product code never sees a price. It only asks, before each call, how many
/// output tokens the call may produce, and reports each completion so the host
/// can count what the lease has spent. A budget instance belongs to one lease
/// and is used by one caller at a time.
/// </summary>
public interface ILlmCallBudget
{
    /// <summary>
    /// The largest output-token limit for the call about to send
    /// <paramref name="request"/> such that the lease's worst-case cost (the
    /// calls already reported plus this one at its full input and this limit)
    /// stays inside what the host reserved. Zero or less means the remaining
    /// budget cannot pay for this call, and the caller must not send it.
    /// </summary>
    int MaxOutputTokens(LlmCompletionRequest request);

    /// <summary>Reports a completed call's provider-reported usage.</summary>
    void Record(LlmCompletion completion);
}

public sealed record LlmProviderUsage(
    int UpstreamRequestCount,
    int? InputTokens,
    int? OutputTokens,
    int? ThinkingTokens,
    int? ReportedTotalTokens,
    int ToolLoopIterations,
    string ResultCategory,
    string? ProviderFinishReason);

public sealed record TranscriptionUsage(
    int UpstreamRequestCount,
    double? DurationSeconds,
    string ResultCategory);

/// <summary>Read-aloud usage. Speech is priced by the characters spoken.</summary>
public sealed record SpeechSynthesisUsage(
    int UpstreamRequestCount,
    int Characters,
    string ResultCategory);

/// <summary>
/// Optional host policy for assistant and transcription access. The product
/// never derives identity or subscription state; a host supplies this decision.
/// </summary>
public interface IAiAccessPolicy
{
    Task<bool> IsAllowedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional host accounting boundary. Implementations may reserve and report
/// provider usage without exposing provider credentials or host billing details
/// to product code.
/// </summary>
public interface IAiUsageAccountingService
{
    Task<AiUsageLease?> BeginLlmTurnAsync(CancellationToken cancellationToken = default);
    Task CompleteLlmTurnAsync(
        AiUsageLease? lease,
        LlmProviderUsage usage,
        CancellationToken cancellationToken = default);

    Task<AiUsageLease?> BeginSttAsync(CancellationToken cancellationToken = default);
    Task CompleteSttAsync(
        AiUsageLease? lease,
        TranscriptionUsage usage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves read-aloud usage for <paramref name="characters"/> characters.
    /// Default-implemented so a host that predates read-aloud keeps compiling;
    /// such a host registers no <see cref="IReadAloudProvider"/> either, so
    /// nothing reaches this unmetered.
    /// </summary>
    Task<AiUsageLease?> BeginTtsAsync(
        int characters,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AiUsageLease?>(null);

    Task CompleteTtsAsync(
        AiUsageLease? lease,
        SpeechSynthesisUsage usage,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Permissive access policy used by a local SelfHosted installation.</summary>
public sealed class AllowAllAiAccessPolicy : IAiAccessPolicy
{
    public Task<bool> IsAllowedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}

/// <summary>No-op usage accounting used by a local BYOK installation.</summary>
public sealed class NoOpAiUsageAccountingService : IAiUsageAccountingService
{
    public static NoOpAiUsageAccountingService Instance { get; } = new();

    public Task<AiUsageLease?> BeginLlmTurnAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AiUsageLease?>(null);

    public Task CompleteLlmTurnAsync(
        AiUsageLease? lease,
        LlmProviderUsage usage,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<AiUsageLease?> BeginSttAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AiUsageLease?>(null);

    public Task CompleteSttAsync(
        AiUsageLease? lease,
        TranscriptionUsage usage,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

}

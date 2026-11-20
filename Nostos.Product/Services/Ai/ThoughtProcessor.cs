using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Turns a post-processing mode into a prompt, sends it over the existing
/// <see cref="ILlmProvider"/> bridge, and returns the text to store (issue #262
/// §7, §8).
///
/// Three properties are structural, not prompt goodwill:
/// <list type="bullet">
/// <item><b>Verbatim never calls the provider.</b> It short-circuits before the
/// bridge is touched, so the default capture is a storage operation, not a
/// generation.</item>
/// <item><b>Only the user's thought is sent.</b> A quote is the capture's
/// <c>SelectedText</c> and never reaches this type, so no mode can rewrite it.</item>
/// <item><b>An empty answer is a fallback, not a loss.</b> The free pool spends
/// its budget reasoning (measured); when it returns no text the raw transcript is
/// kept and the effective mode is reported as verbatim.</item>
/// <item><b>A cut-off rewrite is not stored.</b> A reply the provider ended at
/// its output limit would silently drop the end of the thought, so it falls
/// back to the raw transcript the same way.</item>
/// </list>
///
/// The provider call is metered like an Ask Nostos turn: a host reserves usage
/// before it, may bound its output through <see cref="AiUsageLease.LlmBudget"/>,
/// and settles it afterwards. SelfHosted uses the no-op accounting service.
/// </summary>
public sealed class ThoughtProcessor(
    ILlmProvider llm,
    IAiUsageAccountingService? usageAccounting = null) : IThoughtProcessor
{
    private readonly IAiUsageAccountingService _usage =
        usageAccounting ?? NoOpAiUsageAccountingService.Instance;

    /// <summary>
    /// Never ask for less than this: the pool consumes reasoning tokens even for
    /// a two-word answer, and a tight budget truncates real text.
    /// </summary>
    public const int MaxTokensFloor = 2048;

    /// <summary>The provider's own ceiling; asking for more is silently clamped there anyway.</summary>
    public const int MaxTokensCeiling = 8192;

    /// <summary>
    /// Light polish: copy-edit, never re-argue. Exposed so tests can assert the
    /// exact instruction that travelled with the raw text.
    /// </summary>
    public const string LightPolishSystemPrompt =
        """
        You are a careful copy editor for a personal note-taking app. Rewrite the user's note so it reads cleanly: remove filler words and spoken disfluencies, correct grammar, spelling and punctuation, and smooth awkward phrasing.

        Preserve the author's meaning, stance, uncertainty and voice exactly. Never make a hedged or uncertain thought sound confident. Never add or remove ideas, arguments, evidence, examples or conclusions. Rewrite the user's thought; do not improve the user's argument.

        Return only the rewritten note. No preamble, no commentary, and no quotation marks around the text.
        """;

    /// <summary>
    /// Clarify: reorganise the user's thought, invent nothing. Exposed so tests
    /// can assert the exact instruction that travelled with the raw text.
    /// </summary>
    public const string ClarifySystemPrompt =
        """
        You reorganise a person's own note so the intended idea is clearer. The guiding rule is: clarify the user's thought; do not improve the user's argument.

        Tighten the structure and wording. You may make uncertainty or tension that is already present explicit. You must not introduce arguments, evidence, examples, interpretations or conclusions the author did not express, and you must not strengthen or weaken their claims or stance. Preserve the author's voice.

        Return only the reorganised note. No preamble, no commentary, and no quotation marks around the text.
        """;

    public async Task<ThoughtProcessingResult> ProcessAsync(
        string text,
        string mode,
        CancellationToken ct = default)
    {
        // Nothing to process (a quote-only capture, for instance). No provider call.
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ThoughtProcessingResult(text, ThoughtProcessingModes.Verbatim, ProviderCalled: false);
        }

        var normalized = ThoughtProcessingModes.Normalize(mode);
        if (normalized == ThoughtProcessingModes.Verbatim)
        {
            return new ThoughtProcessingResult(text, ThoughtProcessingModes.Verbatim, ProviderCalled: false);
        }

        var prompt = normalized == ThoughtProcessingModes.Clarify
            ? ClarifySystemPrompt
            : LightPolishSystemPrompt;

        var request = new LlmCompletionRequest(
            [LlmMessage.System(prompt), LlmMessage.User(text)],
            Tools: [],
            MaxTokens: MaxTokensFor(text));

        // A host can refuse the call here (AiUsageException); nothing is spent.
        var lease = await _usage.BeginLlmTurnAsync(ct);
        if (lease?.LlmBudget is { } budget)
        {
            var affordable = budget.MaxOutputTokens(request);
            if (affordable < MaxTokensFloor)
            {
                // The host budget cannot pay for a rewrite with room to answer.
                // Keep the raw words and settle a turn that made no call.
                await SettleAsync(lease, new LlmProviderUsage(0, 0, 0, 0, 0, 0, "SafetyCeiling", null));
                return new ThoughtProcessingResult(
                    text,
                    ThoughtProcessingModes.Verbatim,
                    ProviderCalled: false,
                    FellBackToRaw: true);
            }

            if (affordable < request.MaxTokens)
                request = request with { MaxTokens = affordable };
        }

        LlmCompletion completion;
        try
        {
            completion = await llm.CompleteAsync(request, ct);
        }
        catch (Exception ex) when (ex is LlmException or OperationCanceledException)
        {
            // The call was attempted, so its unknown cost is settled, never
            // treated as free. The caller keeps its own failure handling.
            await SettleAsync(lease, new LlmProviderUsage(
                1, null, null, null, null, 0,
                ex is LlmException ? "ProviderError" : "Cancelled",
                null));
            throw;
        }

        lease?.LlmBudget?.Record(completion);
        await SettleAsync(lease, new LlmProviderUsage(
            1,
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.ThinkingTokens,
            completion.PromptTokens is { } input && completion.CompletionTokens is { } output
                ? input + output
                : null,
            0,
            "Completed",
            completion.FinishReason));

        var cutShort = string.Equals(completion.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(completion.Content) || cutShort)
        {
            // Measured free-pool behaviour: an empty content with finish_reason
            // "length" means the budget went to reasoning. That is data, not an
            // error — keep the raw transcript rather than storing nothing. A
            // non-empty reply that stopped at the limit is a partial rewrite,
            // and storing it would silently lose the end of the thought.
            return new ThoughtProcessingResult(
                text,
                ThoughtProcessingModes.Verbatim,
                ProviderCalled: true,
                FellBackToRaw: true);
        }

        // Store exactly what the provider returned. The raw transcript remains
        // separate and is the source of any later re-run.
        return new ThoughtProcessingResult(completion.Content, normalized, ProviderCalled: true);
    }

    // Settlement never runs on the request token: a disconnect must not strand
    // the host's reservation.
    private Task SettleAsync(AiUsageLease? lease, LlmProviderUsage usage) =>
        lease is null
            ? Task.CompletedTask
            : _usage.CompleteLlmTurnAsync(lease, usage, CancellationToken.None);

    /// <summary>
    /// A generous, input-scaled budget: the pool's own injected prompt and its
    /// reasoning tokens are not free, so a short note still gets room to answer.
    /// </summary>
    private static int MaxTokensFor(string text) =>
        Math.Clamp(1024 + (text.Length * 4), MaxTokensFloor, MaxTokensCeiling);
}

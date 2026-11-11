namespace Nostos.Backend.Configuration;

/// <summary>
/// Assistant LLM bridge configuration (issue #261 §3, decision D2/D8).
///
/// This section is now the FALLBACK for the AI-provider settings: the effective
/// configuration is the stored override when set, otherwise the values here
/// (see <c>IAiProviderConfigResolver</c>). Availability is DERIVED, not a manual
/// server boolean — see <c>EffectiveAiProviderConfig.IsAvailable</c>.
/// <see cref="Enabled"/> is only a hard kill switch and defaults ON, so a
/// configured gateway is usable without anyone editing configuration. When the
/// assistant is unavailable the endpoints are still mapped, but answer with a
/// typed "not configured"/"disabled" error rather than calling a provider or
/// 500-ing. The credential is never read from configuration — only the NAME of
/// the environment variable that holds it — so a committed key is impossible and
/// nothing can be shipped to the Angular client.
///
/// The default <see cref="Model"/> must be a model that ACTUALLY CALLS TOOLS.
/// The pooled free model that used to sit here silently ignored the tool
/// definitions: measured against the gateway, the identical request returned
/// <c>finish_reason: "stop"</c> with zero tool calls, so every capture, search
/// and suggestion the assistant described had in fact not run. Do not put a
/// pooled/multi-vendor id back here without proving tool calling works.
/// </summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>
    /// Hard kill switch. Defaults ON: readiness (a configured key and base URL)
    /// is what actually decides whether the assistant runs, so the feature is
    /// not dead just because nobody flipped a server boolean. Set false only to
    /// take the assistant down entirely.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL of the OpenAI-compatible gateway, INCLUDING the <c>/v1</c>
    /// suffix. The provider appends <c>chat/completions</c> to it.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Model id sent verbatim. It must call tools: capture, reading the
    /// library and proposing topics all run through tool calls, and a model
    /// that ignores them answers as though it had done the work.
    /// Empty by default: model names differ per provider (<c>gpt-4o-mini</c>,
    /// <c>openai/gpt-4o-mini</c>, <c>gemini/…</c>), so a built-in name only
    /// suits one gateway and looked like a choice the owner had made.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Name of the environment variable holding the bearer token.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "NOSTOS_ASSISTANT_TOKEN";

    /// <summary>
    /// Absolute runaway ceiling on upstream LLM calls in one turn, not the normal
    /// UX budget. The external measurement of 2026-09-22 (900 real turns) saw at
    /// most five calls, but those turns could not reorganise a library: a whole
    /// regrouping reads the library, stages deletes, creates and filings, and
    /// corrects a refused step, which takes six to ten calls. Repeated-tool
    /// detection still stops a looping turn early.
    /// </summary>
    public int MaxToolIterations { get; set; } = 12;

    /// <summary>
    /// Cumulative provider-reported token ceiling for one turn (prompt + output;
    /// thinking is already inside output). Zero or below disables the ceiling.
    /// The measured maximum was 31,875 tokens; a library reorganisation re-sends
    /// the library overview on every call, so 120,000 leaves room for a few
    /// hundred books while still bounding a runaway turn. Provider pricing varies
    /// by BYOK configuration and is handled by the provider account.
    /// </summary>
    public int MaxTurnTokens { get; set; } = 120_000;

    // The turn's clocks, in the order they fire. The first two share the
    // turn's TimeProvider (see AssistantOrchestrator):
    //
    //   1. Per provider request — RequestTimeoutSeconds (60 s). A stalled call
    //      fails with assistant_provider_timeout ("Ask Nostos took too long to
    //      answer. Try again."), never as an execution limit.
    //   2. Transport backstop — RequestTimeoutSeconds + 5 s, set on the named
    //      HttpClient in Program.cs. It only covers the case where the
    //      in-process clock cannot run, and maps to the same typed timeout.
    //   3. Whole turn — MaxTurnElapsedMilliseconds (90 s), across every request
    //      and tool round. A request started near the deadline gets only the
    //      time that is left; when that is less than clock 1, the turn clock is
    //      the one that fires. This is the only clock that reports an execution
    //      limit.
    //
    // There is no per-tool timer and no server-side stream-idle timer: tools
    // are in-process, and a mutation that has already begun is allowed to
    // reach its terminal state rather than be abandoned mid-write.
    //
    // AssistantExecutionBudget.ValidateClocks requires at least
    // MinimumToolRoundMarginMilliseconds between clocks 1 and 3 while clock 3
    // is on, so one request can always time out inside a fresh turn and leave
    // room for one tool round. The shipped 60 s / 90 s pair leaves 30 s.

    /// <summary>
    /// Wall-clock ceiling for one turn in milliseconds. Zero or below disables the
    /// ceiling. Selected from the same measurement: 99% of turns finished inside
    /// 14.6 s, while 8 of 900 stalled for 141-145 s waiting on a single slow upstream
    /// call. This is the OUTER clock: <see cref="RequestTimeoutSeconds"/> is shorter
    /// on purpose, so a stalled provider request reports its own timeout before the
    /// turn can report an execution limit.
    /// </summary>
    public int MaxTurnElapsedMilliseconds { get; set; } = 90_000;

    /// <summary>
    /// Per-provider-request ceiling in seconds. This is the INNER clock: it must
    /// stay below <see cref="MaxTurnElapsedMilliseconds"/> by at least one tool
    /// round, and startup rejects a configuration that does not (see
    /// <c>AssistantExecutionBudget.ValidateClocks</c>). 60 s is four times the
    /// measured 99th-percentile turn (14.6 s) and cuts the 141-145 s stalls
    /// observed in the 900-turn run. The named HttpClient's transport timeout is
    /// this value plus a small grace; it is a backstop, not the primary clock.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Extra seconds added to the named assistant HttpClient's timeout so the
    /// orchestrator's in-process request clock normally fires first. A transport
    /// timeout maps to the same typed provider timeout, so the grace never changes
    /// what the person sees.
    /// </summary>
    public const int TransportTimeoutGraceSeconds = 5;

    /// <summary>
    /// The in-process per-request clock as a duration. A non-positive configured
    /// value is treated as the one-second floor the transport has always used;
    /// startup validation rejects it instead of silently changing the ordering.
    /// </summary>
    public TimeSpan RequestTimeout =>
        TimeSpan.FromSeconds(Math.Max(1, RequestTimeoutSeconds));

    /// <summary>
    /// The named assistant HttpClient's whole-request timeout. Deliberately a few
    /// seconds longer than <see cref="RequestTimeout"/> so a stalled call normally
    /// fails on the orchestrator's typed clock first; this covers only the case
    /// where that timer could not run.
    /// </summary>
    public TimeSpan TransportTimeout =>
        RequestTimeout + TimeSpan.FromSeconds(TransportTimeoutGraceSeconds);

    /// <summary>
    /// The change policy's limits (<see cref="Integrations.Assistant.AssistantChangePolicy"/>):
    /// a turn's library changes apply at once, with undo, only while they stay
    /// within all of these; anything larger waits for the user's review.
    /// </summary>
    public int AutoApplyMaxBooksTouched { get; set; } = 25;

    /// <summary>Most books a changeset may take out of a collection without review.</summary>
    public int AutoApplyMaxBooksLosingMembership { get; set; } = 3;

    /// <summary>Most books whose own details a changeset may edit without review.</summary>
    public int AutoApplyMaxBookEdits { get; set; } = 10;

    /// <summary>Most empty collections a changeset may delete without review.</summary>
    public int AutoApplyMaxEmptyCollectionDeletes { get; set; } = 3;
}

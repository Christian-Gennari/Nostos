// Scenario catalogue types for the #566 quality bed.
//
// Each scenario declares its turns, structural expectations, deterministic
// checks, the mode(s) it can run in, and a qualitative rubric for the later
// blinded human scoring. The verbatim #566 description is kept on Verbatim.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

[Flags]
internal enum QualityModes
{
    Deterministic = 1,
    Live = 2,
}

internal enum QualityMutation
{
    /// <summary>No write may happen on any turn of the scenario.</summary>
    None,
    /// <summary>Exactly one capture write with a stable receipt.</summary>
    ExactlyOnceCapture,
    /// <summary>A destructive step is proposed and waits for explicit approval.</summary>
    ApprovalRequired,
}

/// <summary>Structural expectations evaluated per turn, in both modes.</summary>
internal sealed record QualityTurnExpect(
    string[] RequiredTools,
    string[] ForbiddenTools,
    Guid[] GoldNoteIds,
    int MinEvidence = 0,
    int MaxUpstreamCalls = 6,
    int MaxToolCalls = 6,
    string[]? ReplyMustContain = null,
    string[]? ReplyMustNotContain = null,
    string? ExpectedErrorCode = null,
    string? ExpectedErrorMessageContains = null,
    string? ExpectedAnchorPromptKind = null,
    bool ExpectPendingPlan = false,
    bool ExpectCapturedNote = false,
    Guid? ExpectFirstEvidenceNoteId = null,
    int ExpectedNoteDelta = 0,
    int ExpectedCollectionDelta = 0,
    string[]? ExpectedExecuted = null)
{
    public static QualityTurnExpect Empty => new([], [], []);
}

internal sealed record QualityHookContext(
    HttpClient Client,
    string ConversationId,
    string TurnId,
    QualityGate Gate);

internal sealed record QualityTurnVerifyContext(
    IServiceProvider Services,
    QualityTurnRecord Turn,
    IReadOnlyList<QualityTurnRecord> PriorTurns,
    QualityModes Mode);

internal sealed record QualityTurnSpec(
    string Message,
    Func<AssistantContextDto>? Context = null,
    // Deterministic-mode script. Null means "no provider call expected";
    // the runner fails the turn if the provider IS called with no script.
    Func<QualityScriptContext, Task<LlmCompletion>>? Script = null,
    QualityTurnExpect? Expect = null,
    // When true, the previous turn's AnchorPrompt.ContinuationId travels as
    // this turn's ContinuationId and Message is the user's answer.
    bool ContinueFromPrompt = false,
    // Extra product-level check (DB state, request shape). Null = pass.
    // Returns a failure description, or null when the check passes.
    Func<QualityTurnVerifyContext, Task<string?>>? Verify = null,
    // Runs after the stream 'started' event (cancellation scenarios).
    Func<QualityHookContext, Task>? Hook = null);

internal sealed record QualityScenario(
    string Id,
    string Title,
    string Verbatim,
    string Goal,
    string Rubric,
    QualityModes Modes,
    string ModeNote,
    IReadOnlyList<QualityTurnSpec> Turns);

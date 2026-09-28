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
    string[]? ExpectedExecuted = null,
    // LIVE ONLY: alternative gold expressed as imported-book passages. The
    // turn's gold requirement is satisfied if EITHER every GoldNoteIds note
    // is present in note evidence, OR at least one book_text evidence item
    // matches a declared entry (same Handle.BookId, Excerpt containing any
    // marker case-insensitively). Deterministic mode ignores this field.
    (string BookId, string[] ExcerptMarkers)[]? GoldBookPassages = null,
    // LIVE ONLY: expected-error turns also pass when the observed code
    // matches any entry here. Deterministic mode ignores this field.
    string[]? AlsoAcceptErrorCodes = null,
    // DETERMINISTIC ONLY: notes that must be present alongside gold. In live
    // mode they are not required; an absence is recorded as an advisory.
    Guid[]? CoexistenceNoteIds = null,
    // LIVE ONLY override for MinEvidence. Deterministic mode uses MinEvidence.
    int? LiveMinEvidence = null,
    // LIVE ONLY: requesting these tool names records an advisory instead of
    // failing. Keep them out of ForbiddenTools; deterministic mode relies on
    // the scenario's Verify hooks plus ExpectedExecuted/NoPendingPlan checks.
    string[]? AdvisoryTools = null,
    // LIVE ONLY: a Verify hook failure becomes an advisory instead of a
    // failure. The re-read requirement it guards stays hard via
    // RequiredTools/evidence checks unless the catalogue says otherwise.
    bool VerifyAdvisoryInLive = false)
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

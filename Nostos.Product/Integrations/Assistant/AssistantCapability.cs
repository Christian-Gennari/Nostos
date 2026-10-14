using System.Text.Json;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Per-invocation context assembled by the orchestrator. Capture and Act use
/// the client/idempotency pair for exactly-once retries; Stage capabilities add
/// their operations to <see cref="Changeset"/>, the turn's draft.
/// </summary>
public sealed record AssistantToolContext(
    string? ClientId = null,
    string? IdempotencyKey = null,
    AssistantChangesetDraft? Changeset = null);

/// <summary>
/// Outcome of an assistant capability. Failures are data, never exceptions, so
/// the orchestrator can turn any result into a reply without a try/catch. A
/// refused or invalid call also guarantees it touched no store.
/// </summary>
public sealed record AssistantToolResult(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    JsonElement? Data = null)
{
    public static AssistantToolResult Ok(JsonElement? data = null) =>
        new(true, null, null, data);

    public static AssistantToolResult Fail(
        string errorCode,
        string errorMessage,
        JsonElement? data = null) =>
        new(false, errorCode, errorMessage, data);
}

/// <summary>
/// The typed failure codes the assistant surface emits. Canonical service
/// failures pass their own code through unchanged (e.g. <c>topic_not_found</c>),
/// so a client never has to learn a second vocabulary for the same condition.
/// </summary>
public static class AssistantErrorCodes
{
    public const string UnknownCapability = "assistant_unknown_capability";
    public const string ChangesetUnavailable = "assistant_changeset_unavailable";
    public const string ChangesetNotFound = "assistant_changeset_not_found";
    public const string ChangesetTokenMismatch = "assistant_changeset_token_mismatch";
    public const string ChangesetNotReviewable = "assistant_changeset_not_reviewable";
    public const string ChangesetNotUndoable = "assistant_changeset_not_undoable";
    public const string ChangesetBusy = "assistant_changeset_busy";
    public const string NotPermitted = "assistant_not_permitted";
    public const string InvalidArguments = "assistant_invalid_arguments";
    public const string NotFound = "assistant_not_found";
    public const string ContinuationNotFound = "assistant_continuation_not_found";
    public const string ContinuationMismatch = "assistant_continuation_mismatch";
    public const string ContinuationAnswerRequired = "assistant_continuation_answer_required";
    public const string InvalidProcessingMode = "invalid_processing_mode";
    public const string TurnAlreadyRunning = "assistant_turn_already_running";
    public const string TurnCancelled = "assistant_turn_cancelled";
    public const string ExecutionBudgetExhausted = "assistant_execution_budget_exhausted";
    public const string PartiallyCompleted = "assistant_turn_partially_completed";
    public const string UnverifiedCompletionClaim = "assistant_unverified_completion_claim";
    public const string NoEvidence = "assistant_no_evidence";
    public const string SourceIndexingPending = "assistant_source_indexing_pending";
    public const string SourceIndexingFailed = "assistant_source_indexing_failed";
    public const string SourceIndexingUnsupported = "assistant_source_indexing_unsupported";
}

/// <summary>
/// One entry in the assistant's deliberately small action surface. Everything
/// it can do is exactly the set returned by
/// <see cref="AssistantCapabilities.Build"/>; there is no dynamic discovery.
/// </summary>
/// <param name="Name">The wire name the model calls.</param>
/// <param name="Trust">How the capability runs; see <see cref="AssistantTrustClass"/>.</param>
/// <param name="Category">Product-domain metadata for measurement and possible deterministic grouping. This never grants authority.</param>
/// <param name="Summary">The tool description the model reads.</param>
/// <param name="ParametersJsonSchema">
/// The JSON Schema advertised for this capability's arguments. It is
/// documentation, not enforcement: <c>additionalProperties</c> stays true and the
/// canonical readers accept the camelCase request field names they always have.
/// The declared property names must match the names this capability's reader
/// actually consumes, or the model is told about an argument nothing reads.
/// </param>
/// <param name="Execute">The canonical-service delegate.</param>
public sealed record AssistantCapability(
    string Name,
    AssistantTrustClass Trust,
    AssistantCapabilityCategory Category,
    string Summary,
    string ParametersJsonSchema,
    Func<AssistantToolContext, JsonElement, CancellationToken, Task<AssistantToolResult>> Execute);

using System.Text.Json;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// A user approval bound to exactly one plan id (issue #260, decision D7). The
/// token authorises the <see cref="AssistantTrustClass.PlanAndAct"/>
/// capabilities of that plan and nothing else: presenting it for a call built
/// against a different plan is refused by the registry before any store is
/// touched.
/// </summary>
public sealed record AssistantPlanApproval(string PlanId, string ApprovalToken);

/// <summary>
/// Per-invocation context assembled by the caller (the 261-S2 orchestrator).
/// <see cref="PlanId"/> identifies the plan this call was built against;
/// <see cref="Approval"/> carries the user's approval of that plan, when one
/// exists. Capture uses the client/idempotency pair for exactly-once retries.
/// </summary>
public sealed record AssistantToolContext(
    string? ClientId = null,
    string? IdempotencyKey = null,
    string? PlanId = null,
    AssistantPlanApproval? Approval = null);

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
/// failures pass their own code through unchanged (e.g. <c>concept_not_found</c>),
/// so a client never has to learn a second vocabulary for the same condition.
/// </summary>
public static class AssistantErrorCodes
{
    public const string UnknownCapability = "assistant_unknown_capability";
    public const string ApprovalRequired = "assistant_approval_required";
    public const string ApprovalPlanMismatch = "assistant_approval_plan_mismatch";
    public const string InvalidArguments = "assistant_invalid_arguments";
    public const string NotFound = "assistant_not_found";
    public const string ContinuationNotFound = "assistant_continuation_not_found";
    public const string ContinuationMismatch = "assistant_continuation_mismatch";
    public const string ContinuationAnswerRequired = "assistant_continuation_answer_required";
    public const string InvalidProcessingMode = "invalid_processing_mode";
    public const string TurnAlreadyRunning = "assistant_turn_already_running";
    public const string TurnCancelled = "assistant_turn_cancelled";
    public const string ExecutionBudgetExhausted = "assistant_execution_budget_exhausted";
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
/// <param name="Trust">The enforcement class; the registry refuses a PlanAndAct call without a matching approval.</param>
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

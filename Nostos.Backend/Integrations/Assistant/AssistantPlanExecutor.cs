using System.Text.Json;
using System.Text.Json.Nodes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Executes a previously stored approval-required plan exactly once. The plan
/// store remains authoritative for token validation and supersession; this
/// executor preserves ordered step execution and per-step idempotency keys.
/// </summary>
internal sealed class AssistantPlanExecutor(
    AssistantCapabilityRegistry registry,
    AssistantPlanStore plans)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AssistantPlanApproveResponse> ApproveAsync(
        string? planId,
        string? approvalToken,
        CancellationToken ct = default)
    {
        var outcome = plans.Approve(planId, approvalToken);
        if (outcome.TerminalResponse is { } terminal)
        {
            // Lost-response retry: replay execution truth without making the
            // consumed destructive plan executable again.
            return terminal;
        }

        if (!outcome.Approved || outcome.Plan is null)
        {
            return new AssistantPlanApproveResponse(
                false,
                outcome.ErrorCode,
                outcome.ErrorMessage,
                []);
        }

        var plan = outcome.Plan;

        var results = new List<AssistantPlanStepOutcomeDto>(plan.Steps.Count);
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];

            // Each step needs its OWN idempotency key. Canonical library commands
            // dedupe on (client, key), so reusing the plan's single key would make
            // every step after the first a no-op replay of the first — a
            // multi-step reorganisation would silently apply only step one.
            // Deriving from the consumed plan id keeps the key stable and short.
            var context = new AssistantToolContext(
                ClientId: plan.ConversationKey,
                IdempotencyKey: $"{plan.PlanId}:{index}",
                PlanId: plan.PlanId,
                Approval: new AssistantPlanApproval(plan.PlanId, plan.ApprovalToken));

            var args = ParseArguments(step.ArgumentsJson);
            var result = await registry.InvokeAsync(step.Capability, args, context, ct);

            results.Add(new AssistantPlanStepOutcomeDto(
                step.Capability,
                result.Success,
                result.ErrorCode,
                result.ErrorMessage,
                result.Data));

            // Execute the plan in order; a failed step stops the rest rather
            // than blindly applying later steps on a broken base.
            if (!result.Success)
            {
                break;
            }
        }

        var success = results.Count > 0 && results.All(r => r.Success);
        var failure = results.LastOrDefault(r => !r.Success);
        var response = new AssistantPlanApproveResponse(
            success,
            success ? null : failure?.ErrorCode,
            success ? null : failure?.ErrorMessage,
            results);

        // The plan was already consumed before execution. Keep only this
        // short-lived terminal result so an ambiguous HTTP response can be
        // reported truthfully without executing destructive work twice.
        plans.RecordTerminal(plan, response);
        return response;
    }


    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static JsonElement ParseArguments(string? json) =>
        JsonSerializer.SerializeToElement(ParseObject(json), JsonOptions);
}

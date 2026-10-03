using System.Security.Cryptography;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>One ordered step of a pending plan, exactly as it will be executed.</summary>
public sealed record AssistantPlanStep(string Capability, string Summary, string ArgumentsJson);

/// <summary>
/// A server-held pending plan. The approval token is bound to this plan id and
/// to nothing else.
/// </summary>
public sealed record StoredAssistantPlan(
    string PlanId,
    string ApprovalToken,
    string ConversationKey,
    string IdempotencyKey,
    string Summary,
    IReadOnlyList<AssistantPlanStep> Steps);

/// <summary>
/// Outcome of an approval attempt. A completed terminal result is returned only
/// when the exact already-consumed plan id + token are replayed.
/// </summary>
public sealed record AssistantPlanApprovalResult(
    bool Approved,
    StoredAssistantPlan? Plan,
    string? ErrorCode,
    string? ErrorMessage,
    AssistantPlanApproveResponse? TerminalResponse = null);

/// <summary>
/// Pending destructive plans plus a small terminal-result replay window.
///
/// Approval remains consume-before-execution. A successfully consumed plan is
/// never put back into the pending store. After execution, the exact plan id +
/// token can replay its already-computed terminal response for a bounded period;
/// it can never execute the destructive capability again.
///
/// Both pending state and terminal receipts are in-memory, short-lived and
/// capped. A restart or expiry therefore fails closed and requires the user to
/// initiate the destructive request again.
/// </summary>
public sealed class AssistantPlanStore
{
    internal const int MaxPendingPlans = 256;
    internal const int MaxTerminalResults = 256;
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan TerminalLifetime = TimeSpan.FromMinutes(20);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    private readonly Dictionary<string, PendingEnvelope> _byConversation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingEnvelope> _byPlanId =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalEnvelope> _terminalByPlanId =
        new(StringComparer.Ordinal);

    public AssistantPlanStore(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Stores a newly proposed plan and supersedes the conversation's previous
    /// pending plan, if any. The previous plan id immediately stops being
    /// approvable.
    /// </summary>
    public StoredAssistantPlan Create(
        string conversationKey,
        string idempotencyKey,
        string summary,
        IReadOnlyList<AssistantPlanStep> steps)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (_byConversation.TryGetValue(conversationKey, out var previous))
            {
                _byConversation.Remove(conversationKey);
                _byPlanId.Remove(previous.Plan.PlanId);
            }

            EnsurePendingCapacity();

            var plan = new StoredAssistantPlan(
                PlanId: Guid.NewGuid().ToString("N"),
                ApprovalToken: Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ConversationKey: conversationKey,
                IdempotencyKey: idempotencyKey,
                Summary: summary,
                Steps: steps);
            var envelope = new PendingEnvelope(plan, Now + PendingLifetime);

            _byConversation[conversationKey] = envelope;
            _byPlanId[plan.PlanId] = envelope;
            return plan;
        }
    }

    /// <summary>
    /// Validates and consumes an approval. On success the pending plan is
    /// removed before execution. On an exact replay after execution, the
    /// terminal response is returned without making the plan approvable again.
    /// </summary>
    public AssistantPlanApprovalResult Approve(string? planId, string? approvalToken)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (string.IsNullOrWhiteSpace(planId))
            {
                return Refuse(
                    AssistantErrorCodes.ApprovalRequired,
                    "A plan id is required to approve a plan.");
            }

            if (string.IsNullOrWhiteSpace(approvalToken))
            {
                return Refuse(
                    AssistantErrorCodes.ApprovalRequired,
                    $"Approving plan '{planId}' requires its approval token.");
            }

            if (_terminalByPlanId.TryGetValue(planId, out var terminal))
            {
                if (!FixedTimeEquals(terminal.ApprovalToken, approvalToken))
                {
                    return Refuse(
                        AssistantErrorCodes.ApprovalPlanMismatch,
                        $"The approval token does not match plan '{planId}'.");
                }

                return new AssistantPlanApprovalResult(
                    false,
                    null,
                    null,
                    null,
                    terminal.Response);
            }

            if (!_byPlanId.TryGetValue(planId, out var envelope))
            {
                return Refuse(
                    AssistantErrorCodes.NotFound,
                    $"No pending plan with id '{planId}' exists. It may have expired or been superseded.");
            }

            var plan = envelope.Plan;

            // The plan must still be the conversation's single pending plan. A
            // superseded plan is deliberately not approvable even with its own
            // (now stale) token.
            if (!_byConversation.TryGetValue(plan.ConversationKey, out var current)
                || !ReferenceEquals(current, envelope))
            {
                return Refuse(
                    AssistantErrorCodes.ApprovalPlanMismatch,
                    $"Plan '{planId}' is no longer the pending plan for this conversation.");
            }

            if (!FixedTimeEquals(plan.ApprovalToken, approvalToken))
            {
                return Refuse(
                    AssistantErrorCodes.ApprovalPlanMismatch,
                    $"The approval token does not match plan '{planId}'.");
            }

            // Consume BEFORE returning it to the executor. The terminal record is
            // written only after execution has produced its result.
            _byConversation.Remove(plan.ConversationKey);
            _byPlanId.Remove(plan.PlanId);
            return new AssistantPlanApprovalResult(true, plan, null, null);
        }
    }

    public void RecordTerminal(
        StoredAssistantPlan plan,
        AssistantPlanApproveResponse response)
    {
        lock (_gate)
        {
            CleanupExpired();
            EnsureTerminalCapacity();
            _terminalByPlanId[plan.PlanId] = new TerminalEnvelope(
                plan.ApprovalToken,
                response,
                Now + TerminalLifetime);
        }
    }

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private void CleanupExpired()
    {
        var now = Now;

        foreach (var envelope in _byPlanId.Values
                     .Where(item => item.ExpiresAt <= now)
                     .ToList())
        {
            _byPlanId.Remove(envelope.Plan.PlanId);
            if (_byConversation.TryGetValue(envelope.Plan.ConversationKey, out var current)
                && ReferenceEquals(current, envelope))
            {
                _byConversation.Remove(envelope.Plan.ConversationKey);
            }
        }

        foreach (var pair in _terminalByPlanId
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .ToList())
        {
            _terminalByPlanId.Remove(pair.Key);
        }
    }

    private void EnsurePendingCapacity()
    {
        while (_byPlanId.Count >= MaxPendingPlans)
        {
            var oldest = _byPlanId.Values.OrderBy(item => item.ExpiresAt).First();
            _byPlanId.Remove(oldest.Plan.PlanId);
            if (_byConversation.TryGetValue(oldest.Plan.ConversationKey, out var current)
                && ReferenceEquals(current, oldest))
            {
                _byConversation.Remove(oldest.Plan.ConversationKey);
            }
        }
    }

    private void EnsureTerminalCapacity()
    {
        while (_terminalByPlanId.Count >= MaxTerminalResults)
        {
            var oldest = _terminalByPlanId.OrderBy(pair => pair.Value.ExpiresAt).First();
            _terminalByPlanId.Remove(oldest.Key);
        }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        var actualBytes = System.Text.Encoding.UTF8.GetBytes(actual);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static AssistantPlanApprovalResult Refuse(string code, string message) =>
        new(false, null, code, message);

    private sealed record PendingEnvelope(
        StoredAssistantPlan Plan,
        DateTimeOffset ExpiresAt);

    private sealed record TerminalEnvelope(
        string ApprovalToken,
        AssistantPlanApproveResponse Response,
        DateTimeOffset ExpiresAt);
}

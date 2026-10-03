namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// How dangerous an assistant capability is, and therefore what it takes to run
/// it (issue #260, decision D7). Every registered capability declares exactly
/// one class; <see cref="AssistantCapabilityRegistry"/> is the only thing that
/// enforces them.
/// </summary>
public enum AssistantTrustClass
{
    /// <summary>
    /// Low risk; may execute immediately. Reserved for capture, which the user
    /// asked for by capturing and which is exactly-once when it carries a
    /// client/idempotency pair.
    /// </summary>
    Capture,

    /// <summary>
    /// Returns data/proposals only and is structurally incapable of mutating:
    /// it reads through a canonical service and never calls a write method.
    /// </summary>
    Suggest,

    /// <summary>
    /// Normal, low-risk application work that the user explicitly asked the
    /// assistant to perform. Executes immediately inside the ordinary tool
    /// loop so later tool calls can consume the real result. Domain services
    /// still own validation, idempotency and invariants.
    /// </summary>
    Act,

    /// <summary>
    /// Destructive or high-impact state changes that must carry an approval
    /// whose plan id matches the plan the call was built against. Without that
    /// match the registry refuses before the capability's delegate is reached.
    /// </summary>
    PlanAndAct,
}

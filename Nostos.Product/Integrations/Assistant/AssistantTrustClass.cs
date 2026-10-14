namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// How an assistant capability runs (issue #260, decision D7). Every registered
/// capability declares exactly one class. Risk is not judged per tool: library
/// changes are staged and the change policy judges what they would actually do.
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
    /// Library organisation: collections, memberships and book fields. A call
    /// never writes. It stages an operation into the turn's changeset, which
    /// is previewed through the canonical library code as it grows. When the
    /// turn ends, the change policy reads the resulting diff and either applies
    /// the whole changeset atomically, with undo, or holds it for review.
    /// </summary>
    Stage,
}

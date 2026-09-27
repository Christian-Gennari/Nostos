namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Product-domain metadata for assistant capabilities. This is deliberately
/// separate from <see cref="AssistantTrustClass"/>: category describes what a
/// capability is for, while trust class remains the authority/security boundary.
/// </summary>
public enum AssistantCapabilityCategory
{
    KnowledgeRetrieval,
    SourceNavigation,
    LibraryRead,
    Capture,
    Organization,
    OrdinaryAction,
}

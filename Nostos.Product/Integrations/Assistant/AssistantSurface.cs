namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Where a capability is offered (issue #1468). Ask Nostos is the in-app
/// assistant; <see cref="Mcp"/> is the curated set a connected chat app or
/// local agent sees over MCP (#1465). A capability can be on both. The surface
/// only decides what is listed and invocable there; the trust class still
/// decides how a call runs.
/// </summary>
[Flags]
public enum AssistantSurface
{
    None = 0,
    AskNostos = 1,
    Mcp = 2,
}

using System.Text.Json;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The only way an assistant capability runs. Reads, capture and ordinary
/// <see cref="AssistantTrustClass.Act"/> work reach their canonical service
/// immediately. <see cref="AssistantTrustClass.Stage"/> work only stages into
/// the turn's changeset, and is refused when the caller supplied none, so a
/// library change can never bypass the change policy.
/// </summary>
public sealed class AssistantCapabilityRegistry
{
    private readonly IReadOnlyList<AssistantCapability> _all;
    private readonly Dictionary<string, AssistantCapability> _byName;

    public AssistantCapabilityRegistry(IEnumerable<AssistantCapability> capabilities)
    {
        // Reject a duplicate name at construction rather than letting one
        // capability silently shadow another.
        _all = capabilities.ToList();
        _byName = _all.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Ordered capability definitions, for tool-definition generation. The
    /// order is the order they were built in.
    /// </summary>
    public IReadOnlyList<AssistantCapability> All => _all;

    /// <summary>
    /// The capabilities offered on one surface, in build order, each as that
    /// surface presents it (<see cref="AssistantCapability.For"/>). Ask Nostos
    /// builds its tool list from <c>For(AssistantSurface.AskNostos)</c>; the MCP
    /// server lists <c>For(AssistantSurface.Mcp)</c>.
    /// </summary>
    public IReadOnlyList<AssistantCapability> For(AssistantSurface surface) =>
        _all.Where(c => c.IsOn(surface)).Select(c => c.For(surface)).ToList();

    /// <summary>
    /// Invokes one capability by name for a caller on <paramref name="surface"/>.
    /// A capability that is not offered there is refused exactly like an
    /// unknown name, so a surface can never reach a tool it does not list.
    /// </summary>
    public Task<AssistantToolResult> InvokeAsync(
        AssistantSurface surface,
        string name,
        JsonElement argsJson,
        AssistantToolContext context,
        CancellationToken ct = default)
    {
        if (!_byName.TryGetValue(name, out var capability) || !capability.IsOn(surface))
        {
            return Task.FromResult(AssistantToolResult.Fail(
                AssistantErrorCodes.UnknownCapability,
                $"No assistant capability named '{name}' is offered here."));
        }

        return InvokeAsync(name, argsJson, context, ct);
    }

    /// <summary>
    /// Invokes one capability by name. Returns a typed failure for an unknown
    /// name or a Stage call without a changeset; never throws for those cases.
    /// </summary>
    public async Task<AssistantToolResult> InvokeAsync(
        string name,
        JsonElement argsJson,
        AssistantToolContext context,
        CancellationToken ct = default)
    {
        if (!_byName.TryGetValue(name, out var capability))
        {
            return AssistantToolResult.Fail(
                AssistantErrorCodes.UnknownCapability,
                $"No assistant capability named '{name}' is registered.");
        }

        if (capability.Trust == AssistantTrustClass.Stage && context.Changeset is null)
        {
            return AssistantToolResult.Fail(
                AssistantErrorCodes.ChangesetUnavailable,
                $"Capability '{capability.Name}' changes the library and can only run inside an assistant turn.");
        }

        return await capability.Execute(context, argsJson, ct);
    }
}

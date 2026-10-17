namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Whose library a request acts on, as far as process-wide Ask Nostos state is
/// concerned. The product never derives identity; a host supplies it. A
/// multi-tenant host returns the account, so one account's activity can never
/// expire another's pending review or undo.
/// </summary>
public interface IAssistantOwner
{
    /// <summary>A stable, non-empty key for the current request's owner.</summary>
    string Key { get; }
}

/// <summary>One library per process: every request has the same owner. SelfHosted.</summary>
public sealed class SingleAssistantOwner : IAssistantOwner
{
    public static SingleAssistantOwner Instance { get; } = new();

    public string Key => "library";
}

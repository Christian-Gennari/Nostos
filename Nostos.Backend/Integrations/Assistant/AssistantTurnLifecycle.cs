using System.Collections.Concurrent;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Canonical identity normalization shared by the ordinary and streamed turn
/// transports. It preserves the #560 compatibility aliases exactly.
/// </summary>
public static class AssistantTurnIdentity
{
    public static string ConversationKey(string? conversationId) =>
        string.IsNullOrWhiteSpace(conversationId) ? "anonymous" : conversationId.Trim();

    public static string TurnKey(AssistantTurnRequest request)
    {
        var turnId = string.IsNullOrWhiteSpace(request.TurnId)
            ? request.IdempotencyKey
            : request.TurnId;
        return string.IsNullOrWhiteSpace(turnId)
            ? Guid.NewGuid().ToString("N")
            : turnId.Trim();
    }
}

/// <summary>
/// Active-turn cancellation registry. The key is the canonical
/// (ConversationId, TurnId) pair, so a stale stop can never cancel a newer turn.
/// </summary>
public sealed class AssistantTurnExecutionRegistry
{
    private readonly ConcurrentDictionary<TurnKey, ActiveTurn> _active = new();

    public AssistantTurnExecution? TryBegin(
        string conversationId,
        string turnId,
        CancellationToken requestAborted)
    {
        var key = new TurnKey(conversationId, turnId);
        var active = new ActiveTurn(
            CancellationTokenSource.CreateLinkedTokenSource(requestAborted));

        if (!_active.TryAdd(key, active))
        {
            active.Cancellation.Dispose();
            return null;
        }

        return new AssistantTurnExecution(
            active.Cancellation.Token,
            () => Complete(key, active));
    }

    public bool TryCancel(string conversationId, string turnId)
    {
        var key = new TurnKey(conversationId, turnId);
        if (!_active.TryGetValue(key, out var active))
            return false;

        active.Cancellation.Cancel();
        return true;
    }

    private void Complete(TurnKey key, ActiveTurn active)
    {
        if (_active.TryGetValue(key, out var current)
            && ReferenceEquals(current, active)
            && _active.TryRemove(key, out _))
        {
            active.Cancellation.Dispose();
        }
    }

    private readonly record struct TurnKey(string ConversationId, string TurnId);
    private sealed record ActiveTurn(CancellationTokenSource Cancellation);
}

public sealed class AssistantTurnExecution(
    CancellationToken token,
    Action complete) : IDisposable
{
    private int _disposed;
    public CancellationToken Token { get; } = token;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            complete();
    }
}

/// <summary>
/// Fixed product language for meaningful tool work. Mapping depends only on
/// the registered capability identity/category/trust class; it never consumes
/// tool arguments, tool results, model reasoning, or customer content.
/// </summary>
public static class AssistantTurnActivities
{
    public static AssistantTurnActivityDto? ForCapability(AssistantCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (string.Equals(capability.Name, "knowledge_search", StringComparison.Ordinal))
            return new("searching_material", "Searching your notes and books…");

        if (string.Equals(capability.Name, "book_text_search", StringComparison.Ordinal))
            return new("searching_books", "Searching your books…");

        if (string.Equals(capability.Name, "knowledge_read_evidence", StringComparison.Ordinal))
            return new("opening_evidence", "Opening the matching passage…");

        if (capability.Trust == AssistantTrustClass.Capture)
            return new("saving_note", "Saving your note…");

        if (capability.Trust == AssistantTrustClass.Act)
        {
            if (capability.Name.Contains("collection", StringComparison.Ordinal))
                return new("updating_collection", "Updating the collection…");
            if (capability.Name.Contains("link", StringComparison.Ordinal))
                return new("linking_note", "Linking the note…");
            return new("applying_action", "Applying the requested change…");
        }

        if (capability.Category == AssistantCapabilityCategory.LibraryRead)
            return new("checking_library", "Checking your library…");

        if (capability.Category == AssistantCapabilityCategory.KnowledgeRetrieval)
            return new("searching_notes", "Searching your notes…");

        if (capability.Category == AssistantCapabilityCategory.SourceNavigation)
            return new("opening_source", "Opening the matching source…");

        return null;
    }
}

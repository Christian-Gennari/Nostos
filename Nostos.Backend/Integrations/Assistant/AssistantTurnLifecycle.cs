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
///
/// The active state also owns the mutation boundary. Cancellation and
/// "mutation has begun" are serialized through the same gate so exactly one
/// can win: if cancellation wins, the mutation lease is refused; if the
/// mutation lease wins, that canonical write is allowed to reach its truthful
/// terminal state before the turn stops.
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
            active.Complete();
            return null;
        }

        return new AssistantTurnExecution(
            active.Token,
            active.TryBeginMutation,
            () => Complete(key, active));
    }

    public bool TryCancel(string conversationId, string turnId)
    {
        var key = new TurnKey(conversationId, turnId);
        if (!_active.TryGetValue(key, out var active))
            return false;

        return active.TryCancel();
    }

    private void Complete(TurnKey key, ActiveTurn active)
    {
        // Mark the lifecycle terminal before removing the dictionary entry. A
        // concurrent Stop that already obtained the ActiveTurn can then observe
        // completion and return not_active without touching a disposed CTS.
        active.Complete();

        if (_active.TryGetValue(key, out var current)
            && ReferenceEquals(current, active))
        {
            _active.TryRemove(key, out _);
        }
    }

    private readonly record struct TurnKey(string ConversationId, string TurnId);

    private sealed class ActiveTurn
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation;
        private bool _completed;
        private bool _cancelInFlight;
        private bool _cancellationRequested;
        private bool _mutationInProgress;
        private bool _disposed;

        public ActiveTurn(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            Token = cancellation.Token;
        }

        public CancellationToken Token { get; }

        public bool TryCancel()
        {
            CancellationTokenSource cancellation;

            lock (_gate)
            {
                if (_completed)
                    return false;

                // Idempotent Stop requests remain accepted while this turn is
                // active, but only the first request needs to signal the token.
                if (_cancellationRequested)
                    return true;

                _cancellationRequested = true;
                _cancelInFlight = true;
                cancellation = _cancellation;
            }

            try
            {
                cancellation.Cancel();
            }
            finally
            {
                var dispose = false;
                lock (_gate)
                {
                    _cancelInFlight = false;
                    dispose = TryMarkDisposedLocked();
                }

                if (dispose)
                    cancellation.Dispose();
            }

            return true;
        }

        public AssistantTurnMutationLease? TryBeginMutation()
        {
            lock (_gate)
            {
                if (_completed || _cancellationRequested || _mutationInProgress)
                    return null;

                _mutationInProgress = true;
                return new AssistantTurnMutationLease(EndMutation);
            }
        }

        public void Complete()
        {
            var dispose = false;

            lock (_gate)
            {
                if (_completed)
                    return;

                _completed = true;
                dispose = TryMarkDisposedLocked();
            }

            if (dispose)
                _cancellation.Dispose();
        }

        private void EndMutation()
        {
            lock (_gate)
            {
                _mutationInProgress = false;
            }
        }

        private bool TryMarkDisposedLocked()
        {
            if (_disposed || !_completed || _cancelInFlight)
                return false;

            _disposed = true;
            return true;
        }
    }
}

public sealed class AssistantTurnExecution(
    CancellationToken token,
    Func<AssistantTurnMutationLease?> tryBeginMutation,
    Action complete) : IDisposable
{
    private int _disposed;
    public CancellationToken Token { get; } = token;

    /// <summary>
    /// Atomically claims the canonical write boundary against Stop. A null
    /// result means cancellation or completion won before the write began.
    /// </summary>
    public AssistantTurnMutationLease? TryBeginMutation() =>
        Volatile.Read(ref _disposed) == 0 ? tryBeginMutation() : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            complete();
    }
}

public sealed class AssistantTurnMutationLease(Action complete) : IDisposable
{
    private int _disposed;

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

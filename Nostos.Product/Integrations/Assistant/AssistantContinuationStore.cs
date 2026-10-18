using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Minimum server-authoritative state for an interrupted deterministic capture.
///
/// This is deliberately not provider/model state. It contains only the original
/// capture arguments, the application context that made the capture
/// deterministic, and the stored capture-processing mode. One conversation has
/// at most one current continuation. Active continuations and replay receipts
/// are both short-lived and bounded.
/// </summary>
internal sealed record StoredAssistantContinuation(
    string ContinuationId,
    string OwnerKey,
    string ConversationId,
    string OriginalTurnId,
    string Kind,
    string ArgumentsJson,
    string OriginalMessage,
    AssistantContextDto Context,
    DateTimeOffset ExpiresAt,
    int BookResolutionAttempts = 0);

internal enum AssistantContinuationLookupStatus
{
    Found,
    NotFound,
    ConversationMismatch,
}

internal sealed record AssistantContinuationLookup(
    AssistantContinuationLookupStatus Status,
    StoredAssistantContinuation? Continuation);

/// <summary>
/// Process-local store of interrupted captures and their replay receipts.
///
/// Every continuation belongs to an owner (<see cref="IAssistantOwner"/>), and
/// conversations are matched within it. Each owner has its own capacity in
/// both pools, so an owner that adds past it only expires its own entries.
/// When a pool as a whole is full, room comes from whichever owner holds the
/// most, so one account cannot push out another that holds fewer.
/// </summary>
public sealed class AssistantContinuationStore
{
    internal const int MaxActiveContinuations = 256;
    internal const int MaxReplayReceipts = 256;
    internal const int OwnerActiveContinuations = 64;
    internal const int OwnerReplayReceipts = 64;
    internal static readonly TimeSpan ActiveLifetime = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan ReceiptLifetime = TimeSpan.FromMinutes(20);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    private readonly Dictionary<string, StoredAssistantContinuation> _activeById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string OwnerKey, string ConversationId), string> _activeIdByConversation =
        new();
    private readonly Dictionary<ReceiptKey, ContinuationReceipt> _receipts = new();

    public AssistantContinuationStore(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    internal StoredAssistantContinuation Create(
        string ownerKey,
        string conversationId,
        string originalTurnId,
        string kind,
        string argumentsJson,
        string originalMessage,
        AssistantContextDto context)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (_activeIdByConversation.TryGetValue((ownerKey, conversationId), out var previousId))
            {
                _activeById.Remove(previousId);
            }

            EnsureActiveCapacity(ownerKey);

            var continuation = new StoredAssistantContinuation(
                Guid.NewGuid().ToString("N"),
                ownerKey,
                conversationId,
                originalTurnId,
                kind,
                argumentsJson,
                originalMessage,
                context,
                Now + ActiveLifetime);

            _activeById[continuation.ContinuationId] = continuation;
            _activeIdByConversation[(ownerKey, conversationId)] = continuation.ContinuationId;
            return continuation;
        }
    }

    internal AssistantContinuationLookup Find(
        string ownerKey,
        string continuationId,
        string conversationId)
    {
        lock (_gate)
        {
            CleanupExpired();

            // Another owner's continuation reads as missing rather than as a
            // mismatch, so a lookup reveals nothing about other accounts.
            if (!_activeById.TryGetValue(continuationId, out var continuation)
                || !string.Equals(continuation.OwnerKey, ownerKey, StringComparison.Ordinal))
            {
                return new AssistantContinuationLookup(
                    AssistantContinuationLookupStatus.NotFound,
                    null);
            }

            if (!string.Equals(
                    continuation.ConversationId,
                    conversationId,
                    StringComparison.Ordinal))
            {
                return new AssistantContinuationLookup(
                    AssistantContinuationLookupStatus.ConversationMismatch,
                    null);
            }

            return new AssistantContinuationLookup(
                AssistantContinuationLookupStatus.Found,
                continuation);
        }
    }

    internal StoredAssistantContinuation Update(
        StoredAssistantContinuation continuation,
        string kind,
        AssistantContextDto context,
        int? bookResolutionAttempts = null)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (!_activeById.TryGetValue(continuation.ContinuationId, out var current)
                || !ReferenceEquals(current, continuation))
            {
                return continuation;
            }

            var updated = continuation with
            {
                Kind = kind,
                Context = context,
                BookResolutionAttempts =
                    bookResolutionAttempts ?? continuation.BookResolutionAttempts,
                ExpiresAt = Now + ActiveLifetime,
            };

            _activeById[updated.ContinuationId] = updated;
            return updated;
        }
    }

    internal bool TryGetReceipt(
        string ownerKey,
        string continuationId,
        string conversationId,
        string turnId,
        out AssistantTurnResponse response)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (_receipts.TryGetValue(
                    new ReceiptKey(ownerKey, continuationId, conversationId, turnId),
                    out var receipt))
            {
                response = receipt.Response;
                return true;
            }

            response = default!;
            return false;
        }
    }

    /// <summary>
    /// Records the result of one continuation answer while keeping the
    /// continuation active (for example, an unresolved book title that asks
    /// again, or a book answer that advances to a page prompt).
    /// </summary>
    internal void RecordReceipt(
        string ownerKey,
        string continuationId,
        string conversationId,
        string turnId,
        AssistantTurnResponse response)
    {
        lock (_gate)
        {
            CleanupExpired();
            AddReceipt(new ReceiptKey(ownerKey, continuationId, conversationId, turnId), response);
        }
    }

    /// <summary>
    /// Completes a continuation and keeps only the short-lived terminal replay
    /// receipt. A retry of the same logical TurnId is therefore truthful and
    /// cannot execute the capture a second time.
    /// </summary>
    internal void Complete(
        StoredAssistantContinuation continuation,
        string turnId,
        AssistantTurnResponse response)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (_activeById.TryGetValue(continuation.ContinuationId, out var current)
                && ReferenceEquals(current, continuation))
            {
                RemoveActive(continuation);
            }

            AddReceipt(
                new ReceiptKey(
                    continuation.OwnerKey,
                    continuation.ContinuationId,
                    continuation.ConversationId,
                    turnId),
                response);
        }
    }

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private void CleanupExpired()
    {
        var now = Now;

        foreach (var continuation in _activeById.Values
                     .Where(item => item.ExpiresAt <= now)
                     .ToList())
        {
            RemoveActive(continuation);
        }

        foreach (var pair in _receipts.Where(pair => pair.Value.ExpiresAt <= now).ToList())
        {
            _receipts.Remove(pair.Key);
        }
    }

    private void RemoveActive(StoredAssistantContinuation continuation)
    {
        _activeById.Remove(continuation.ContinuationId);
        var conversation = (continuation.OwnerKey, continuation.ConversationId);
        if (_activeIdByConversation.TryGetValue(conversation, out var currentId)
            && string.Equals(currentId, continuation.ContinuationId, StringComparison.Ordinal))
        {
            _activeIdByConversation.Remove(conversation);
        }
    }

    /// <summary>Leaves room for one more active continuation from <paramref name="ownerKey"/>.</summary>
    private void EnsureActiveCapacity(string ownerKey)
    {
        while (_activeById.Values.Count(item => item.OwnerKey == ownerKey) >= OwnerActiveContinuations)
        {
            RemoveActive(OldestActive(ownerKey));
        }

        while (_activeById.Count >= MaxActiveContinuations)
        {
            RemoveActive(OldestActive(Largest(_activeById.Values.Select(item => item.OwnerKey), ownerKey)));
        }
    }

    private StoredAssistantContinuation OldestActive(string ownerKey) =>
        _activeById.Values
            .Where(item => item.OwnerKey == ownerKey)
            .MinBy(item => item.ExpiresAt)!;

    /// <summary>Stores a receipt, making room within its owner first when it is new.</summary>
    private void AddReceipt(ReceiptKey key, AssistantTurnResponse response)
    {
        if (!_receipts.ContainsKey(key))
        {
            while (_receipts.Keys.Count(item => item.OwnerKey == key.OwnerKey) >= OwnerReplayReceipts)
            {
                _receipts.Remove(OldestReceipt(key.OwnerKey));
            }

            while (_receipts.Count >= MaxReplayReceipts)
            {
                _receipts.Remove(OldestReceipt(Largest(_receipts.Keys.Select(item => item.OwnerKey), key.OwnerKey)));
            }
        }

        _receipts[key] = new ContinuationReceipt(response, Now + ReceiptLifetime);
    }

    private ReceiptKey OldestReceipt(string ownerKey) =>
        _receipts
            .Where(pair => pair.Key.OwnerKey == ownerKey)
            .MinBy(pair => pair.Value.ExpiresAt)
            .Key;

    /// <summary>
    /// The owner holding the most entries. On a tie the adding owner goes
    /// first, so adding never costs an equal peer.
    /// </summary>
    private static string Largest(IEnumerable<string> owners, string addingOwner) =>
        owners
            .GroupBy(owner => owner, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key == addingOwner)
            .First()
            .Key;

    private readonly record struct ReceiptKey(
        string OwnerKey,
        string ContinuationId,
        string ConversationId,
        string TurnId);

    private sealed record ContinuationReceipt(
        AssistantTurnResponse Response,
        DateTimeOffset ExpiresAt);
}

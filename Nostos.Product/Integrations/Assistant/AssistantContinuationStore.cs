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
    string ConversationId,
    string OriginalTurnId,
    string Kind,
    string ArgumentsJson,
    string OriginalMessage,
    AssistantContextDto Context,
    string ProcessingMode,
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

public sealed class AssistantContinuationStore
{
    internal const int MaxActiveContinuations = 256;
    internal const int MaxReplayReceipts = 256;
    internal static readonly TimeSpan ActiveLifetime = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan ReceiptLifetime = TimeSpan.FromMinutes(20);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    private readonly Dictionary<string, StoredAssistantContinuation> _activeById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeIdByConversation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string ContinuationId, string ConversationId, string TurnId), ContinuationReceipt>
        _receipts = new();

    public AssistantContinuationStore(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    internal StoredAssistantContinuation Create(
        string conversationId,
        string originalTurnId,
        string kind,
        string argumentsJson,
        string originalMessage,
        AssistantContextDto context,
        string processingMode)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (_activeIdByConversation.TryGetValue(conversationId, out var previousId))
            {
                _activeById.Remove(previousId);
            }

            EnsureActiveCapacity();

            var continuation = new StoredAssistantContinuation(
                Guid.NewGuid().ToString("N"),
                conversationId,
                originalTurnId,
                kind,
                argumentsJson,
                originalMessage,
                context,
                processingMode,
                Now + ActiveLifetime);

            _activeById[continuation.ContinuationId] = continuation;
            _activeIdByConversation[conversationId] = continuation.ContinuationId;
            return continuation;
        }
    }

    internal AssistantContinuationLookup Find(string continuationId, string conversationId)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (!_activeById.TryGetValue(continuationId, out var continuation))
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
        string continuationId,
        string conversationId,
        string turnId,
        out AssistantTurnResponse response)
    {
        lock (_gate)
        {
            CleanupExpired();

            if (_receipts.TryGetValue((continuationId, conversationId, turnId), out var receipt))
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
        string continuationId,
        string conversationId,
        string turnId,
        AssistantTurnResponse response)
    {
        lock (_gate)
        {
            CleanupExpired();
            EnsureReceiptCapacity();
            _receipts[(continuationId, conversationId, turnId)] =
                new ContinuationReceipt(response, Now + ReceiptLifetime);
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
                _activeById.Remove(continuation.ContinuationId);
                if (_activeIdByConversation.TryGetValue(continuation.ConversationId, out var currentId)
                    && string.Equals(currentId, continuation.ContinuationId, StringComparison.Ordinal))
                {
                    _activeIdByConversation.Remove(continuation.ConversationId);
                }
            }

            EnsureReceiptCapacity();
            _receipts[(continuation.ContinuationId, continuation.ConversationId, turnId)] =
                new ContinuationReceipt(response, Now + ReceiptLifetime);
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
            _activeById.Remove(continuation.ContinuationId);
            if (_activeIdByConversation.TryGetValue(continuation.ConversationId, out var currentId)
                && string.Equals(currentId, continuation.ContinuationId, StringComparison.Ordinal))
            {
                _activeIdByConversation.Remove(continuation.ConversationId);
            }
        }

        foreach (var pair in _receipts.Where(pair => pair.Value.ExpiresAt <= now).ToList())
        {
            _receipts.Remove(pair.Key);
        }
    }

    private void EnsureActiveCapacity()
    {
        while (_activeById.Count >= MaxActiveContinuations)
        {
            var oldest = _activeById.Values.OrderBy(item => item.ExpiresAt).First();
            _activeById.Remove(oldest.ContinuationId);
            if (_activeIdByConversation.TryGetValue(oldest.ConversationId, out var currentId)
                && string.Equals(currentId, oldest.ContinuationId, StringComparison.Ordinal))
            {
                _activeIdByConversation.Remove(oldest.ConversationId);
            }
        }
    }

    private void EnsureReceiptCapacity()
    {
        while (_receipts.Count >= MaxReplayReceipts)
        {
            var oldest = _receipts.OrderBy(pair => pair.Value.ExpiresAt).First();
            _receipts.Remove(oldest.Key);
        }
    }

    private sealed record ContinuationReceipt(
        AssistantTurnResponse Response,
        DateTimeOffset ExpiresAt);
}

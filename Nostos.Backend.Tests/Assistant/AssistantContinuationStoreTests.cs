using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The continuation store is process-wide, and a multi-tenant host shares it
/// across accounts (#1035). These pin that conversations match per owner and
/// that one owner's activity never expires another owner's continuation.
/// </summary>
public sealed class AssistantContinuationStoreTests
{
    private static readonly AssistantContextDto Context = new("second-brain", "/second-brain");
    private static readonly AssistantTurnResponse Response = new("done", null, null, [], null);

    [Fact]
    public void The_same_conversation_key_holds_one_continuation_per_owner()
    {
        var store = new AssistantContinuationStore(new TickingClock());
        var mine = Create(store, "a", "anonymous");
        var theirs = Create(store, "b", "anonymous");

        Lookup(store, "a", mine).Status.Should().Be(AssistantContinuationLookupStatus.Found);
        Lookup(store, "b", theirs).Status.Should().Be(AssistantContinuationLookupStatus.Found);
    }

    [Fact]
    public void Another_owners_continuation_reads_as_missing()
    {
        var store = new AssistantContinuationStore(new TickingClock());
        var theirs = Create(store, "b", "c");

        store.Find("a", theirs.ContinuationId, "c").Status
            .Should().Be(AssistantContinuationLookupStatus.NotFound);
        store.Find("a", theirs.ContinuationId, "other").Status
            .Should().Be(AssistantContinuationLookupStatus.NotFound);
    }

    [Fact]
    public void A_receipt_replays_only_for_its_owner()
    {
        var store = new AssistantContinuationStore(new TickingClock());
        var continuation = Create(store, "a", "c");
        store.Complete(continuation, "turn-2", Response);

        store.TryGetReceipt("a", continuation.ContinuationId, "c", "turn-2", out _).Should().BeTrue();
        store.TryGetReceipt("b", continuation.ContinuationId, "c", "turn-2", out _).Should().BeFalse();
    }

    [Fact]
    public void An_owner_past_its_capacity_expires_only_its_own_oldest()
    {
        var store = new AssistantContinuationStore(new TickingClock());
        var other = Create(store, "b", "c-b");
        var first = Create(store, "a", "c-a");
        for (var i = 1; i < AssistantContinuationStore.OwnerActiveContinuations; i++)
            Create(store, "a", $"c-a{i}");

        Create(store, "a", "c-a-last");

        Lookup(store, "a", first).Status.Should().Be(AssistantContinuationLookupStatus.NotFound);
        Lookup(store, "b", other).Status.Should().Be(AssistantContinuationLookupStatus.Found);
    }

    [Fact]
    public void A_full_pool_takes_room_from_the_owner_holding_the_most()
    {
        var store = new AssistantContinuationStore(new TickingClock());
        var small = Create(store, "small", "c");
        var firstOfLargest = Create(store, "large", "c-large-0");
        for (var i = 1; i < AssistantContinuationStore.OwnerActiveContinuations; i++)
            Create(store, "large", $"c-large-{i}");
        // Everyone else holds fewer than "large", so the order is unambiguous.
        var perFiller = AssistantContinuationStore.OwnerActiveContinuations - 1;
        var fillers = AssistantContinuationStore.MaxActiveContinuations
            - 1 - AssistantContinuationStore.OwnerActiveContinuations;
        for (var i = 0; i < fillers; i++)
            Create(store, $"filler{i / perFiller}", $"c-f{i}");

        // The pool is now full; "small" holds one, "large" holds the most.
        Create(store, "small", "c-2");

        Lookup(store, "small", small).Status.Should().Be(AssistantContinuationLookupStatus.Found);
        Lookup(store, "large", firstOfLargest).Status.Should().Be(AssistantContinuationLookupStatus.NotFound);
    }

    [Fact]
    public void An_owner_past_its_receipt_capacity_expires_only_its_own_oldest()
    {
        var store = new AssistantContinuationStore(new TickingClock());
        store.RecordReceipt("b", "k-b", "c", "t", Response);
        store.RecordReceipt("a", "k-a0", "c", "t", Response);
        for (var i = 1; i <= AssistantContinuationStore.OwnerReplayReceipts; i++)
            store.RecordReceipt("a", $"k-a{i}", "c", "t", Response);

        store.TryGetReceipt("a", "k-a0", "c", "t", out _).Should().BeFalse();
        store.TryGetReceipt("b", "k-b", "c", "t", out _).Should().BeTrue();
    }

    private static StoredAssistantContinuation Create(
        AssistantContinuationStore store,
        string owner,
        string conversation) =>
        store.Create(owner, conversation, "turn-1", "book", "{}", "save this", Context);

    private static AssistantContinuationLookup Lookup(
        AssistantContinuationStore store,
        string owner,
        StoredAssistantContinuation continuation) =>
        store.Find(owner, continuation.ContinuationId, continuation.ConversationId);

    /// <summary>Each reading is a millisecond later, so "oldest" is never a tie.</summary>
    private sealed class TickingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddMilliseconds(1);
    }
}

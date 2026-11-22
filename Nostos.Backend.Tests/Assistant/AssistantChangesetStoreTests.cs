using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Library;
using Nostos.Product.Composition;
using Nostos.Shared.Dtos;
using Xunit;
using Nostos.Backend.Tests.Support;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The changeset store is process-wide, and a multi-tenant host shares it
/// across accounts (#971). These pin that one owner's activity never expires
/// another owner's review or undo, and that conversations match per owner.
/// </summary>
public sealed class AssistantChangesetStoreTests
{
    private static readonly LibraryChangesetDiffDto EmptyDiff =
        new([], [], [], new LibraryChangesetCountsDto(0, 0, 0, 0, 0, 0, 0, 0), [], []);

    private readonly TestTimeProvider _clock = new();

    [Fact]
    public void A_review_step_keeps_its_topic_kind_after_the_reader_leaves_it_out()
    {
        // A preview recomputes the diff without the excluded step, so the card
        // can only keep a left-out topic in place if the step itself says what
        // it changes (issue #1770 review).
        var store = new AssistantChangesetStore(_clock);
        var kindId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var diff = new LibraryChangesetDiffDto(
            [], [], [],
            new LibraryChangesetCountsDto(0, 0, 0, 0, 0, 0, 0, 0, 1, 1),
            [new LibraryTopicKindChangeDto(kindId, "created", "School of thought", null, "kind-7", 1, StepIndex: 0)],
            [new LibraryTopicKindAssignmentDto(
                topicId, "Freire", kindId, "School of thought", null, "assistant", StepIndex: 1, KindColorKey: "kind-7")]);

        var stored = store.Add(
            "owner", "conversation", "turn",
            [
                new AssistantStagedStep(
                    new CreateTopicKindOp("school", "School of thought", "kind-7"),
                    "Create the topic kind “School of thought”", []),
                new AssistantStagedStep(
                    new SetTopicKindOp([topicId], TopicKindRef.Existing(kindId)),
                    "Set “Freire” to “School of thought”", [0]),
            ],
            diff, "hash", AssistantChangesetStatus.Review, [], undo: null);

        var create = stored.ToDto().Steps[0].TopicKind;
        create.Should().NotBeNull();
        create!.CreatesKind.Should().BeTrue();
        create.KindName.Should().Be("School of thought");
        create.ColorKey.Should().Be("kind-7");

        store.Update(stored, changeset =>
        {
            changeset.Excluded.Add(1);
            changeset.Diff = EmptyDiff;
        });

        var dto = stored.ToDto();
        dto.Steps[1].Included.Should().BeFalse();
        var topic = dto.Steps[1].TopicKind;
        topic.Should().NotBeNull("the step still describes the topic the reader left out");
        topic!.TopicId.Should().Be(topicId);
        topic.TopicName.Should().Be("Freire");
        topic.KindKey.Should().Be(kindId.ToString());
        topic.KindName.Should().Be("School of thought");
        topic.ColorKey.Should().Be("kind-7");
        topic.IsNewKind.Should().BeTrue();
        topic.CreatesKind.Should().BeFalse();
    }

    [Fact]
    public void An_owner_past_its_capacity_expires_only_its_own_oldest()
    {
        var store = new AssistantChangesetStore(_clock);
        var other = Applied(store, "b", "c-b");
        var first = Applied(store, "a", "c-a");
        for (var i = 1; i < AssistantChangesetStore.OwnerCapacity; i++)
            Applied(store, "a", $"c-a{i}");

        Applied(store, "a", "c-a-last");

        Exists(store, first).Should().BeFalse();
        Exists(store, other).Should().BeTrue();
    }

    [Fact]
    public void A_full_process_takes_room_from_the_owner_holding_the_most()
    {
        var store = new AssistantChangesetStore(_clock);
        var small = Applied(store, "small", "c");
        var firstOfLargest = Applied(store, "large", "c-large-0");
        for (var i = 1; i < AssistantChangesetStore.OwnerCapacity; i++)
            Applied(store, "large", $"c-large-{i}");
        // Everyone else holds fewer than "large", so the order is unambiguous.
        var filler = 0;
        while (filler < AssistantChangesetStore.Capacity - 1 - AssistantChangesetStore.OwnerCapacity)
        {
            Applied(store, $"filler{filler / (AssistantChangesetStore.OwnerCapacity - 1)}", $"c-f{filler}");
            filler++;
        }

        // The store is now full; "small" holds one, "large" holds the most.
        Applied(store, "small", "c-2");

        Exists(store, small).Should().BeTrue();
        Exists(store, firstOfLargest).Should().BeFalse();
    }

    [Fact]
    public void A_newer_review_discards_the_older_only_within_the_same_owner()
    {
        var store = new AssistantChangesetStore(_clock);
        var mine = Review(store, "a", "anonymous");
        var theirs = Review(store, "b", "anonymous");

        mine.Status.Should().Be(AssistantChangesetStatus.Review);
        theirs.Status.Should().Be(AssistantChangesetStatus.Review);

        Review(store, "a", "anonymous");
        mine.Status.Should().Be(AssistantChangesetStatus.Discarded);
        theirs.Status.Should().Be(AssistantChangesetStatus.Review);
    }

    [Fact]
    public void A_turn_already_applied_is_found_only_for_its_owner()
    {
        var store = new AssistantChangesetStore(_clock);
        var applied = Applied(store, "a", "anonymous", turnId: "t1");

        store.FindAppliedForTurn("a", "anonymous", "t1").Should().BeSameAs(applied);
        store.FindAppliedForTurn("b", "anonymous", "t1").Should().BeNull();
    }

    [Fact]
    public void A_lookup_on_a_full_store_evicts_nothing()
    {
        var store = new AssistantChangesetStore(_clock);
        var all = new List<StoredAssistantChangeset>();
        for (var i = 0; i < AssistantChangesetStore.Capacity; i++)
            all.Add(Applied(store, $"o{i % 10}", $"c{i}"));

        foreach (var changeset in all)
            Exists(store, changeset).Should().BeTrue();
    }

    [Fact]
    public void Changesets_still_expire()
    {
        var store = new AssistantChangesetStore(_clock);
        var review = Review(store, "a", "c");
        var applied = Applied(store, "a", "c2");

        _clock.Advance(AssistantChangesetStore.ReviewLifetime);
        Exists(store, review).Should().BeFalse();
        Exists(store, applied).Should().BeTrue();

        _clock.Advance(AssistantChangesetStore.UndoLifetime);
        Exists(store, applied).Should().BeFalse();
    }

    [Fact]
    public void The_product_registers_a_single_owner_unless_the_host_supplies_one()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAssistantOwner>()
            .Should().BeSameAs(SingleAssistantOwner.Instance);
    }

    private StoredAssistantChangeset Applied(
        AssistantChangesetStore store, string owner, string conversation, string? turnId = null)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        return store.Add(owner, conversation, turnId ?? Guid.NewGuid().ToString("N"), [], EmptyDiff, "hash",
            AssistantChangesetStatus.Applied, [], undo: null);
    }

    private StoredAssistantChangeset Review(AssistantChangesetStore store, string owner, string conversation)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        return store.Add(owner, conversation, Guid.NewGuid().ToString("N"), [], EmptyDiff, "hash",
            AssistantChangesetStatus.Review, [], undo: null);
    }

    private static bool Exists(AssistantChangesetStore store, StoredAssistantChangeset changeset) =>
        store.Find(changeset.ChangesetId, changeset.ActionToken, out _) == AssistantChangesetStore.Lookup.Found;

}

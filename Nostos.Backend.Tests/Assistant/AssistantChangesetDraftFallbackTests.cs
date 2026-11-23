using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The last-resort staging path (issue #1770 review, round 3): when the
/// canonical plan cannot be previewed, the draft falls back to the raw
/// collection sequence with canonical topic-kind steps. That fallback is its own
/// sequence — not the model's raw one — so it is previewed on its own and held
/// to the changeset service's operation limit, so the diff, the hash and the
/// steps all describe what the fallback would actually stage.
/// </summary>
public sealed class AssistantChangesetDraftFallbackTests
{
    private static readonly Guid CollectionA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CollectionB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    [Fact]
    public async Task The_fallback_is_previewed_on_its_own_instead_of_reusing_the_raw_preview()
    {
        var state = PlanState(topicCount: 2);
        var service = new ScriptedChangesetService()
            .ThenOk(RawDiff(), "raw-hash", state)
            .ThenFail("changeset_invalid", "That order cannot be previewed.")
            .ThenOk(FallbackDiff(), "fallback-hash");

        var draft = new AssistantChangesetDraft(service, library: null!);
        var staged = await draft.StageAsync(Additions(topicCount: 2), default);

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        // The raw preview, the canonical plan's, then the fallback's own.
        service.Previews.Should().HaveCount(3);
        service.Previews[2].Should().NotBeEquivalentTo(service.Previews[0],
            "the fallback rebuilds the kind steps, so it is not the raw sequence");
        draft.DiffHash.Should().Be("fallback-hash",
            "the hash the user reviews must be the fallback's, which is what an apply would run");
        draft.Diff!.Counts.CollectionsUpdated.Should().Be(2);
    }

    [Fact]
    public async Task A_fallback_over_the_operation_limit_is_refused_before_it_is_previewed()
    {
        // Two canonical renames and 998 kind steps come to exactly the service's
        // limit, while the raw collection sequence plus those 998 steps is one
        // more: the fallback is what would be staged, so it is what is measured.
        const int topics = 998;
        var state = PlanState(topics);
        var service = new ScriptedChangesetService()
            .ThenOk(RawDiff(), "raw-hash", state)
            .ThenFail("changeset_invalid", "That order cannot be previewed.")
            .ThenOk(FallbackDiff(), "fallback-hash");

        var draft = new AssistantChangesetDraft(service, library: null!);
        var staged = await draft.StageAsync(Additions(topics), default);

        staged.Success.Should().BeFalse();
        staged.ErrorCode.Should().Be("changeset_too_large");
        service.Previews.Should().HaveCount(2, "the fallback is refused, never previewed");
        draft.IsEmpty.Should().BeTrue();
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Two collections the raw sequence renames three times and the canonical
    /// plan twice, and a set of topics whose kind the change gives. The raw
    /// previews as a draft of its own; the fallback rebuilds the kind steps one
    /// per topic, so it is a different sequence from the raw one.
    /// </summary>
    private static LibraryChangesetPreviewState PlanState(int topicCount) =>
        new(
            Snapshot([("A", CollectionA), ("B", CollectionB)], topicCount, kindAssigned: false),
            Snapshot([("Y", CollectionA), ("Z", CollectionB)], topicCount, kindAssigned: true),
            new Dictionary<string, Guid>(),
            new Dictionary<Guid, string>());

    private static LibraryChangesetDiff.LibrarySnapshot Snapshot(
        IReadOnlyList<(string Name, Guid Id)> collections,
        int topicCount,
        bool kindAssigned) =>
        SnapshotOf(collections, Assignments(topicCount, kindAssigned));

    private static Dictionary<Guid, TopicKindAssignmentState> Assignments(int topicCount, bool kindAssigned)
    {
        var assignments = new Dictionary<Guid, TopicKindAssignmentState>();
        for (var index = 0; index < topicCount; index++)
        {
            assignments[TopicId(index)] = new TopicKindAssignmentState(
                $"Topic {index}",
                kindAssigned ? TopicKindModel.IdeaId : null,
                kindAssigned ? TopicKindSources.Assistant : null);
        }

        return assignments;
    }

    private static LibraryChangesetDiff.LibrarySnapshot SnapshotOf(
        IReadOnlyList<(string Name, Guid Id)> collections,
        IReadOnlyDictionary<Guid, TopicKindAssignmentState> assignments) =>
        new(
            "1",
            collections.ToDictionary(
                entry => entry.Id,
                entry => new CollectionState(entry.Name, null)),
            new Dictionary<Guid, HashSet<Guid>>(),
            new Dictionary<Guid, BookFieldsState>(),
            new Dictionary<Guid, TopicKindState>
            {
                [TopicKindModel.IdeaId] = new("Idea", "kind-2", 2),
            },
            assignments);

    private static Guid TopicId(int index) =>
        Guid.Parse($"bbbbbbbb-0000-0000-0000-{index:D12}");

    private static List<(LibraryChangeOp Op, IReadOnlyList<int> DependsOn)> Additions(int topicCount) =>
    [
        // The raw sequence renames A twice on the way to Y; the canonical plan
        // collapses that to one rename, so the two sequences differ.
        (new RenameCollectionOp(LibraryCollectionRef.Existing(CollectionA), "X"), []),
        (new RenameCollectionOp(LibraryCollectionRef.Existing(CollectionA), "Y"), []),
        (new RenameCollectionOp(LibraryCollectionRef.Existing(CollectionB), "Z"), []),
        (new SetTopicKindOp(
            Enumerable.Range(0, topicCount).Select(TopicId).ToList(),
            TopicKindRef.Existing(TopicKindModel.IdeaId),
            NamedTopicIds: []), []),
    ];

    private static LibraryChangesetDiffDto RawDiff() =>
        new(
            [new LibraryCollectionChangeDto(CollectionA, "updated", "Y", "A", null, null, false, 0)],
            [],
            [],
            new LibraryChangesetCountsDto(0, 0, 1, 0, 0, 0, 0, 0),
            [],
            []);

    private static LibraryChangesetDiffDto FallbackDiff() =>
        new(
            [
                new LibraryCollectionChangeDto(CollectionA, "updated", "Y", "A", null, null, false, 0),
                new LibraryCollectionChangeDto(CollectionB, "updated", "Z", "B", null, null, false, 0),
            ],
            [],
            [],
            new LibraryChangesetCountsDto(0, 0, 2, 0, 0, 0, 0, 0),
            [],
            []);

    /// <summary>A changeset service whose previews are scripted, one per call.</summary>
    private sealed class ScriptedChangesetService : ILibraryChangesetService
    {
        private readonly List<Func<LibraryChangesetResult>> _script = [];

        public List<IReadOnlyList<LibraryChangeOp>> Previews { get; } = [];

        public ScriptedChangesetService ThenOk(
            LibraryChangesetDiffDto diff,
            string hash,
            LibraryChangesetPreviewState state = null)
        {
            _script.Add(() => new LibraryChangesetResult(true, null, null, null, diff, hash, "1", false, null)
            {
                PreviewState = state,
            });
            return this;
        }

        public ScriptedChangesetService ThenFail(string code, string message)
        {
            _script.Add(() => LibraryChangesetResult.Fail(code, message, "1"));
            return this;
        }

        public Task<LibraryChangesetResult> PreviewAsync(
            IReadOnlyList<LibraryChangeOp> ops,
            CancellationToken ct = default)
        {
            Previews.Add(ops);
            if (_script.Count == 0)
                throw new InvalidOperationException("The draft previewed more often than the test scripted.");
            var next = _script[0];
            _script.RemoveAt(0);
            return Task.FromResult(next());
        }

        public Task<LibraryChangesetResult> ApplyAsync(
            IReadOnlyList<LibraryChangeOp> ops,
            string? expectedDiffHash,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Staging never applies.");

        public Task<LibraryRevertResult> RevertAsync(
            LibraryUndoSnapshot undo,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Staging never undoes.");
    }
}

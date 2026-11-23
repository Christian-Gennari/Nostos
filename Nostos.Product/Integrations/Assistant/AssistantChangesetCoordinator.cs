using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>What finishing a turn did with its staged library changes.</summary>
public sealed record AssistantChangesetOutcome(AssistantChangesetDto? Changeset, AssistantTurnErrorDto? Error)
{
    public static readonly AssistantChangesetOutcome None = new(null, null);

    public bool Applied => Changeset?.Status == "applied";
    public bool AwaitingReview => Changeset?.Status == "review";
}

/// <summary>
/// Every transition of a turn's library changes: finishing the turn (apply at
/// once or hold for review, as the change policy decides), recomputing a
/// review with steps left out, applying it, discarding it, and undoing an
/// applied changeset. Each runs only the server's stored steps.
/// </summary>
public sealed class AssistantChangesetCoordinator(
    AssistantChangesetStore store,
    ILibraryChangesetService changesets,
    AssistantOptions options,
    IAssistantOwner? owner = null)
{
    private readonly IAssistantOwner _owner = owner ?? SingleAssistantOwner.Instance;

    public const string ReviewReply = "Here's what I'd change. Nothing changes until you apply it.";
    public const string LibraryMovedReason = "The library changed while I was working, so check this before applying.";

    /// <summary>
    /// Applies the turn's changes or holds them for review. Never applies twice
    /// for the same turn: a retried delivery reports the earlier result.
    /// </summary>
    public async Task<AssistantChangesetOutcome> FinishTurnAsync(
        string conversationKey,
        string turnId,
        AssistantChangesetDraft draft,
        bool readUntrustedContent,
        bool turnIncomplete,
        CancellationToken ct)
    {
        if (store.FindAppliedForTurn(_owner.Key, conversationKey, turnId) is { } earlier)
            return new AssistantChangesetOutcome(earlier.ToDto(), null);

        if (draft.IsEmpty || draft.Diff is not { } diff || diff.IsEmpty || draft.DiffHash is not { } hash)
            return AssistantChangesetOutcome.None;

        var decision = AssistantChangePolicy.Evaluate(diff, options, readUntrustedContent, turnIncomplete);
        if (decision.RequiresReview)
        {
            var review = store.Add(_owner.Key, conversationKey, turnId, draft.Steps, diff, hash,
                AssistantChangesetStatus.Review, decision.Reasons, undo: null,
                readUntrustedContent, turnIncomplete);
            return new AssistantChangesetOutcome(review.ToDto(), null);
        }

        // The write itself must not be abandoned half way by a client hang-up.
        var applied = await changesets.ApplyAsync(draft.Steps.Select(s => s.Op).ToList(), hash, CancellationToken.None);
        if (applied.Success)
        {
            var stored = store.Add(_owner.Key, conversationKey, turnId, draft.Steps, applied.Diff!, applied.DiffHash!,
                AssistantChangesetStatus.Applied, [], applied.Undo);
            return new AssistantChangesetOutcome(stored.ToDto(), null);
        }

        if (applied.ErrorCode == LibraryChangesetErrorCodes.DiffChanged && applied.Diff is { IsEmpty: false })
        {
            var review = store.Add(_owner.Key, conversationKey, turnId, draft.Steps, applied.Diff, applied.DiffHash!,
                AssistantChangesetStatus.Review, [LibraryMovedReason], undo: null,
                readUntrustedContent, turnIncomplete);
            return new AssistantChangesetOutcome(review.ToDto(), null);
        }

        return new AssistantChangesetOutcome(null, new AssistantTurnErrorDto(
            applied.ErrorCode ?? "changeset_failed",
            $"Nothing was changed: {applied.ErrorMessage ?? "the changes could not be applied."}"));
    }

    public async Task<AssistantChangesetActionResponse> PreviewAsync(
        string? changesetId,
        AssistantChangesetPreviewRequest request,
        CancellationToken ct)
    {
        if (Find(changesetId, request.ActionToken, out var changeset) is { } refused)
            return refused;
        if (changeset!.Status != AssistantChangesetStatus.Review || changeset.Busy)
            return NotReviewable(changeset);

        var refresh = await RefreshAsync(changeset, WithDependants(changeset, request.Excluded), ct);
        if (!refresh.Result.Success)
            return Fail(refresh.Result.ErrorCode ?? "changeset_failed", refresh.Result.ErrorMessage, changeset);

        // The reasons are judged on what the remaining steps do now. A kind the
        // preview no longer creates must not still say it creates it.
        var reasons = ReasonsFor(changeset, refresh.Result.Diff!);
        store.Update(changeset, c =>
        {
            c.Excluded = refresh.Excluded;
            c.SkippedReasons = refresh.SkippedReasons;
            c.Diff = refresh.Result.Diff!;
            c.DiffHash = refresh.Result.DiffHash!;
            c.ReviewReasons = reasons;
        });
        return Ok(changeset);
    }

    public async Task<AssistantChangesetActionResponse> ApplyAsync(
        string? changesetId,
        AssistantChangesetApplyRequest request,
        CancellationToken ct)
    {
        if (Find(changesetId, request.ActionToken, out var changeset) is { } refused)
            return refused;

        // A retry after a lost response: the first apply already happened.
        if (changeset!.Status == AssistantChangesetStatus.Applied)
            return Ok(changeset);

        if (!store.TryBegin(changeset, AssistantChangesetStatus.Review))
            return changeset.Busy
                ? Fail(AssistantErrorCodes.ChangesetBusy, "These changes are already being applied.", changeset)
                : NotReviewable(changeset);

        var excluded = WithDependants(
            changeset,
            (request.Excluded ?? []).Concat(changeset.SkippedReasons.Keys).ToList());
        var ops = Included(changeset, excluded);
        if (ops.Count == 0)
        {
            store.End(changeset, _ => { });
            return Fail(LibraryChangesetErrorCodes.Empty,
                changeset.SkippedReasons.Count > 0
                    ? "There is nothing left to apply: every remaining step would change nothing."
                    : "Every step is left out, so there is nothing to apply.",
                changeset);
        }

        LibraryChangesetResult result;
        try
        {
            result = await changesets.ApplyAsync(ops, request.DiffHash ?? changeset.DiffHash, CancellationToken.None);
        }
        catch
        {
            store.End(changeset, _ => { });
            throw;
        }

        if (result.Success)
        {
            store.End(changeset, c =>
            {
                c.Excluded = excluded;
                c.SkippedReasons = [];
                c.Status = AssistantChangesetStatus.Applied;
                c.Diff = result.Diff!;
                c.DiffHash = result.DiffHash!;
                c.Undo = result.Undo;
            });
            return Ok(changeset);
        }

        if (result.ErrorCode == LibraryChangesetErrorCodes.DiffChanged && result.Diff is not null)
        {
            // The library moved under the review. Recompute the review against
            // what the remaining steps would do now, and keep the steps the
            // library made useless out of it: a second Apply must not run them
            // again, and must not create a kind no topic still needs.
            var refresh = await RefreshAsync(changeset, excluded, ct);
            var shown = refresh.Result.Success ? refresh.Result : result;
            var reasons = refresh.Result.Success
                ? new[] { LibraryMovedReason }.Concat(ReasonsFor(changeset, shown.Diff!)).ToList()
                : [LibraryMovedReason];
            store.End(changeset, c =>
            {
                c.Excluded = refresh.Excluded;
                c.SkippedReasons = refresh.SkippedReasons;
                c.Diff = shown.Diff!;
                c.DiffHash = shown.DiffHash!;
                c.ReviewReasons = reasons;
            });
            return Fail(result.ErrorCode, result.ErrorMessage, changeset);
        }

        store.End(changeset, _ => { });
        return Fail(result.ErrorCode ?? "changeset_failed", result.ErrorMessage, changeset);
    }

    public AssistantChangesetActionResponse Discard(string? changesetId, AssistantChangesetTokenRequest request)
    {
        if (Find(changesetId, request.ActionToken, out var changeset) is { } refused)
            return refused;
        if (changeset!.Status == AssistantChangesetStatus.Discarded)
            return Ok(changeset);
        if (!store.TryBegin(changeset, AssistantChangesetStatus.Review))
            return NotReviewable(changeset);

        store.End(changeset, c => c.Status = AssistantChangesetStatus.Discarded);
        return Ok(changeset);
    }

    public async Task<AssistantChangesetActionResponse> UndoAsync(
        string? changesetId,
        AssistantChangesetTokenRequest request,
        CancellationToken ct)
    {
        if (Find(changesetId, request.ActionToken, out var changeset) is { } refused)
            return refused;
        if (changeset!.Status == AssistantChangesetStatus.Undone)
            return Ok(changeset);
        if (changeset.Undo is null || !store.TryBegin(changeset, AssistantChangesetStatus.Applied))
        {
            return Fail(AssistantErrorCodes.ChangesetNotUndoable,
                changeset.Busy ? "These changes are already being undone." : "These changes can no longer be undone here.",
                changeset);
        }

        LibraryRevertResult reverted;
        try
        {
            reverted = await changesets.RevertAsync(changeset.Undo, CancellationToken.None);
        }
        catch
        {
            store.End(changeset, _ => { });
            throw;
        }

        store.End(changeset, c =>
        {
            c.Status = AssistantChangesetStatus.Undone;
            c.Undo = null;
        });
        return new AssistantChangesetActionResponse(true, null, null, changeset.ToDto(), reverted.Conflicts);
    }

    // ------------------------------------------------------------------

    private static readonly LibraryChangesetDiffDto EmptyDiff =
        new([], [], [], new LibraryChangesetCountsDto(0, 0, 0, 0, 0, 0, 0, 0), [], []);

    private static readonly string EmptyDiffHash = LibraryChangesetDiff.Hash(EmptyDiff);

    /// <summary>The kind a step gives a topic the reader has already set themselves.</summary>
    private const string KeptOwnKind = "kept: you set this kind yourself";

    /// <summary>A create-kind step no topic still needs.</summary>
    private const string KeptEmptyKind = "kept: no topic still needs this kind";

    /// <summary>A clear whose topic already has no kind.</summary>
    private const string KeptUngivenKind = "kept: the topic already has no kind";

    /// <summary>
    /// What the remaining steps would do now: the diff, the steps the server
    /// kept out because they would change nothing, and the excluded set that
    /// makes a later apply agree with it.
    /// </summary>
    private sealed record ReviewRefresh(
        LibraryChangesetResult Result,
        HashSet<int> Excluded,
        Dictionary<int, string> SkippedReasons);

    /// <summary>
    /// Recomputes the review and reconciles it with the library. A step the
    /// library has made useless (the reader set the staged topic's kind
    /// themselves between the preview and the apply) is left out with a reason
    /// rather than shown as applied: the diff no longer contains it, and a
    /// second Apply must not run it, nor create a kind no topic still needs.
    /// </summary>
    private async Task<ReviewRefresh> RefreshAsync(
        StoredAssistantChangeset changeset,
        HashSet<int> excluded,
        CancellationToken ct)
    {
        var preview = await PreviewIncludedAsync(changeset, excluded, ct);
        var skipped = DeadSteps(changeset, excluded, preview);
        if (skipped.Count == 0 || preview.Diff is null)
            return new ReviewRefresh(preview, excluded, []);

        // The steps now do nothing, so the review is recomputed without them:
        // its diff, its hash and its kind groups must describe what an apply of
        // the remaining steps would actually do.
        excluded.UnionWith(skipped.Keys);
        var again = await PreviewIncludedAsync(changeset, excluded, ct);
        return new ReviewRefresh(again, excluded, skipped);
    }

    /// <summary>The preview of exactly the steps that are not excluded; an empty one with none.</summary>
    private async Task<LibraryChangesetResult> PreviewIncludedAsync(
        StoredAssistantChangeset changeset,
        HashSet<int> excluded,
        CancellationToken ct)
    {
        var ops = Included(changeset, excluded);
        return ops.Count == 0
            ? new LibraryChangesetResult(true, null, null, null, EmptyDiff, EmptyDiffHash, "0", false, null)
            : await changesets.PreviewAsync(ops, ct);
    }

    /// <summary>
    /// The included steps this preview shows would change nothing, with the
    /// sentence the card shows for each. Only kind steps are judged this way:
    /// their effect is one named row per topic in the diff, so a protected
    /// topic (or a kind nothing is left to create) is visible as an absence.
    /// </summary>
    private static Dictionary<int, string> DeadSteps(
        StoredAssistantChangeset changeset,
        HashSet<int> excluded,
        LibraryChangesetResult preview)
    {
        var dead = new Dictionary<int, string>();
        if (preview.Diff is not { } diff)
            return dead;

        var assignedTopics = diff.TopicKindAssignments.Select(assignment => assignment.TopicId).ToHashSet();
        for (var index = 0; index < changeset.Steps.Count; index++)
        {
            if (excluded.Contains(index))
                continue;

            switch (changeset.Steps[index].Op)
            {
                case SetTopicKindOp setKind when setKind.TopicIds.All(topicId => !assignedTopics.Contains(topicId)):
                    dead[index] = setKind.Kind is null ? KeptUngivenKind : KeptOwnKind;
                    break;
                case CreateTopicKindOp createKind when CreatedKindHasNoTopic(diff, createKind):
                    dead[index] = KeptEmptyKind;
                    break;
            }
        }

        return dead;
    }

    /// <summary>True when the kind this step creates was made, and no topic the diff changes uses it.</summary>
    private static bool CreatedKindHasNoTopic(LibraryChangesetDiffDto diff, CreateTopicKindOp op)
    {
        var created = diff.TopicKinds.FirstOrDefault(kind =>
            kind.Kind == "created" && string.Equals(kind.Name, op.Name, StringComparison.Ordinal));
        // No created kind means the step never ran (its topics were left out, so
        // the include set dropped it) or a sibling with that name was reused:
        // either way there is no empty kind to name here.
        return created is not null
            && !diff.TopicKindAssignments.Any(assignment => assignment.KindId == created.Id);
    }

    /// <summary>The review's reasons on the diff the remaining steps would produce.</summary>
    private IReadOnlyList<string> ReasonsFor(StoredAssistantChangeset changeset, LibraryChangesetDiffDto diff) =>
        AssistantChangePolicy
            .Evaluate(diff, options, changeset.ReadUntrustedContent, changeset.TurnIncomplete)
            .Reasons;

    private AssistantChangesetActionResponse? Find(
        string? changesetId,
        string? token,
        out StoredAssistantChangeset? changeset) =>
        store.Find(changesetId, token, out changeset) switch
        {
            AssistantChangesetStore.Lookup.Found => null,
            AssistantChangesetStore.Lookup.TokenMismatch => Fail(AssistantErrorCodes.ChangesetTokenMismatch,
                "These changes belong to a different conversation.", null),
            _ => Fail(AssistantErrorCodes.ChangesetNotFound,
                "These changes are no longer available. Ask again to prepare them afresh.", null),
        };

    /// <summary>Leaving a step out leaves out every step that needs it.</summary>
    private static HashSet<int> WithDependants(StoredAssistantChangeset changeset, IReadOnlyList<int>? excluded)
    {
        var result = (excluded ?? []).Where(i => i >= 0 && i < changeset.Steps.Count).ToHashSet();
        bool grew;
        do
        {
            grew = false;
            for (var index = 0; index < changeset.Steps.Count; index++)
            {
                if (!result.Contains(index) && changeset.Steps[index].DependsOn.Any(result.Contains))
                    grew = result.Add(index);
            }
        }
        while (grew);
        return result;
    }

    private static List<LibraryChangeOp> Included(StoredAssistantChangeset changeset, HashSet<int> excluded)
    {
        var included = changeset.Steps.Where((_, index) => !excluded.Contains(index)).Select(s => s.Op).ToList();

        // A kind the changeset creates is only there for the topics it
        // classifies. When the reader unticks every topic of a new kind, the
        // kind itself goes too; a kind staged on its own stays.
        var allCreated = changeset.Steps.Select(step => step.Op).OfType<CreateTopicKindOp>()
            .Select(op => op.Ref)
            .ToHashSet(StringComparer.Ordinal);
        if (allCreated.Count == 0)
            return included;

        var usedEverywhere = changeset.Steps.Select(step => step.Op).OfType<SetTopicKindOp>()
            .Where(op => op.Kind?.Ref is not null)
            .Select(op => op.Kind!.Value.Ref!)
            .ToHashSet(StringComparer.Ordinal);
        var usedNow = included.OfType<SetTopicKindOp>()
            .Where(op => op.Kind?.Ref is not null)
            .Select(op => op.Kind!.Value.Ref!)
            .ToHashSet(StringComparer.Ordinal);

        return included
            .Where(op => op is not CreateTopicKindOp createKind
                || !usedEverywhere.Contains(createKind.Ref)
                || usedNow.Contains(createKind.Ref))
            .ToList();
    }

    private static AssistantChangesetActionResponse Ok(StoredAssistantChangeset changeset) =>
        new(true, null, null, changeset.ToDto(), []);

    private static AssistantChangesetActionResponse NotReviewable(StoredAssistantChangeset changeset) =>
        Fail(AssistantErrorCodes.ChangesetNotReviewable, changeset.Status switch
        {
            AssistantChangesetStatus.Discarded => "These changes were set aside. Ask again to prepare them afresh.",
            AssistantChangesetStatus.Undone => "These changes were undone.",
            AssistantChangesetStatus.Applied => "These changes are already applied.",
            _ => "These changes are being applied.",
        }, changeset);

    private static AssistantChangesetActionResponse Fail(
        string code,
        string? message,
        StoredAssistantChangeset? changeset) =>
        new(false, code, message ?? "The changes could not be made.", changeset?.ToDto(), []);
}

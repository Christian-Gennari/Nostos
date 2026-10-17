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
                AssistantChangesetStatus.Review, decision.Reasons, undo: null);
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
                AssistantChangesetStatus.Review, [LibraryMovedReason], undo: null);
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

        var excluded = WithDependants(changeset, request.Excluded);
        var ops = Included(changeset, excluded);
        if (ops.Count == 0)
        {
            store.Update(changeset, c =>
            {
                c.Excluded = excluded;
                c.Diff = EmptyDiff;
                c.DiffHash = string.Empty;
            });
            return Ok(changeset);
        }

        var preview = await changesets.PreviewAsync(ops, ct);
        if (!preview.Success)
            return Fail(preview.ErrorCode ?? "changeset_failed", preview.ErrorMessage, changeset);

        store.Update(changeset, c =>
        {
            c.Excluded = excluded;
            c.Diff = preview.Diff!;
            c.DiffHash = preview.DiffHash!;
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

        var excluded = WithDependants(changeset, request.Excluded);
        var ops = Included(changeset, excluded);
        if (ops.Count == 0)
        {
            store.End(changeset, _ => { });
            return Fail(LibraryChangesetErrorCodes.Empty, "Every step is left out, so there is nothing to apply.", changeset);
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
                c.Status = AssistantChangesetStatus.Applied;
                c.Diff = result.Diff!;
                c.DiffHash = result.DiffHash!;
                c.Undo = result.Undo;
            });
            return Ok(changeset);
        }

        if (result.ErrorCode == LibraryChangesetErrorCodes.DiffChanged && result.Diff is not null)
        {
            store.End(changeset, c =>
            {
                c.Excluded = excluded;
                c.Diff = result.Diff;
                c.DiffHash = result.DiffHash!;
                c.ReviewReasons = [LibraryMovedReason];
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
        new([], [], [], new LibraryChangesetCountsDto(0, 0, 0, 0, 0, 0, 0, 0));

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

    private static List<LibraryChangeOp> Included(StoredAssistantChangeset changeset, HashSet<int> excluded) =>
        changeset.Steps.Where((_, index) => !excluded.Contains(index)).Select(s => s.Op).ToList();

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

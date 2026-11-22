using System.Text.Json;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>One staged operation, with the sentence the user reviews it by.</summary>
public sealed record AssistantStagedStep(LibraryChangeOp Op, string Summary, IReadOnlyList<int> DependsOn);

/// <summary>
/// The library changes one turn is building. Stage capabilities add to it; it
/// never writes. Every addition is validated against the raw sequence through
/// the canonical library code, then collapsed into a canonical plan derived
/// directly from the net before/after diff so equivalent plans produce the same
/// steps and diffHash (#1740). What it holds when the turn ends is what the
/// change policy judges.
/// </summary>
public sealed class AssistantChangesetDraft(ILibraryChangesetService changesets, ILibraryService library)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly LibraryChangesetCountsDto EmptyCounts = new(0, 0, 0, 0, 0, 0, 0, 0);

    private readonly List<LibraryChangeOp> _rawOps = [];
    private readonly List<AssistantStagedStep> _steps = [];
    private readonly Dictionary<string, LibraryCollectionRef> _liveNamedCollections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TopicKindRef> _liveNamedKinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, BookDto?> _books = [];
    private Dictionary<Guid, CollectionDto>? _collections;
    private LibraryChangesetPreviewState? _lastPreviewState;
    private int _refCounter;

    public IReadOnlyList<AssistantStagedStep> Steps => _steps;

    /// <summary>The diff of everything staged so far, from the last successful preview.</summary>
    public LibraryChangesetDiffDto? Diff { get; private set; }

    public string? DiffHash { get; private set; }

    /// <summary>
    /// The last staging call found nothing to change: the library already
    /// matches the request, so no review exists (issue #1746).
    /// </summary>
    public bool LastStageWasNoChange { get; private set; }

    public bool IsEmpty => _steps.Count == 0;

    /// <summary>
    /// Reads a collection argument: an existing collection's id, or the name
    /// (or <c>ref</c>) of a collection created or reused earlier in this turn
    /// that is still live in the staged library state.
    /// </summary>
    public bool TryResolveCollection(string? value, out LibraryCollectionRef reference, out int? createdBy)
    {
        reference = default;
        createdBy = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var id))
        {
            reference = LibraryCollectionRef.Existing(id);
            return true;
        }

        if (_liveNamedCollections.TryGetValue(trimmed, out var live))
        {
            reference = live;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a topic-kind argument: an existing kind's id, or the name (or
    /// <c>ref</c>) of a kind created earlier in this turn.
    /// </summary>
    public bool TryResolveTopicKind(string? value, out TopicKindRef reference, out int? createdBy)
    {
        reference = default;
        createdBy = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var id))
        {
            reference = TopicKindRef.Existing(id);
            return true;
        }

        if (_liveNamedKinds.TryGetValue(trimmed, out var live))
        {
            reference = live;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Stages one operation. On success the result tells the model what the
    /// whole changeset would now do; on failure nothing is staged and the
    /// canonical failure is returned as-is.
    /// </summary>
    public Task<AssistantToolResult> StageAsync(
        LibraryChangeOp op,
        IReadOnlyList<int> dependsOn,
        CancellationToken ct) =>
        StageAsync([(op, dependsOn)], ct);

    /// <summary>
    /// Stages several operations as consecutive steps, validated and rebuilt
    /// from the net diff once. Either all of them are staged or none is.
    /// </summary>
    public async Task<AssistantToolResult> StageAsync(
        IReadOnlyList<(LibraryChangeOp Op, IReadOnlyList<int> DependsOn)> additions,
        CancellationToken ct)
    {
        // Deleting an existing collection that was already deleted earlier in
        // this turn (directly or by cascade) is an idempotent no-op.
        if (additions.Count == 1
            && additions[0].Op is DeleteCollectionOp { Collection.Id: { } deletedId }
            && _lastPreviewState is { } prior
            && prior.Before.Collections.ContainsKey(deletedId)
            && !prior.After.Collections.ContainsKey(deletedId))
        {
            return CurrentResult();
        }

        var baseCount = _rawOps.Count;
        var nextRefId = _refCounter;
        var batchRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var batchKindRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawCandidate = new List<LibraryChangeOp>(_rawOps.Count + additions.Count);
        rawCandidate.AddRange(_rawOps);

        foreach (var (op, _) in additions)
        {
            if (op is CreateCollectionOp create)
            {
                var uniqueRef = $"{create.Ref}#{++nextRefId}";
                var remappedParent = create.Parent is { } parent ? RemapBatchRef(parent, batchRefs) : (LibraryCollectionRef?)null;
                batchRefs[create.Ref] = uniqueRef;
                rawCandidate.Add(create with { Ref = uniqueRef, Parent = remappedParent });
            }
            else if (op is CreateTopicKindOp createKind)
            {
                var uniqueRef = $"{createKind.Ref}#{++nextRefId}";
                batchKindRefs[createKind.Ref] = uniqueRef;
                rawCandidate.Add(createKind with { Ref = uniqueRef });
            }
            else
            {
                rawCandidate.Add(RemapBatchRefs(op, batchRefs, batchKindRefs));
            }
        }

        var rawPreview = await changesets.PreviewAsync(rawCandidate, ct);
        if (!rawPreview.Success)
        {
            var failedStep = rawPreview.FailedOperation is { } index && index >= baseCount
                ? $" (in the change you just asked for, part {index - baseCount + 1})"
                : string.Empty;
            var message = (rawPreview.ErrorMessage ?? "That change could not be made.") + failedStep;
            return AssistantToolResult.Fail(
                rawPreview.ErrorCode ?? "changeset_invalid",
                message,
                JsonSerializer.SerializeToElement(new { status = "not_staged", reason = message }, Json));
        }

        var previewState = rawPreview.PreviewState;
        if (previewState is null)
        {
            // A service without preview state (a fake or a wrapper) gives no
            // snapshots to plan from: keep the model's operations as they are,
            // with one dependency list per operation.
            return await CommitAsync(
                rawCandidate, nextRefId, previewState: null, rawCandidate,
                ComputeSimpleDependsOn(rawCandidate), new Dictionary<Guid, int>(), rawPreview, ct);
        }

        var plan = AssistantChangesetPlanBuilder.Build(previewState, rawCandidate);
        if (plan.Ops.Count == 0)
        {
            CommitState(rawCandidate, nextRefId, previewState);
            _steps.Clear();
            Diff = null;
            LastStageWasNoChange = true;
            DiffHash = null;
            return NoChangeResult(EmptyCounts);
        }

        var canonicalPreview = await changesets.PreviewAsync(plan.Ops, ct);
        if (!canonicalPreview.Success)
        {
            var fallbackDepends = ComputeSimpleDependsOn(rawCandidate);
            return await CommitAsync(rawCandidate, nextRefId, previewState, rawCandidate, fallbackDepends, new Dictionary<Guid, int>(), rawPreview, ct);
        }

        return await CommitAsync(rawCandidate, nextRefId, previewState, plan.Ops, plan.DependsOn, plan.DeletedDescendantCounts, canonicalPreview, ct);
    }

    private async Task<AssistantToolResult> CommitAsync(
        List<LibraryChangeOp> rawCandidate,
        int nextRefId,
        LibraryChangesetPreviewState? previewState,
        IReadOnlyList<LibraryChangeOp> presentedOps,
        IReadOnlyList<IReadOnlyList<int>> dependsOn,
        IReadOnlyDictionary<Guid, int> deletedDescendantCounts,
        LibraryChangesetResult finalPreview,
        CancellationToken ct)
    {
        CommitState(rawCandidate, nextRefId, previewState);

        if (finalPreview.Diff!.IsEmpty)
        {
            _steps.Clear();
            Diff = null;
            LastStageWasNoChange = true;
            DiffHash = null;
            return NoChangeResult(finalPreview.Diff.Counts);
        }

        var rebuilt = await BuildStepsAsync(presentedOps, dependsOn, deletedDescendantCounts, previewState, ct);
        _steps.Clear();
        _steps.AddRange(rebuilt);
        LastStageWasNoChange = false;
        Diff = finalPreview.Diff;
        DiffHash = finalPreview.DiffHash;

        return StagedResult(_steps, finalPreview.Diff.Counts);
    }

    private void CommitState(
        List<LibraryChangeOp> rawCandidate,
        int nextRefId,
        LibraryChangesetPreviewState? previewState)
    {
        _rawOps.Clear();
        _rawOps.AddRange(rawCandidate);
        _refCounter = nextRefId;
        _lastPreviewState = previewState;

        _liveNamedCollections.Clear();
        _liveNamedKinds.Clear();
        if (previewState is null)
            return;

        foreach (var op in _rawOps)
        {
            if (op is not CreateCollectionOp create)
                continue;
            if (!previewState.CreatedRefs.TryGetValue(create.Ref, out var id))
                continue;
            if (!previewState.After.Collections.TryGetValue(id, out var live))
                continue;

            var reference = LibraryCollectionRef.Created(create.Ref);
            _liveNamedCollections[live.Name] = reference;
            _liveNamedCollections[create.Ref] = reference;
        }

        _liveNamedKinds.Clear();
        foreach (var op in _rawOps)
        {
            if (op is not CreateTopicKindOp createKind)
                continue;
            if (!previewState.CreatedRefs.TryGetValue(createKind.Ref, out var kindId))
                continue;
            if (!previewState.After.TopicKinds.TryGetValue(kindId, out var live))
                continue;

            var reference = TopicKindRef.Created(createKind.Ref);
            _liveNamedKinds[live.Name] = reference;
            _liveNamedKinds[createKind.Ref] = reference;
        }
    }

    private AssistantToolResult CurrentResult() =>
        _steps.Count == 0 || Diff is null || Diff.IsEmpty
            ? NoChangeResult(Diff?.Counts ?? EmptyCounts)
            : StagedResult(_steps, Diff.Counts);

    private static AssistantToolResult NoChangeResult(LibraryChangesetCountsDto counts) =>
        AssistantToolResult.Ok(JsonSerializer.SerializeToElement(new
        {
            status = "no_change",
            steps = Array.Empty<string>(),
            note = "No library change was needed: the library already matches what you asked for.",
            changesetSoFar = counts,
        }, Json));

    private static AssistantToolResult StagedResult(
        IReadOnlyList<AssistantStagedStep> steps,
        LibraryChangesetCountsDto counts) =>
        AssistantToolResult.Ok(JsonSerializer.SerializeToElement(new
        {
            status = "staged",
            steps = steps.Select(s => s.Summary).ToList(),
            note = "Staged, not applied. Stage every change the request needs; Nostos applies or presents them together when you finish.",
            changesetSoFar = counts,
        }, Json));

    private static LibraryCollectionRef RemapBatchRef(
        LibraryCollectionRef reference,
        Dictionary<string, string> batchRefs) =>
        reference.Ref is { } name && batchRefs.TryGetValue(name, out var mapped)
            ? LibraryCollectionRef.Created(mapped)
            : reference;

    private static LibraryChangeOp RemapBatchRefs(
        LibraryChangeOp op,
        Dictionary<string, string> batchRefs,
        Dictionary<string, string> batchKindRefs)
    {
        if (batchRefs.Count == 0 && batchKindRefs.Count == 0)
            return op;

        return op switch
        {
            RenameCollectionOp rename => rename with { Collection = RemapBatchRef(rename.Collection, batchRefs) },
            MoveCollectionOp move => move with
            {
                Collection = RemapBatchRef(move.Collection, batchRefs),
                NewParent = move.NewParent is { } p ? RemapBatchRef(p, batchRefs) : null,
            },
            DeleteCollectionOp delete => delete with { Collection = RemapBatchRef(delete.Collection, batchRefs) },
            SetBookCollectionsOp set => set with { Collections = set.Collections.Select(c => RemapBatchRef(c, batchRefs)).ToList() },
            AddBooksToCollectionOp add => add with { Collection = RemapBatchRef(add.Collection, batchRefs) },
            RemoveBooksFromCollectionOp remove => remove with { Collection = RemapBatchRef(remove.Collection, batchRefs) },
            SetTopicKindOp setKind => setKind with
            {
                Kind = setKind.Kind is { } kind ? RemapBatchKindRef(kind, batchKindRefs) : null,
            },
            _ => op,
        };
    }

    private static TopicKindRef RemapBatchKindRef(
        TopicKindRef reference,
        Dictionary<string, string> batchKindRefs) =>
        reference.Ref is { } name && batchKindRefs.TryGetValue(name, out var mapped)
            ? TopicKindRef.Created(mapped)
            : reference;

    private static List<IReadOnlyList<int>> ComputeSimpleDependsOn(IReadOnlyList<LibraryChangeOp> ops)
    {
        var createStepByRef = new Dictionary<string, int>(StringComparer.Ordinal);
        var createKindStepByRef = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i] is CreateCollectionOp create)
                createStepByRef[create.Ref] = i;
            if (ops[i] is CreateTopicKindOp createKind)
                createKindStepByRef[createKind.Ref] = i;
        }

        return ops.Select((op, index) =>
        {
            var dependencies = AssistantChangesetPlanBuilder.ReferencesUsedBy(op)
                .Where(r => r.Ref is not null)
                .Select(r => createStepByRef.TryGetValue(r.Ref!, out var stepIndex) && stepIndex < index ? stepIndex : -1)
                .Where(stepIndex => stepIndex >= 0)
                .ToList();
            if (op is SetTopicKindOp { Kind.Ref: { } kindRef }
                && createKindStepByRef.TryGetValue(kindRef, out var kindStep)
                && kindStep < index)
            {
                dependencies.Add(kindStep);
            }
            return (IReadOnlyList<int>)dependencies.Distinct().Order().ToList();
        }).ToList();
    }

    // ------------------------------------------------------------------
    // Step sentences
    // ------------------------------------------------------------------

    private async Task<List<AssistantStagedStep>> BuildStepsAsync(
        IReadOnlyList<LibraryChangeOp> ops,
        IReadOnlyList<IReadOnlyList<int>> dependsOn,
        IReadOnlyDictionary<Guid, int> deletedDescendantCounts,
        LibraryChangesetPreviewState? previewState,
        CancellationToken ct)
    {
        var displayNames = new Dictionary<LibraryCollectionRef, string>();
        if (previewState is not null)
        {
            foreach (var (id, state) in previewState.Before.Collections)
                displayNames[LibraryCollectionRef.Existing(id)] = state.Name;
        }

        var steps = new List<AssistantStagedStep>(ops.Count);
        var kindNames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var summary = await SummarizeAsync(op, displayNames, kindNames, deletedDescendantCounts, previewState, ct);
            steps.Add(new AssistantStagedStep(op, summary, dependsOn[i]));
        }

        return steps;
    }

    private async Task<string> SummarizeAsync(
        LibraryChangeOp op,
        Dictionary<LibraryCollectionRef, string> displayNames,
        Dictionary<string, string> kindNames,
        IReadOnlyDictionary<Guid, int> deletedDescendantCounts,
        LibraryChangesetPreviewState? previewState,
        CancellationToken ct)
    {
        switch (op)
        {
            case CreateTopicKindOp createKind:
            {
                kindNames[createKind.Ref] = createKind.Name;
                return $"Create the topic kind “{createKind.Name}”";
            }
            case SetTopicKindOp setKind:
            {
                var kind = setKind.Kind is { } reference
                    ? KindName(reference, kindNames, previewState)
                    : null;
                var distinct = setKind.TopicIds.Distinct().ToList();
                var topics = distinct.Count == 1
                    ? $"“{TopicName(distinct[0], previewState)}”"
                    : $"{distinct.Count} topics";
                return kind is null
                    ? $"Clear the kind of {topics}"
                    : $"Set {topics} to “{kind}”";
            }
            case CreateCollectionOp create:
            {
                displayNames[LibraryCollectionRef.Created(create.Ref)] = create.Name;
                return create.Parent is { } parent
                    ? $"Create collection “{create.Name}” inside “{await NameAsync(parent, displayNames, ct)}”"
                    : $"Create collection “{create.Name}”";
            }
            case RenameCollectionOp rename:
            {
                var beforeName = await NameAsync(rename.Collection, displayNames, ct);
                displayNames[rename.Collection] = rename.Name;
                return $"Rename “{beforeName}” to “{rename.Name}”";
            }
            case MoveCollectionOp move:
                return move.NewParent is { } newParent
                    ? $"Move “{await NameAsync(move.Collection, displayNames, ct)}” into “{await NameAsync(newParent, displayNames, ct)}”"
                    : $"Move “{await NameAsync(move.Collection, displayNames, ct)}” to the top level";
            case DeleteCollectionOp delete:
            {
                var name = await NameAsync(delete.Collection, displayNames, ct);
                var descendants = delete.Collection.Id is { } id && deletedDescendantCounts.TryGetValue(id, out var count)
                    ? count
                    : 0;
                return descendants switch
                {
                    0 => $"Delete collection “{name}”",
                    1 => $"Delete collection “{name}” and its subcollection",
                    _ => $"Delete collection “{name}” and its {descendants} subcollections",
                };
            }
            case AddBooksToCollectionOp add:
                return $"Add {await BooksPhraseAsync(add.BookIds, previewState, ct)} to “{await NameAsync(add.Collection, displayNames, ct)}”";
            case RemoveBooksFromCollectionOp remove:
                return $"Remove {await BooksPhraseAsync(remove.BookIds, previewState, ct)} from “{await NameAsync(remove.Collection, displayNames, ct)}”";
            case SetBookCollectionsOp set:
            {
                var title = await BookTitleAsync(set.BookId, previewState, ct);
                if (set.Collections.Count == 0)
                    return $"Take “{title}” out of every collection";
                var names = new List<string>();
                foreach (var collection in set.Collections)
                    names.Add($"“{await NameAsync(collection, displayNames, ct)}”");
                return $"Put “{title}” in {string.Join(", ", names)} only";
            }
            case UpdateBookOp update:
            {
                var fields = new List<string>();
                if (update.Title is not null) fields.Add("title");
                if (update.Author is not null) fields.Add("author");
                if (update.Description is not null) fields.Add("description");
                if (update.Rating is not null) fields.Add("rating");
                if (update.EditionRating is not null) fields.Add("edition rating");
                if (update.IsFavorite is not null) fields.Add(update.IsFavorite.Value ? "mark favourite" : "unmark favourite");
                if (update.PersonalReview is not null) fields.Add("review");
                if (update.IsFinished is not null) fields.Add(update.IsFinished.Value ? "mark finished" : "mark not finished");
                return $"Edit “{await BookTitleAsync(update.BookId, previewState, ct)}”: {string.Join(", ", fields)}";
            }
            default:
                return "Change the library";
        }
    }

    private async Task<string> NameAsync(
        LibraryCollectionRef reference,
        Dictionary<LibraryCollectionRef, string> displayNames,
        CancellationToken ct)
    {
        if (displayNames.TryGetValue(reference, out var name))
            return name;

        if (reference.Ref is { } created)
            return created;

        var collections = await CollectionsAsync(ct);
        return collections.TryGetValue(reference.Id!.Value, out var dto) ? dto.Name : "a collection";
    }

    private static string KindName(
        TopicKindRef reference,
        Dictionary<string, string> kindNames,
        LibraryChangesetPreviewState? previewState)
    {
        if (reference.Ref is { } created)
            return kindNames.GetValueOrDefault(created) ?? created;

        if (reference.Id is { } id && previewState is not null
            && previewState.After.TopicKinds.TryGetValue(id, out var kind))
        {
            return kind.Name;
        }

        return "a kind";
    }

    private static string TopicName(Guid topicId, LibraryChangesetPreviewState? previewState) =>
        previewState?.After.TopicKindAssignments.GetValueOrDefault(topicId)?.TopicName
        ?? previewState?.Before.TopicKindAssignments.GetValueOrDefault(topicId)?.TopicName
        ?? "a topic";

    private async Task<string> BooksPhraseAsync(
        IReadOnlyList<Guid> bookIds,
        LibraryChangesetPreviewState? previewState,
        CancellationToken ct)
    {
        var distinct = bookIds.Distinct().ToList();
        return distinct.Count == 1
            ? $"“{await BookTitleAsync(distinct[0], previewState, ct)}”"
            : $"{distinct.Count} books";
    }

    private async Task<string> BookTitleAsync(
        Guid bookId,
        LibraryChangesetPreviewState? previewState,
        CancellationToken ct)
    {
        if (previewState is not null && previewState.BookTitles.TryGetValue(bookId, out var title))
            return title;

        var book = await TryGetBookAsync(bookId, ct);
        return book?.Title ?? "a book";
    }

    private async Task<BookDto?> TryGetBookAsync(Guid bookId, CancellationToken ct)
    {
        if (_books.TryGetValue(bookId, out var cached))
            return cached;

        var result = await library.GetBookAsync(bookId, ct);
        var book = result.Data as BookDto;
        _books[bookId] = book;
        return book;
    }

    private async Task<Dictionary<Guid, CollectionDto>> CollectionsAsync(CancellationToken ct)
    {
        if (_collections is not null)
            return _collections;

        var result = await library.ListCollectionsAsync(ct);
        _collections = result.Data is IEnumerable<CollectionDto> items
            ? items.ToDictionary(c => c.Id)
            : [];
        return _collections;
    }
}

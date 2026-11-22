using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Derives a canonical, minimal list of <see cref="LibraryChangeOp"/> directly
/// from the before/after snapshots produced by <see cref="ILibraryChangesetService.PreviewAsync"/>.
///
/// Because the plan is built from the net diff rather than the model's raw tool
/// calls, equivalent ways of reaching the same library state — deleting and
/// recreating a collection, splitting or repeating adds, using
/// <c>library_set_book_collections</c> vs add/remove, or calling tools in a
/// different order — produce the exact same steps, step order, and diffHash (#1740).
/// </summary>
internal static class AssistantChangesetPlanBuilder
{
    internal sealed record CanonicalPlan(
        IReadOnlyList<LibraryChangeOp> Ops,
        IReadOnlyList<IReadOnlyList<int>> DependsOn,
        IReadOnlyDictionary<Guid, int> DeletedDescendantCounts);

    public static CanonicalPlan Build(
        LibraryChangesetPreviewState state,
        IReadOnlyList<LibraryChangeOp> rawOps)
    {
        var before = state.Before;
        var after = state.After;

        // 1. Match deleted collections with newly created collections that have
        //    the same normalized name under the same effective parent, top-down,
        //    so delete+recreate becomes a reuse of the original collection id.
        var reusedMap = MatchReusedCollections(before.Collections, after.Collections);

        Guid MapId(Guid id) => reusedMap.TryGetValue(id, out var reused) ? reused : id;

        var normCollections = new Dictionary<Guid, CollectionState>(after.Collections.Count);
        foreach (var (id, col) in after.Collections)
        {
            var mappedId = MapId(id);
            var mappedParent = col.ParentId is { } parentId ? MapId(parentId) : (Guid?)null;
            normCollections[mappedId] = new CollectionState(col.Name, mappedParent);
        }

        var normMemberships = new Dictionary<Guid, HashSet<Guid>>(after.Memberships.Count);
        foreach (var (bookId, members) in after.Memberships)
            normMemberships[bookId] = members.Select(MapId).ToHashSet();

        // 2. Schedule canonical collection operations from before.Collections -> normCollections.
        if (!TryScheduleCollectionOps(
                before.Collections,
                normCollections,
                out var ops,
                out var createdRefById,
                out var moveStepByCollection,
                out var deletedDescendantCounts))
        {
            return FallbackToRaw(rawOps, before, after);
        }

        LibraryCollectionRef RefOf(Guid id) =>
            createdRefById.TryGetValue(id, out var reference)
                ? LibraryCollectionRef.Created(reference)
                : LibraryCollectionRef.Existing(id);

        // 3. Emit canonical membership operations: for each surviving/created
        //    collection in deterministic order, at most one remove and one add,
        //    with book ids sorted by (title, id). Deleted collections drop their
        //    memberships automatically when deleted.
        var beforeByCollection = InvertMemberships(before.Memberships);
        var afterByCollection = InvertMemberships(normMemberships);

        var sortedCollectionIds = normCollections.Keys
            .OrderBy(id => normCollections[id].Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => normCollections[id].Name, StringComparer.Ordinal)
            .ThenBy(id => CollectionPath(normCollections, id), StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => CollectionPath(normCollections, id), StringComparer.Ordinal)
            .ThenBy(id => createdRefById.ContainsKey(id) ? 1 : 0)
            .ThenBy(id => createdRefById.TryGetValue(id, out var r) ? r : id.ToString("N"), StringComparer.Ordinal)
            .ToList();

        var removes = new List<LibraryChangeOp>();
        var adds = new List<LibraryChangeOp>();
        foreach (var collectionId in sortedCollectionIds)
        {
            var was = beforeByCollection.GetValueOrDefault(collectionId);
            var now = afterByCollection.GetValueOrDefault(collectionId);

            if (was is { Count: > 0 })
            {
                var removed = now is null
                    ? SortBookIds(was, state.BookTitles)
                    : SortBookIds(was.Where(bookId => !now.Contains(bookId)), state.BookTitles);
                if (removed.Count > 0)
                    removes.Add(new RemoveBooksFromCollectionOp(RefOf(collectionId), removed));
            }

            if (now is { Count: > 0 })
            {
                var added = was is null
                    ? SortBookIds(now, state.BookTitles)
                    : SortBookIds(now.Where(bookId => !was.Contains(bookId)), state.BookTitles);
                if (added.Count > 0)
                    adds.Add(new AddBooksToCollectionOp(RefOf(collectionId), added));
            }
        }

        ops.AddRange(removes);
        ops.AddRange(adds);

        // 4. Emit canonical book edits: at most one UpdateBookOp per edited book
        //    carrying only the fields whose net value changed.
        foreach (var bookId in SortBookIds(after.Books.Keys, state.BookTitles))
        {
            if (!before.Books.TryGetValue(bookId, out var was))
                continue;
            if (BuildUpdateBookOp(bookId, was, after.Books[bookId]) is { } update)
                ops.Add(update);
        }

        ops.AddRange(BuildTopicKindOps(before, after, rawOps));

        var dependsOn = ComputeDependencies(ops, before.Collections, moveStepByCollection);
        return new CanonicalPlan(ops, dependsOn, deletedDescendantCounts);
    }

    private static Dictionary<Guid, Guid> MatchReusedCollections(
        IReadOnlyDictionary<Guid, CollectionState> before,
        IReadOnlyDictionary<Guid, CollectionState> after)
    {
        var deletedIds = before.Keys.Where(id => !after.ContainsKey(id)).ToList();
        var createdIds = after.Keys.Where(id => !before.ContainsKey(id)).ToList();
        var reusedMap = new Dictionary<Guid, Guid>();
        if (deletedIds.Count == 0 || createdIds.Count == 0)
            return reusedMap;

        var claimed = new HashSet<Guid>();
        var orderedCreated = createdIds
            .OrderBy(id => DepthOf(after, id))
            .ThenBy(id => after[id].Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => after[id].Name, StringComparer.Ordinal)
            .ThenBy(id => CollectionPath(after, id), StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id)
            .ToList();

        foreach (var createdId in orderedCreated)
        {
            var state = after[createdId];
            var effectiveParent = state.ParentId is { } parentId
                ? (reusedMap.TryGetValue(parentId, out var mapped) ? mapped : parentId)
                : (Guid?)null;
            var normalizedName = BookIdentityNormalizer.NormalizeTitle(state.Name);

            var match = deletedIds
                .Where(id => !claimed.Contains(id)
                    && before[id].ParentId == effectiveParent
                    && string.Equals(BookIdentityNormalizer.NormalizeTitle(before[id].Name), normalizedName, StringComparison.Ordinal))
                .OrderBy(id => id)
                .FirstOrDefault();

            if (match != Guid.Empty)
            {
                reusedMap[createdId] = match;
                claimed.Add(match);
            }
        }

        return reusedMap;
    }

    private static bool TryScheduleCollectionOps(
        IReadOnlyDictionary<Guid, CollectionState> before,
        IReadOnlyDictionary<Guid, CollectionState> target,
        out List<LibraryChangeOp> ops,
        out Dictionary<Guid, string> createdRefById,
        out Dictionary<Guid, int> moveStepByCollection,
        out Dictionary<Guid, int> deletedDescendantCounts)
    {
        ops = [];
        var refsById = new Dictionary<Guid, string>();
        createdRefById = refsById;
        moveStepByCollection = [];
        deletedDescendantCounts = [];

        var sim = new Dictionary<Guid, CollectionState>(before);
        var effectiveDeleted = before.Keys.Where(id => !target.ContainsKey(id)).ToHashSet();
        var effectiveCreated = target.Keys.Where(id => !before.ContainsKey(id)).ToHashSet();
        var deleteRoots = effectiveDeleted
            .Where(id => before[id].ParentId is not { } parentId || !effectiveDeleted.Contains(parentId))
            .ToHashSet();
        var usedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        LibraryCollectionRef RefOf(Guid id) =>
            refsById.TryGetValue(id, out var reference)
                ? LibraryCollectionRef.Created(reference)
                : LibraryCollectionRef.Existing(id);

        while (!CollectionMapsEqual(sim, target))
        {
            // 1. Delete ready roots (no surviving descendants remain underneath).
            var readyDelete = deleteRoots
                .Where(id => sim.ContainsKey(id) && DescendantsOf(sim, id).All(effectiveDeleted.Contains))
                .OrderBy(id => sim[id].Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => sim[id].Name, StringComparer.Ordinal)
                .ThenBy(id => id)
                .FirstOrDefault();
            if (readyDelete != Guid.Empty)
            {
                var descendants = DescendantsOf(sim, readyDelete);
                deletedDescendantCounts[readyDelete] = descendants.Count;
                sim.Remove(readyDelete);
                foreach (var child in descendants)
                    sim.Remove(child);
                ops.Add(new DeleteCollectionOp(LibraryCollectionRef.Existing(readyDelete)));
                continue;
            }

            // 2. Create ready new collections (parent exists and no sibling name clash).
            var readyCreate = effectiveCreated
                .Where(id => !sim.ContainsKey(id)
                    && (target[id].ParentId is not { } p || sim.ContainsKey(p))
                    && !HasSiblingCollision(sim, target[id].ParentId, target[id].Name, ignoreId: null))
                .OrderBy(id => DepthOf(target, id))
                .ThenBy(id => target[id].Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => target[id].Name, StringComparer.Ordinal)
                .ThenBy(id => CollectionPath(target, id), StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => CollectionPath(target, id), StringComparer.Ordinal)
                .FirstOrDefault();
            if (readyCreate != Guid.Empty)
            {
                var state = target[readyCreate];
                var canonicalRef = AllocateRef(state.Name, usedRefs);
                createdRefById[readyCreate] = canonicalRef;
                sim[readyCreate] = state;
                ops.Add(new CreateCollectionOp(
                    canonicalRef,
                    state.Name,
                    state.ParentId is { } parentId ? RefOf(parentId) : null));
                continue;
            }

            // 3. Move ready collections (destination exists, non-cyclic, no sibling clash).
            var readyMove = sim.Keys
                .Where(id => target.TryGetValue(id, out var t)
                    && sim[id].ParentId != t.ParentId
                    && (t.ParentId is not { } p || (sim.ContainsKey(p) && p != id && !IsDescendant(sim, p, id)))
                    && !HasSiblingCollision(sim, t.ParentId, sim[id].Name, ignoreId: id))
                .OrderBy(id => sim[id].Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => sim[id].Name, StringComparer.Ordinal)
                .ThenBy(id => id)
                .FirstOrDefault();
            if (readyMove != Guid.Empty)
            {
                var newParent = target[readyMove].ParentId;
                moveStepByCollection[readyMove] = ops.Count;
                sim[readyMove] = sim[readyMove] with { ParentId = newParent };
                ops.Add(new MoveCollectionOp(RefOf(readyMove), newParent is { } p ? RefOf(p) : null));
                continue;
            }

            // 4. Rename ready collections (no sibling clash at current parent).
            var readyRename = sim.Keys
                .Where(id => target.TryGetValue(id, out var t)
                    && !string.Equals(sim[id].Name, t.Name, StringComparison.Ordinal)
                    && !HasSiblingCollision(sim, sim[id].ParentId, t.Name, ignoreId: id))
                .OrderBy(id => sim[id].Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id => sim[id].Name, StringComparer.Ordinal)
                .ThenBy(id => id)
                .FirstOrDefault();
            if (readyRename != Guid.Empty)
            {
                var newName = target[readyRename].Name;
                sim[readyRename] = sim[readyRename] with { Name = newName };
                ops.Add(new RenameCollectionOp(RefOf(readyRename), newName));
                continue;
            }

            // Genuine name-swap or parent-swap cycle requiring a temporary step:
            // fall back cleanly to the already-validated raw sequence.
            return false;
        }

        return true;
    }

    private static CanonicalPlan FallbackToRaw(
        IReadOnlyList<LibraryChangeOp> rawOps,
        LibraryChangesetDiff.LibrarySnapshot before,
        LibraryChangesetDiff.LibrarySnapshot after)
    {
        // The collection sequence is the model's raw one because a name swap or
        // a parent cycle has no canonical order. Topic-kind operations have no
        // such cycles, so keep them canonical even here: one op per topic is
        // what lets the review card tick a topic on its own and makes the
        // client's included set agree with the server's.
        var ops = rawOps
            .Where(op => op is not SetTopicKindOp and not CreateTopicKindOp)
            .ToList();
        ops.AddRange(BuildTopicKindOps(before, after, rawOps));

        var dependsOn = ComputeDependencies(ops, before.Collections, moveStepByCollection: null);
        var deletedDescendantCounts = new Dictionary<Guid, int>();
        foreach (var op in ops)
        {
            if (op is DeleteCollectionOp { Collection.Id: { } id } && before.Collections.ContainsKey(id))
                deletedDescendantCounts[id] = DescendantsOf(before.Collections, id).Count;
        }

        return new CanonicalPlan(ops, dependsOn, deletedDescendantCounts);
    }

    private static List<IReadOnlyList<int>> ComputeDependencies(
        IReadOnlyList<LibraryChangeOp> ops,
        IReadOnlyDictionary<Guid, CollectionState> beforeCollections,
        IReadOnlyDictionary<Guid, int>? moveStepByCollection)
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

        var result = new List<IReadOnlyList<int>>(ops.Count);
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var deps = new SortedSet<int>();
            foreach (var reference in ReferencesUsedBy(op))
            {
                if (reference.Ref is { } name && createStepByRef.TryGetValue(name, out var createStep) && createStep < i)
                    deps.Add(createStep);
            }

            if (op is SetTopicKindOp { Kind.Ref: { } kindRef }
                && createKindStepByRef.TryGetValue(kindRef, out var createKindStep)
                && createKindStep < i)
            {
                deps.Add(createKindStep);
            }

            // If a surviving child was moved out of a deleted collection's subtree,
            // excluding the move must also exclude the delete so the child is never lost.
            if (moveStepByCollection is { Count: > 0 }
                && op is DeleteCollectionOp { Collection.Id: { } deletedRootId })
            {
                foreach (var (movedId, moveStep) in moveStepByCollection)
                {
                    if (moveStep < i && IsDescendant(beforeCollections, movedId, deletedRootId))
                        deps.Add(moveStep);
                }
            }

            result.Add(deps.ToList());
        }

        return result;
    }

    internal static IEnumerable<LibraryCollectionRef> ReferencesUsedBy(LibraryChangeOp op) => op switch
    {
        CreateCollectionOp { Parent: { } parent } => [parent],
        RenameCollectionOp rename => [rename.Collection],
        MoveCollectionOp { NewParent: { } parent } move => [move.Collection, parent],
        MoveCollectionOp move => [move.Collection],
        DeleteCollectionOp delete => [delete.Collection],
        SetBookCollectionsOp set => set.Collections,
        AddBooksToCollectionOp add => [add.Collection],
        RemoveBooksFromCollectionOp remove => [remove.Collection],
        _ => [],
    };

    /// <summary>
    /// The canonical topic-kind operations for a before/after pair. A topic is
    /// named only when a raw operation allowed it — either its
    /// <see cref="SetTopicKindOp.NamedTopicIds"/> contains it or, when that set
    /// is absent, the op overwrites reader kinds. Every other topic gets a
    /// non-null, empty named set, which is what tells the apply to protect a
    /// reader kind even if the reader sets one after staging.
    /// </summary>
    internal static List<LibraryChangeOp> BuildTopicKindOps(
        LibraryChangesetDiff.LibrarySnapshot before,
        LibraryChangesetDiff.LibrarySnapshot after,
        IReadOnlyList<LibraryChangeOp> rawOps)
    {
        var ops = new List<LibraryChangeOp>();
        var createdRefById = new Dictionary<Guid, string>();
        var usedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namedByRaw = NamedTopicIds(rawOps);

        var createdKinds = after.TopicKinds
            .Where(pair => !before.TopicKinds.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Value.SortOrder)
            .ThenBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Value.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key)
            .ToList();
        foreach (var (id, kind) in createdKinds)
        {
            var reference = AllocateRef($"kind:{kind.Name}", usedRefs);
            createdRefById[id] = reference;
            ops.Add(new CreateTopicKindOp(reference, kind.Name, kind.ColorKey));
        }

        var assignments = before.TopicKindAssignments.Keys
            .Union(after.TopicKindAssignments.Keys)
            .Where(topicId => before.TopicKindAssignments.GetValueOrDefault(topicId)
                != after.TopicKindAssignments.GetValueOrDefault(topicId))
            .OrderBy(topicId => KindName(after, after.TopicKindAssignments.GetValueOrDefault(topicId)?.KindId) ?? "\uFFFF",
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(topicId => after.TopicKindAssignments.GetValueOrDefault(topicId)?.TopicName
                ?? before.TopicKindAssignments.GetValueOrDefault(topicId)?.TopicName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(topicId => topicId)
            .ToList();

        foreach (var topicId in assignments)
        {
            var now = after.TopicKindAssignments.GetValueOrDefault(topicId);
            TopicKindRef? kind = null;
            if (now?.KindId is { } kindId)
            {
                kind = createdRefById.TryGetValue(kindId, out var reference)
                    ? TopicKindRef.Created(reference)
                    : TopicKindRef.Existing(kindId);
            }

            // Only a topic a raw operation allowed may have a reader kind
            // replaced. Every other topic carries a non-null, empty named set,
            // which is how the apply always protects it, even if the reader
            // sets a kind after this plan was staged.
            IReadOnlyList<Guid> named = namedByRaw.Contains(topicId) ? [topicId] : [];
            ops.Add(new SetTopicKindOp([topicId], kind, NamedTopicIds: named));
        }

        return ops;
    }

    /// <summary>
    /// The topics a raw operation allowed to have a reader kind replaced. A set
    /// on the op wins: the model's verified ids are exactly what may change. A
    /// whole-batch <see cref="SetTopicKindOp.OverwriteReaderKinds"/> counts only
    /// when the op carries no set (an older caller), never as a way to widen a
    /// set the model did pass.
    /// </summary>
    private static HashSet<Guid> NamedTopicIds(IReadOnlyList<LibraryChangeOp> rawOps)
    {
        var named = new HashSet<Guid>();
        foreach (var op in rawOps.OfType<SetTopicKindOp>())
        {
            if (op.NamedTopicIds is { } ids)
                named.UnionWith(ids);
            else if (op.OverwriteReaderKinds)
                named.UnionWith(op.TopicIds);
        }

        return named;
    }

    private static string? KindName(LibraryChangesetDiff.LibrarySnapshot snapshot, Guid? kindId) =>
        kindId is { } id && snapshot.TopicKinds.TryGetValue(id, out var kind) ? kind.Name : null;

    private static UpdateBookOp? BuildUpdateBookOp(Guid bookId, BookFieldsState was, BookFieldsState now)
    {
        var title = !string.Equals(was.Title, now.Title, StringComparison.Ordinal) ? now.Title : null;
        var author = !string.Equals(was.Author, now.Author, StringComparison.Ordinal) ? (now.Author ?? string.Empty) : null;
        var description = !string.Equals(was.Description, now.Description, StringComparison.Ordinal) ? (now.Description ?? string.Empty) : null;
        var rating = was.Rating != now.Rating ? now.Rating : (int?)null;
        var isFavorite = was.IsFavorite != now.IsFavorite ? now.IsFavorite : (bool?)null;
        var personalReview = !string.Equals(was.PersonalReview, now.PersonalReview, StringComparison.Ordinal) ? (now.PersonalReview ?? string.Empty) : null;
        var isFinished = (was.FinishedAt is not null) != (now.FinishedAt is not null) ? (now.FinishedAt is not null) : (bool?)null;
        var editionRating = was.EditionRating != now.EditionRating ? (now.EditionRating ?? 0) : (int?)null;

        if (title is null && author is null && description is null && rating is null
            && isFavorite is null && personalReview is null && isFinished is null && editionRating is null)
        {
            return null;
        }

        return new UpdateBookOp(bookId, title, author, description, rating, isFavorite, personalReview, isFinished, editionRating);
    }

    private static Dictionary<Guid, HashSet<Guid>> InvertMemberships(IReadOnlyDictionary<Guid, HashSet<Guid>> byBook)
    {
        var byCollection = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var (bookId, collections) in byBook)
        {
            foreach (var collectionId in collections)
            {
                if (!byCollection.TryGetValue(collectionId, out var set))
                {
                    set = [];
                    byCollection[collectionId] = set;
                }
                set.Add(bookId);
            }
        }
        return byCollection;
    }

    private static List<Guid> SortBookIds(IEnumerable<Guid> bookIds, IReadOnlyDictionary<Guid, string> titles) =>
        bookIds
            .Distinct()
            .OrderBy(id => titles.GetValueOrDefault(id) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => titles.GetValueOrDefault(id) ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(id => id)
            .ToList();

    private static string AllocateRef(string name, HashSet<string> usedRefs)
    {
        if (usedRefs.Add(name))
            return name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name}#{suffix}";
            if (usedRefs.Add(candidate))
                return candidate;
        }
    }

    private static bool CollectionMapsEqual(
        IReadOnlyDictionary<Guid, CollectionState> a,
        IReadOnlyDictionary<Guid, CollectionState> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (id, state) in a)
        {
            if (!b.TryGetValue(id, out var other) || state != other)
                return false;
        }
        return true;
    }

    private static bool HasSiblingCollision(
        IReadOnlyDictionary<Guid, CollectionState> collections,
        Guid? parentId,
        string name,
        Guid? ignoreId)
    {
        var normalized = BookIdentityNormalizer.NormalizeTitle(name);
        foreach (var (id, state) in collections)
        {
            if (id != ignoreId
                && state.ParentId == parentId
                && string.Equals(BookIdentityNormalizer.NormalizeTitle(state.Name), normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static int DepthOf(IReadOnlyDictionary<Guid, CollectionState> collections, Guid id)
    {
        var depth = 0;
        var seen = new HashSet<Guid> { id };
        var current = id;
        while (collections.TryGetValue(current, out var state)
            && state.ParentId is { } parentId
            && seen.Add(parentId))
        {
            depth++;
            current = parentId;
        }
        return depth;
    }

    private static string CollectionPath(IReadOnlyDictionary<Guid, CollectionState> collections, Guid id)
    {
        var parts = new List<string>();
        var seen = new HashSet<Guid>();
        var current = id;
        while (seen.Add(current) && collections.TryGetValue(current, out var state))
        {
            parts.Add(state.Name);
            if (state.ParentId is not { } parentId)
                break;
            current = parentId;
        }
        parts.Reverse();
        return string.Join("\0", parts);
    }

    private static List<Guid> DescendantsOf(IReadOnlyDictionary<Guid, CollectionState> collections, Guid rootId)
    {
        var result = new List<Guid>();
        var frontier = new HashSet<Guid> { rootId };
        while (frontier.Count > 0)
        {
            var next = new HashSet<Guid>();
            foreach (var (id, state) in collections)
            {
                if (state.ParentId is { } p && frontier.Contains(p))
                {
                    result.Add(id);
                    next.Add(id);
                }
            }
            frontier = next;
        }
        return result;
    }

    private static bool IsDescendant(
        IReadOnlyDictionary<Guid, CollectionState> collections,
        Guid candidate,
        Guid ancestor)
    {
        var seen = new HashSet<Guid> { candidate };
        var current = candidate;
        while (collections.TryGetValue(current, out var state)
            && state.ParentId is { } parentId
            && seen.Add(parentId))
        {
            if (parentId == ancestor)
                return true;
            current = parentId;
        }
        return false;
    }
}

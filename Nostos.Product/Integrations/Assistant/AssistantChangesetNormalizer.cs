using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Normalises a turn's staged library operations so equivalent regroup plans
/// collapse to the same minimal steps (issue #1740):
/// - reusing an existing collection instead of deleting and recreating it with
///   the same name and parent, or issuing a redundant create for one that
///   already exists;
/// - cancelling remove-then-re-add pairs produced by regrouping;
/// - pruning no-op memberships, duplicate deletes, and redundant steps;
/// - merging split add/remove/update operations targeting the same collection
///   or book.
///
/// Operations naming an unknown book or collection are left intact so the
/// canonical changeset preview still fails with its exact error code.
/// </summary>
internal static class AssistantChangesetNormalizer
{
    public sealed record NormalizationResult(
        IReadOnlyList<LibraryChangeOp> Ops,
        IReadOnlyDictionary<string, LibraryCollectionRef> RefAliases);

    public static async Task<NormalizationResult> NormalizeAsync(
        IReadOnlyList<LibraryChangeOp> rawOps,
        IReadOnlyDictionary<Guid, CollectionDto> initialCollections,
        Func<Guid, CancellationToken, Task<BookDto?>> getBookAsync,
        Func<Guid, CancellationToken, Task<IReadOnlyList<Guid>>> getDirectMembersAsync,
        CancellationToken ct)
    {
        var (afterCollections, refAliases) = await NormalizeCollectionsAsync(
            rawOps, initialCollections, getDirectMembersAsync, ct);
        var afterCancellation = await CancelSupersededOpsAsync(
            afterCollections, initialCollections, getBookAsync, ct);
        var afterPruning = await PruneNoOpsAsync(
            afterCancellation, initialCollections, getBookAsync, ct);
        var merged = MergeAdjacentCompatibleOps(afterPruning, initialCollections);

        return new NormalizationResult(merged, refAliases);
    }

    // ------------------------------------------------------------------
    // Pass 1: Collection reuse, duplicate create/delete removal,
    //         and delete-then-recreate collapse
    // ------------------------------------------------------------------

    private static async Task<(List<LibraryChangeOp> Ops, Dictionary<string, LibraryCollectionRef> RefAliases)>
        NormalizeCollectionsAsync(
            IReadOnlyList<LibraryChangeOp> rawOps,
            IReadOnlyDictionary<Guid, CollectionDto> initialCollections,
            Func<Guid, CancellationToken, Task<IReadOnlyList<Guid>>> getDirectMembersAsync,
            CancellationToken ct)
    {
        var aliveExisting = initialCollections.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.Name, kv.Value.ParentId));
        var modifiedExisting = new HashSet<Guid>();
        var deletedExisting = new Dictionary<Guid, (string Name, Guid? ParentId)>();
        var createdAlive = new List<(string Ref, string Name, LibraryCollectionRef? Parent)>();
        var refAliases = new Dictionary<string, LibraryCollectionRef>(StringComparer.OrdinalIgnoreCase);
        var output = new List<LibraryChangeOp>(rawOps.Count);

        LibraryCollectionRef ResolveAlias(LibraryCollectionRef reference)
        {
            var current = reference;
            var guard = 0;
            while (current.Ref is { } name && refAliases.TryGetValue(name, out var mapped) && guard++ < 16)
            {
                if (mapped == current)
                    break;
                current = mapped;
            }
            return current;
        }

        LibraryChangeOp Rewrite(LibraryChangeOp op) => op switch
        {
            CreateCollectionOp create => create with
            {
                Parent = create.Parent is { } p ? ResolveAlias(p) : null,
            },
            RenameCollectionOp rename => rename with { Collection = ResolveAlias(rename.Collection) },
            MoveCollectionOp move => move with
            {
                Collection = ResolveAlias(move.Collection),
                NewParent = move.NewParent is { } p ? ResolveAlias(p) : null,
            },
            DeleteCollectionOp delete => delete with { Collection = ResolveAlias(delete.Collection) },
            SetBookCollectionsOp set => set with { Collections = set.Collections.Select(ResolveAlias).ToList() },
            AddBooksToCollectionOp add => add with { Collection = ResolveAlias(add.Collection) },
            RemoveBooksFromCollectionOp remove => remove with { Collection = ResolveAlias(remove.Collection) },
            _ => op,
        };

        foreach (var rawOp in rawOps)
        {
            var op = Rewrite(rawOp);
            switch (op)
            {
                case CreateCollectionOp create:
                {
                    var normalized = BookIdentityNormalizer.NormalizeTitle(create.Name);

                    // 1a. An existing collection with the same parent and name is still alive: reuse it.
                    if (create.Parent is null || create.Parent.Value.Id is not null)
                    {
                        var parentId = create.Parent?.Id;
                        var existing = aliveExisting.FirstOrDefault(kv =>
                            kv.Value.ParentId == parentId
                            && BookIdentityNormalizer.NormalizeTitle(kv.Value.Name) == normalized);
                        if (existing.Key != Guid.Empty)
                        {
                            refAliases[create.Ref] = LibraryCollectionRef.Existing(existing.Key);
                            continue;
                        }
                    }

                    // 1b. A new collection with the same parent and name was already staged: reuse its ref.
                    var existingCreated = createdAlive.FirstOrDefault(c =>
                        c.Parent == create.Parent
                        && BookIdentityNormalizer.NormalizeTitle(c.Name) == normalized);
                    if (existingCreated.Ref is not null)
                    {
                        refAliases[create.Ref] = LibraryCollectionRef.Created(existingCreated.Ref);
                        continue;
                    }

                    // 1c. An existing collection with the same parent and name was deleted earlier in this
                    //     plan: keep that collection instead of deleting and recreating it, preserving the
                    //     clearing of its subcollections and existing memberships (which later add/set ops
                    //     will cancel where books stay).
                    if (create.Parent is null || create.Parent.Value.Id is not null)
                    {
                        var parentId = create.Parent?.Id;
                        var deleted = deletedExisting.FirstOrDefault(kv =>
                            kv.Value.ParentId == parentId
                            && BookIdentityNormalizer.NormalizeTitle(kv.Value.Name) == normalized
                            && !modifiedExisting.Contains(kv.Key));
                        if (deleted.Key != Guid.Empty)
                        {
                            var origId = deleted.Key;
                            var delIndex = output.FindIndex(o => o is DeleteCollectionOp d && d.Collection.Id == origId);
                            if (delIndex >= 0)
                            {
                                var replacement = new List<LibraryChangeOp>();
                                foreach (var child in initialCollections.Values
                                    .Where(c => c.ParentId == origId)
                                    .OrderBy(c => c.Id))
                                {
                                    replacement.Add(new DeleteCollectionOp(LibraryCollectionRef.Existing(child.Id)));
                                }

                                var members = await getDirectMembersAsync(origId, ct);
                                if (members.Count > 0)
                                    replacement.Add(new RemoveBooksFromCollectionOp(LibraryCollectionRef.Existing(origId), members));

                                output.RemoveAt(delIndex);
                                output.InsertRange(delIndex, replacement);

                                deletedExisting.Remove(origId);
                                aliveExisting[origId] = deleted.Value;
                                refAliases[create.Ref] = LibraryCollectionRef.Existing(origId);
                                continue;
                            }
                        }
                    }

                    createdAlive.Add((create.Ref, create.Name, create.Parent));
                    output.Add(create);
                    break;
                }

                case DeleteCollectionOp delete:
                {
                    if (delete.Collection.Id is { } id)
                    {
                        if (deletedExisting.ContainsKey(id))
                            continue;

                        if (aliveExisting.ContainsKey(id))
                        {
                            var subtree = new List<Guid>();
                            var frontier = new List<Guid> { id };
                            while (frontier.Count > 0)
                            {
                                subtree.AddRange(frontier);
                                frontier = aliveExisting
                                    .Where(kv => kv.Value.ParentId is { } p && frontier.Contains(p))
                                    .Select(kv => kv.Key)
                                    .ToList();
                            }

                            foreach (var dId in subtree)
                            {
                                deletedExisting[dId] = aliveExisting[dId];
                                aliveExisting.Remove(dId);
                            }
                        }

                        output.Add(delete);
                    }
                    else if (delete.Collection.Ref is { } createdRef)
                    {
                        var removedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { createdRef };
                        bool grew;
                        do
                        {
                            grew = false;
                            foreach (var c in createdAlive)
                            {
                                if (c.Parent?.Ref is { } parentRef && removedRefs.Contains(parentRef) && removedRefs.Add(c.Ref))
                                    grew = true;
                            }
                        }
                        while (grew);

                        createdAlive.RemoveAll(c => removedRefs.Contains(c.Ref));
                        output.Add(delete);
                    }
                    break;
                }

                case RenameCollectionOp rename:
                {
                    if (rename.Collection.Id is { } id && aliveExisting.TryGetValue(id, out var col))
                    {
                        if (string.Equals(col.Name, rename.Name, StringComparison.Ordinal))
                            continue;
                        modifiedExisting.Add(id);
                        aliveExisting[id] = (rename.Name, col.ParentId);
                    }
                    output.Add(rename);
                    break;
                }

                case MoveCollectionOp move:
                {
                    if (move.Collection.Id is { } id && aliveExisting.TryGetValue(id, out var col))
                    {
                        if (move.NewParent?.Ref is null && move.NewParent?.Id == col.ParentId)
                            continue;
                        modifiedExisting.Add(id);
                        aliveExisting[id] = (col.Name, move.NewParent?.Id);
                    }
                    output.Add(move);
                    break;
                }

                default:
                    output.Add(op);
                    break;
            }
        }

        return (output, refAliases);
    }

    // ------------------------------------------------------------------
    // Pass 2a: Cancel remove-then-re-add / remove-then-set / duplicate sets
    // ------------------------------------------------------------------

    private static async Task<List<LibraryChangeOp>> CancelSupersededOpsAsync(
        List<LibraryChangeOp> ops,
        IReadOnlyDictionary<Guid, CollectionDto> initialCollections,
        Func<Guid, CancellationToken, Task<BookDto?>> getBookAsync,
        CancellationToken ct)
    {
        var createdRefs = ops.OfType<CreateCollectionOp>()
            .Select(c => c.Ref)
            .ToHashSet(StringComparer.Ordinal);

        bool IsKnownCollection(LibraryCollectionRef col) =>
            col.Id is { } id ? initialCollections.ContainsKey(id)
            : col.Ref is { } r && createdRefs.Contains(r);

        var result = new List<LibraryChangeOp>(ops.Count);
        for (var i = 0; i < ops.Count; i++)
        {
            switch (ops[i])
            {
                case RemoveBooksFromCollectionOp remove:
                {
                    var distinct = remove.BookIds.Distinct().ToList();
                    if (!IsKnownCollection(remove.Collection))
                    {
                        result.Add(remove with { BookIds = distinct });
                        continue;
                    }

                    var kept = new List<Guid>(distinct.Count);
                    foreach (var bookId in distinct)
                    {
                        if (await getBookAsync(bookId, ct) is null)
                        {
                            kept.Add(bookId);
                            continue;
                        }

                        var superseded = false;
                        for (var j = i + 1; j < ops.Count; j++)
                        {
                            if (ops[j] is AddBooksToCollectionOp add
                                && add.Collection == remove.Collection
                                && add.BookIds.Contains(bookId))
                            {
                                superseded = true;
                                break;
                            }
                            if (ops[j] is SetBookCollectionsOp set && set.BookId == bookId)
                            {
                                superseded = true;
                                break;
                            }
                            if (ops[j] is DeleteCollectionOp del
                                && CoversCollection(del.Collection, remove.Collection, initialCollections))
                            {
                                superseded = true;
                                break;
                            }
                        }

                        if (!superseded)
                            kept.Add(bookId);
                    }

                    if (kept.Count > 0)
                        result.Add(new RemoveBooksFromCollectionOp(remove.Collection, kept));
                    break;
                }

                case SetBookCollectionsOp set:
                {
                    var distinctCols = set.Collections.Distinct().ToList();
                    var allKnown = distinctCols.All(IsKnownCollection);
                    if (allKnown && await getBookAsync(set.BookId, ct) is not null)
                    {
                        var superseded = false;
                        for (var j = i + 1; j < ops.Count; j++)
                        {
                            if (ops[j] is SetBookCollectionsOp later && later.BookId == set.BookId)
                            {
                                superseded = true;
                                break;
                            }
                        }

                        if (superseded)
                            continue;
                    }

                    result.Add(new SetBookCollectionsOp(set.BookId, distinctCols));
                    break;
                }

                default:
                    result.Add(ops[i]);
                    break;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Pass 2b: Forward membership simulation to drop no-op additions,
    //          removals, and unchanged SetBookCollectionsOp steps
    // ------------------------------------------------------------------

    private static async Task<List<LibraryChangeOp>> PruneNoOpsAsync(
        List<LibraryChangeOp> ops,
        IReadOnlyDictionary<Guid, CollectionDto> initialCollections,
        Func<Guid, CancellationToken, Task<BookDto?>> getBookAsync,
        CancellationToken ct)
    {
        var createdRefs = ops.OfType<CreateCollectionOp>()
            .Select(c => c.Ref)
            .ToHashSet(StringComparer.Ordinal);

        bool IsKnownCollection(LibraryCollectionRef col) =>
            col.Id is { } id ? initialCollections.ContainsKey(id)
            : col.Ref is { } r && createdRefs.Contains(r);

        var memberships = new Dictionary<Guid, HashSet<LibraryCollectionRef>>();
        var unknownBooks = new HashSet<Guid>();

        async Task EnsureBookLoadedAsync(Guid bookId)
        {
            if (memberships.ContainsKey(bookId) || unknownBooks.Contains(bookId))
                return;
            var book = await getBookAsync(bookId, ct);
            if (book is null)
            {
                unknownBooks.Add(bookId);
                return;
            }
            memberships[bookId] = book.CollectionIds?
                .Select(LibraryCollectionRef.Existing)
                .ToHashSet() ?? [];
        }

        foreach (var op in ops)
        {
            switch (op)
            {
                case AddBooksToCollectionOp add:
                    foreach (var id in add.BookIds) await EnsureBookLoadedAsync(id);
                    break;
                case RemoveBooksFromCollectionOp remove:
                    foreach (var id in remove.BookIds) await EnsureBookLoadedAsync(id);
                    break;
                case SetBookCollectionsOp set:
                    await EnsureBookLoadedAsync(set.BookId);
                    break;
            }
        }

        var pruned = new List<LibraryChangeOp>(ops.Count);
        foreach (var op in ops)
        {
            switch (op)
            {
                case DeleteCollectionOp delete:
                {
                    foreach (var set in memberships.Values)
                        set.RemoveWhere(c => CoversCollection(delete.Collection, c, initialCollections));
                    pruned.Add(delete);
                    break;
                }

                case AddBooksToCollectionOp add:
                {
                    var distinct = add.BookIds.Distinct().ToList();
                    if (!IsKnownCollection(add.Collection))
                    {
                        pruned.Add(add with { BookIds = distinct });
                        break;
                    }

                    var kept = new List<Guid>(distinct.Count);
                    foreach (var bookId in distinct)
                    {
                        if (unknownBooks.Contains(bookId) || memberships[bookId].Add(add.Collection))
                            kept.Add(bookId);
                    }

                    if (kept.Count > 0)
                        pruned.Add(new AddBooksToCollectionOp(add.Collection, kept));
                    break;
                }

                case RemoveBooksFromCollectionOp remove:
                {
                    var distinct = remove.BookIds.Distinct().ToList();
                    if (!IsKnownCollection(remove.Collection))
                    {
                        pruned.Add(remove with { BookIds = distinct });
                        break;
                    }

                    var kept = new List<Guid>(distinct.Count);
                    foreach (var bookId in distinct)
                    {
                        if (unknownBooks.Contains(bookId) || memberships[bookId].Remove(remove.Collection))
                            kept.Add(bookId);
                    }

                    if (kept.Count > 0)
                        pruned.Add(new RemoveBooksFromCollectionOp(remove.Collection, kept));
                    break;
                }

                case SetBookCollectionsOp set:
                {
                    var distinctCols = set.Collections.Distinct().ToList();
                    if (unknownBooks.Contains(set.BookId) || !distinctCols.All(IsKnownCollection))
                    {
                        pruned.Add(new SetBookCollectionsOp(set.BookId, distinctCols));
                        break;
                    }

                    var target = distinctCols.ToHashSet();
                    if (!memberships[set.BookId].SetEquals(target))
                    {
                        memberships[set.BookId] = target;
                        pruned.Add(new SetBookCollectionsOp(set.BookId, distinctCols));
                    }
                    break;
                }

                default:
                    pruned.Add(op);
                    break;
            }
        }

        return pruned;
    }

    // ------------------------------------------------------------------
    // Pass 2c: Merge split add/remove/update operations
    // ------------------------------------------------------------------

    private static List<LibraryChangeOp> MergeAdjacentCompatibleOps(
        List<LibraryChangeOp> ops,
        IReadOnlyDictionary<Guid, CollectionDto> initialCollections)
    {
        var merged = new List<LibraryChangeOp>(ops.Count);
        foreach (var op in ops)
        {
            switch (op)
            {
                case AddBooksToCollectionOp add:
                {
                    var targetIndex = FindMergeableAddIndex(merged, add, initialCollections);
                    if (targetIndex >= 0 && merged[targetIndex] is AddBooksToCollectionOp prev)
                    {
                        var combined = prev.BookIds.Concat(add.BookIds).Distinct().ToList();
                        merged[targetIndex] = new AddBooksToCollectionOp(prev.Collection, combined);
                    }
                    else
                    {
                        merged.Add(add);
                    }
                    break;
                }

                case RemoveBooksFromCollectionOp remove:
                {
                    var targetIndex = FindMergeableRemoveIndex(merged, remove, initialCollections);
                    if (targetIndex >= 0 && merged[targetIndex] is RemoveBooksFromCollectionOp prev)
                    {
                        var combined = prev.BookIds.Concat(remove.BookIds).Distinct().ToList();
                        merged[targetIndex] = new RemoveBooksFromCollectionOp(prev.Collection, combined);
                    }
                    else
                    {
                        merged.Add(remove);
                    }
                    break;
                }

                case UpdateBookOp update:
                {
                    var targetIndex = merged.FindIndex(o => o is UpdateBookOp prev && prev.BookId == update.BookId);
                    if (targetIndex >= 0 && merged[targetIndex] is UpdateBookOp existing)
                    {
                        merged[targetIndex] = new UpdateBookOp(
                            existing.BookId,
                            Title: update.Title ?? existing.Title,
                            Author: update.Author ?? existing.Author,
                            Description: update.Description ?? existing.Description,
                            Rating: update.Rating ?? existing.Rating,
                            IsFavorite: update.IsFavorite ?? existing.IsFavorite,
                            PersonalReview: update.PersonalReview ?? existing.PersonalReview,
                            IsFinished: update.IsFinished ?? existing.IsFinished,
                            EditionRating: update.EditionRating ?? existing.EditionRating);
                    }
                    else
                    {
                        merged.Add(update);
                    }
                    break;
                }

                default:
                    merged.Add(op);
                    break;
            }
        }

        return merged;
    }

    private static int FindMergeableAddIndex(
        List<LibraryChangeOp> merged,
        AddBooksToCollectionOp incoming,
        IReadOnlyDictionary<Guid, CollectionDto> initialCollections)
    {
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            switch (merged[i])
            {
                case AddBooksToCollectionOp prev when prev.Collection == incoming.Collection:
                    return i;
                case RemoveBooksFromCollectionOp rem when rem.Collection == incoming.Collection:
                    return -1;
                case DeleteCollectionOp del when CoversCollection(del.Collection, incoming.Collection, initialCollections):
                    return -1;
                case SetBookCollectionsOp set when incoming.BookIds.Contains(set.BookId):
                    return -1;
            }
        }
        return -1;
    }

    private static int FindMergeableRemoveIndex(
        List<LibraryChangeOp> merged,
        RemoveBooksFromCollectionOp incoming,
        IReadOnlyDictionary<Guid, CollectionDto> initialCollections)
    {
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            switch (merged[i])
            {
                case RemoveBooksFromCollectionOp prev when prev.Collection == incoming.Collection:
                    return i;
                case AddBooksToCollectionOp add when add.Collection == incoming.Collection:
                    return -1;
                case DeleteCollectionOp del when CoversCollection(del.Collection, incoming.Collection, initialCollections):
                    return -1;
                case SetBookCollectionsOp set when incoming.BookIds.Contains(set.BookId):
                    return -1;
            }
        }
        return -1;
    }

    private static bool CoversCollection(
        LibraryCollectionRef deleted,
        LibraryCollectionRef target,
        IReadOnlyDictionary<Guid, CollectionDto> initialCollections)
    {
        if (deleted == target)
            return true;

        if (deleted.Id is not { } deletedId || target.Id is not { } targetId)
            return false;

        var cursor = targetId;
        var guard = 0;
        while (initialCollections.TryGetValue(cursor, out var dto) && dto.ParentId is { } parentId && guard++ < 64)
        {
            if (parentId == deletedId)
                return true;
            cursor = parentId;
        }

        return false;
    }
}

using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Changesets: several library changes run as one unit through the canonical
/// commands, so a preview is validated by exactly the code an apply runs, an
/// apply is all-or-nothing, and undo restores exactly the rows it replaced.
/// Extracted from <see cref="LibraryService"/> (issue #1611): it was a partial
/// class adding a second command surface to the facade.
/// </summary>
public sealed class LibraryChangesetService(
    LibraryMutationExecutor mutations,
    LibraryBookCommandService books,
    LibraryCollectionService collections) : ILibraryChangesetService
{
    /// <summary>Most operations one changeset may carry.</summary>
    public const int MaxChangesetOperations = 1000;

    /// <summary>Most book ids one changeset may name across its operations.</summary>
    public const int MaxChangesetBooks = 2000;

    public Task<LibraryChangesetResult> PreviewAsync(
        IReadOnlyList<LibraryChangeOp> ops,
        CancellationToken ct = default) =>
        RunChangesetAsync(ops, commit: false, expectedDiffHash: null, ct);

    public Task<LibraryChangesetResult> ApplyAsync(
        IReadOnlyList<LibraryChangeOp> ops,
        string? expectedDiffHash,
        CancellationToken ct = default) =>
        RunChangesetAsync(ops, commit: true, expectedDiffHash, ct);

    private async Task<LibraryChangesetResult> RunChangesetAsync(
        IReadOnlyList<LibraryChangeOp> ops,
        bool commit,
        string? expectedDiffHash,
        CancellationToken ct)
    {
        if (ValidateShape(ops) is { } invalid)
            return invalid;

        return await mutations.RunInTransactionAsync<LibraryChangesetResult>(async (db, token) =>
        {
            var editedBookIds = ops.OfType<UpdateBookOp>().Select(op => op.BookId).ToHashSet();
            var editedTopicIds = ops.OfType<SetTopicKindOp>()
                .SelectMany(op => op.TopicIds)
                .ToHashSet();
            var before = await LibraryChangesetDiff.SnapshotAsync(db, editedBookIds, editedTopicIds, token);

            var refs = new Dictionary<string, Guid>(StringComparer.Ordinal);
            for (var index = 0; index < ops.Count; index++)
            {
                var failure = await RunOperationAsync(db, ops[index], refs, token);
                db.ChangeTracker.Clear();
                if (failure is { } f)
                {
                    var version = await LibraryChangesetDiff.CurrentVersionAsync(db, token);
                    return (false, LibraryChangesetResult.Fail(f.Code, f.Message, version, index));
                }
            }

            var after = await LibraryChangesetDiff.SnapshotAsync(db, editedBookIds, editedTopicIds, token);
            var diff = await LibraryChangesetDiff.BuildAsync(db, before, after, token);
            var hash = LibraryChangesetDiff.Hash(diff);
            var stateVersion = await LibraryChangesetDiff.CurrentVersionAsync(db, token);

            if (!commit)
            {
                var touchedBookIds = before.Memberships.Keys
                    .Union(after.Memberships.Keys)
                    .Union(after.Books.Keys)
                    .ToList();
                var bookTitles = touchedBookIds.Count == 0
                    ? new Dictionary<Guid, string>()
                    : await db.Books.AsNoTracking()
                        .Where(b => touchedBookIds.Contains(b.Id))
                        .Select(b => new { b.Id, b.Title })
                        .ToDictionaryAsync(b => b.Id, b => b.Title, token);
                var previewState = new LibraryChangesetPreviewState(before, after, refs, bookTitles);
                return (false, new LibraryChangesetResult(true, null, null, null, diff, hash, before.Version, false, null)
                {
                    PreviewState = previewState,
                });
            }

            if (expectedDiffHash is not null && !string.Equals(expectedDiffHash, hash, StringComparison.Ordinal))
            {
                return (false, new LibraryChangesetResult(
                    false,
                    LibraryChangesetErrorCodes.DiffChanged,
                    "The library changed since this was reviewed, so these changes would now do something different.",
                    null, diff, hash, before.Version, false, null));
            }

            var undo = LibraryChangesetDiff.BuildUndo(before, after);
            return (true, new LibraryChangesetResult(true, null, null, null, diff, hash, stateVersion, true, undo));
        }, ct);
    }

    private static LibraryChangesetResult? ValidateShape(IReadOnlyList<LibraryChangeOp> ops)
    {
        if (ops.Count == 0)
            return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.Empty, "There are no changes to make.", "0");

        if (ops.Count > MaxChangesetOperations)
            return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.TooLarge,
                $"A single change can hold at most {MaxChangesetOperations} operations. Split it into smaller steps.", "0");

        var books = ops.Sum(op => op switch
        {
            SetBookCollectionsOp => 1,
            AddBooksToCollectionOp add => add.BookIds.Count,
            RemoveBooksFromCollectionOp remove => remove.BookIds.Count,
            UpdateBookOp => 1,
            _ => 0,
        });
        if (books > MaxChangesetBooks)
            return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.TooLarge,
                $"A single change can touch at most {MaxChangesetBooks} books. Split it into smaller steps.", "0");

        // A reference must be created before it is used, and only once.
        var created = new HashSet<string>(StringComparer.Ordinal);
        var createdKinds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < ops.Count; index++)
        {
            foreach (var used in ReferencesUsedBy(ops[index]))
            {
                if (used.Ref is { } name && !created.Contains(name))
                    return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.UnknownReference,
                        $"Operation {index + 1} names collection '{name}' before it is created.", "0", index);
            }

            foreach (var used in KindReferencesUsedBy(ops[index]))
            {
                if (used.Ref is { } name && !createdKinds.Contains(name))
                    return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.UnknownReference,
                        $"Operation {index + 1} names topic kind '{name}' before it is created.", "0", index);
            }

            if (ops[index] is CreateCollectionOp create && !created.Add(create.Ref))
                return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.DuplicateReference,
                    $"Collection reference '{create.Ref}' is created twice.", "0", index);

            if (ops[index] is CreateTopicKindOp createKind && !createdKinds.Add(createKind.Ref))
                return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.DuplicateReference,
                    $"Topic kind reference '{createKind.Ref}' is created twice.", "0", index);
        }

        return null;
    }

    private static IEnumerable<LibraryCollectionRef> ReferencesUsedBy(LibraryChangeOp op) => op switch
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

    private static IEnumerable<TopicKindRef> KindReferencesUsedBy(LibraryChangeOp op) => op switch
    {
        SetTopicKindOp { Kind: { } kind } => [kind],
        _ => [],
    };

    // ------------------------------------------------------------------
    // Operations, each through its canonical command
    // ------------------------------------------------------------------

    private readonly record struct OperationFailure(string Code, string Message);

    private async Task<OperationFailure?> RunOperationAsync(
        NostosDbContext db,
        LibraryChangeOp op,
        Dictionary<string, Guid> refs,
        CancellationToken ct)
    {
        switch (op)
        {
            case CreateCollectionOp create:
            {
                var outcome = await collections.CreateCollectionCoreAsync(db,
                    new LibraryCreateCollectionRequest(string.Empty, string.Empty, create.Name,
                        create.Parent is { } createParent ? Resolve(createParent, refs) : null), ct);
                if (AsFailure(outcome.Result) is { } failure)
                    return failure;
                // A sibling with the same name is reused, exactly as a direct create would.
                refs[create.Ref] = ((CollectionDto)outcome.Result.Data!).Id;
                return null;
            }

            case RenameCollectionOp rename:
                return AsFailure((await collections.RenameCollectionCoreAsync(db,
                    new LibraryRenameCollectionRequest(string.Empty, string.Empty,
                        Resolve(rename.Collection, refs), rename.Name), ct)).Result);

            case MoveCollectionOp move:
                return AsFailure((await collections.MoveCollectionCoreAsync(db,
                    new LibraryMoveCollectionRequest(string.Empty, string.Empty,
                        Resolve(move.Collection, refs),
                        move.NewParent is { } newParent ? Resolve(newParent, refs) : null), ct)).Result);

            case DeleteCollectionOp delete:
            {
                var rootId = Resolve(delete.Collection, refs);
                var all = await db.Collections.AsNoTracking()
                    .Select(c => new { c.Id, c.ParentId })
                    .ToListAsync(ct);
                if (all.All(c => c.Id != rootId))
                    return new OperationFailure(LibraryChangesetErrorCodes.CollectionNotFound, LibraryReplyFormatter.CollectionNotFound);

                // Deepest first, so every delete finds its collection childless.
                var subtree = new List<Guid>();
                var frontier = new List<Guid> { rootId };
                while (frontier.Count > 0)
                {
                    subtree.AddRange(frontier);
                    frontier = all.Where(c => c.ParentId is { } p && frontier.Contains(p)).Select(c => c.Id).ToList();
                }

                for (var index = subtree.Count - 1; index >= 0; index--)
                {
                    var failure = AsFailure((await collections.DeleteCollectionCoreAsync(db,
                        new LibraryDeleteCollectionRequest(string.Empty, string.Empty, subtree[index], Confirm: true), ct)).Result);
                    db.ChangeTracker.Clear();
                    if (failure is not null)
                        return failure;
                }

                return null;
            }

            case SetBookCollectionsOp set:
                return await SetMembershipAsync(db, set.BookId,
                    set.Collections.Select(c => Resolve(c, refs)).ToList(), ct);

            case AddBooksToCollectionOp add:
            {
                var collectionId = Resolve(add.Collection, refs);
                if (!await db.Collections.AsNoTracking().AnyAsync(c => c.Id == collectionId, ct))
                    return new OperationFailure(LibraryChangesetErrorCodes.CollectionNotFound, LibraryReplyFormatter.CollectionNotFound);

                foreach (var bookId in add.BookIds.Distinct())
                {
                    var current = await MembershipOfAsync(db, bookId, ct);
                    if (current is null)
                        return new OperationFailure(LibraryChangesetErrorCodes.BookNotFound, LibraryReplyFormatter.BookNotFound);
                    if (current.Contains(collectionId))
                        continue;

                    if (await SetMembershipAsync(db, bookId, [.. current, collectionId], ct) is { } failure)
                        return failure;
                    db.ChangeTracker.Clear();
                }

                return null;
            }

            case RemoveBooksFromCollectionOp remove:
            {
                var collectionId = Resolve(remove.Collection, refs);
                if (!await db.Collections.AsNoTracking().AnyAsync(c => c.Id == collectionId, ct))
                    return new OperationFailure(LibraryChangesetErrorCodes.CollectionNotFound, LibraryReplyFormatter.CollectionNotFound);

                foreach (var bookId in remove.BookIds.Distinct())
                {
                    var current = await MembershipOfAsync(db, bookId, ct);
                    if (current is null)
                        return new OperationFailure(LibraryChangesetErrorCodes.BookNotFound, LibraryReplyFormatter.BookNotFound);
                    if (!current.Contains(collectionId))
                        continue;

                    if (await SetMembershipAsync(db, bookId, current.Where(id => id != collectionId).ToList(), ct) is { } failure)
                        return failure;
                    db.ChangeTracker.Clear();
                }

                return null;
            }

            case UpdateBookOp update:
                return AsFailure((await books.UpdateBookCoreAsync(db,
                    new LibraryUpdateBookRequest(
                        ClientId: string.Empty,
                        IdempotencyKey: string.Empty,
                        BookId: update.BookId,
                        Title: update.Title,
                        Author: update.Author,
                        Description: update.Description,
                        Rating: update.Rating,
                        IsFavorite: update.IsFavorite,
                        PersonalReview: update.PersonalReview,
                        IsFinished: update.IsFinished,
                        EditionRating: update.EditionRating), ct)).Result);

            case CreateTopicKindOp createKind:
                return await CreateTopicKindAsync(db, createKind, refs, ct);

            case SetTopicKindOp setKind:
                return await SetTopicKindAsync(db, setKind, refs, ct);

            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.GetType().Name, "Unknown changeset operation.");
        }
    }

    private static async Task<OperationFailure?> CreateTopicKindAsync(
        NostosDbContext db,
        CreateTopicKindOp op,
        Dictionary<string, Guid> refs,
        CancellationToken ct)
    {
        var colorKey = op.ColorKey;
        if (string.IsNullOrWhiteSpace(colorKey))
        {
            var used = await db.TopicKinds.AsNoTracking()
                .Select(kind => kind.ColorKey)
                .ToListAsync(ct);
            colorKey = TopicKindModel.PaletteColorKeys
                .FirstOrDefault(candidate => !used.Contains(candidate, StringComparer.Ordinal));
            if (colorKey is null)
            {
                return new OperationFailure(
                    LibraryChangesetErrorCodes.TopicKindLimit,
                    $"A library can have at most {TopicKindModel.MaxKinds} topic kinds. Delete one before adding another.");
            }
        }

        // The repository is bound to the transaction's context so creation and
        // every assignment commit or roll back together.
        var repository = new TopicRepository(db);
        var outcome = await repository.CreateKindAsync(op.Name, colorKey, ct);
        if (outcome.Status != TopicKindMutationStatus.Success)
            return KindFailure(outcome.Status, outcome.ErrorMessage);

        refs[op.Ref] = outcome.Kind!.Id;
        return null;
    }

    private static async Task<OperationFailure?> SetTopicKindAsync(
        NostosDbContext db,
        SetTopicKindOp op,
        Dictionary<string, Guid> refs,
        CancellationToken ct)
    {
        if (op.TopicIds.Count == 0)
            return new OperationFailure(LibraryChangesetErrorCodes.TopicNotFound, "No topics were named.");

        var kindId = op.Kind is { } reference ? ResolveKind(reference, refs) : (Guid?)null;
        var repository = new TopicRepository(db);
        var outcome = await repository.SetTopicKindsAsync(
            op.TopicIds.Distinct().Select(topicId => new TopicKindAssignment(topicId, kindId)).ToList(),
            source: TopicKindSources.Assistant,
            overwriteReaderKinds: op.OverwriteReaderKinds,
            ignoreUnknownTopicIds: false,
            ct);

        return outcome.Status switch
        {
            SetTopicsKindStatus.Success => null,
            SetTopicsKindStatus.TopicNotFound => new OperationFailure(
                LibraryChangesetErrorCodes.TopicNotFound, "One or more topics were not found."),
            SetTopicsKindStatus.KindNotFound => new OperationFailure(
                LibraryChangesetErrorCodes.TopicKindNotFound, "Topic kind not found."),
            _ => new OperationFailure(
                "topic_kind_invalid", outcome.ErrorMessage ?? "The topic kind could not be set."),
        };
    }

    private static OperationFailure KindFailure(TopicKindMutationStatus status, string? message) => status switch
    {
        TopicKindMutationStatus.InvalidInput => new OperationFailure(
            "topic_kind_invalid", message ?? "The topic kind is not valid."),
        TopicKindMutationStatus.Conflict => new OperationFailure(
            LibraryChangesetErrorCodes.TopicKindDuplicate, message ?? "A topic kind with this name already exists."),
        TopicKindMutationStatus.LimitReached => new OperationFailure(
            LibraryChangesetErrorCodes.TopicKindLimit,
            message ?? $"A library can have at most {TopicKindModel.MaxKinds} topic kinds."),
        _ => new OperationFailure(
            LibraryChangesetErrorCodes.TopicKindNotFound, message ?? "Topic kind not found."),
    };

    private static Guid ResolveKind(TopicKindRef reference, Dictionary<string, Guid> refs) =>
        reference.Id ?? refs[reference.Ref!];

    private async Task<OperationFailure?> SetMembershipAsync(
        NostosDbContext db,
        Guid bookId,
        IReadOnlyList<Guid> collectionIds,
        CancellationToken ct) =>
        AsFailure((await books.UpdateBookCoreAsync(db,
            new LibraryUpdateBookRequest(
                ClientId: string.Empty,
                IdempotencyKey: string.Empty,
                BookId: bookId,
                CollectionIds: collectionIds), ct)).Result);

    /// <summary>The book's current collections, or null when the book does not exist.</summary>
    private static async Task<List<Guid>?> MembershipOfAsync(NostosDbContext db, Guid bookId, CancellationToken ct)
    {
        if (!await db.Books.AsNoTracking().AnyAsync(b => b.Id == bookId, ct))
            return null;

        return await db.BookCollections.AsNoTracking()
            .Where(bc => bc.BookId == bookId)
            .Select(bc => bc.CollectionId)
            .ToListAsync(ct);
    }

    private static Guid Resolve(LibraryCollectionRef reference, Dictionary<string, Guid> refs) =>
        reference.Id ?? refs[reference.Ref!];

    private static OperationFailure? AsFailure(LibraryCommandResultDto result) => result.Data switch
    {
        LibraryConfirmationErrorDto error => new OperationFailure(error.Code, result.Reply),
        LibraryErrorDto error => new OperationFailure(error.Code, result.Reply),
        _ => null,
    };

    // ------------------------------------------------------------------
    // Undo
    // ------------------------------------------------------------------

    public Task<LibraryRevertResult> RevertAsync(LibraryUndoSnapshot undo, CancellationToken ct = default) =>
        mutations.RunInTransactionAsync<LibraryRevertResult>(async (db, token) =>
        {
            var conflicts = new List<string>();
            var restored = 0;

            // 0. Topic kinds: put every topic's kind and source back, then remove
            //    the kinds the changeset created once nothing uses them. Restoring
            //    first means a created kind is empty by the time it is removed.
            var assignedTopicIds = undo.TopicKindAssignments.Select(s => s.TopicId).ToList();
            if (assignedTopicIds.Count > 0)
            {
                var topics = await db.Topics
                    .Where(t => assignedTopicIds.Contains(t.Id))
                    .ToDictionaryAsync(t => t.Id, token);
                foreach (var snapshot in undo.TopicKindAssignments)
                {
                    if (!topics.TryGetValue(snapshot.TopicId, out var topic))
                        continue;

                    if (topic.KindId != snapshot.After.KindId
                        || !string.Equals(topic.KindSource, snapshot.After.Source, StringComparison.Ordinal))
                    {
                        conflicts.Add($"Kept the kind of \"{snapshot.TopicName}\": it was changed after this was applied.");
                        continue;
                    }

                    topic.KindId = snapshot.Before.KindId;
                    topic.KindSource = snapshot.Before.Source;
                    restored++;
                }

                await db.SaveChangesAsync(token);
            }

            foreach (var snapshot in undo.TopicKinds.Where(s => s.Before is null && s.After is not null))
            {
                var kind = await db.TopicKinds.FirstOrDefaultAsync(k => k.Id == snapshot.Id, token);
                if (kind is null)
                    continue;

                if (kind.Name != snapshot.After!.Name
                    || kind.ColorKey != snapshot.After.ColorKey
                    || kind.SortOrder != snapshot.After.SortOrder)
                {
                    conflicts.Add($"Kept the kind \"{kind.Name}\": it was changed after this was applied.");
                    continue;
                }

                if (await db.Topics.AnyAsync(t => t.KindId == snapshot.Id, token))
                    continue;

                db.TopicKinds.Remove(kind);
                restored++;
            }

            await db.SaveChangesAsync(token);

            var current = await db.Collections.ToDictionaryAsync(c => c.Id, token);

            bool SiblingNameTaken(Guid self, Guid? parentId, string name, HashSet<Guid>? ignored = null)
            {
                var normalized = BookIdentityNormalizer.NormalizeTitle(name);
                return current.Values.Any(c =>
                    c.Id != self
                    && (ignored is null || !ignored.Contains(c.Id))
                    && c.ParentId == parentId
                    && BookIdentityNormalizer.NormalizeTitle(c.Name) == normalized);
            }

            // 1. Detach memberships for books untouched since apply. Removing rows
            //    that point at collections the changeset created frees those
            //    collections to be deleted before deleted collections with the
            //    same name are restored.
            var bookIds = undo.Memberships.Select(m => m.BookId).ToList();
            var existingBooks = await db.Books.AsNoTracking()
                .Where(b => bookIds.Contains(b.Id))
                .Select(b => new { b.Id, b.Title })
                .ToDictionaryAsync(b => b.Id, b => b.Title, token);
            var rows = await db.BookCollections
                .Where(bc => bookIds.Contains(bc.BookId))
                .ToListAsync(token);

            var revertibleMemberships = new List<MembershipSnapshot>();
            foreach (var snapshot in undo.Memberships)
            {
                if (!existingBooks.TryGetValue(snapshot.BookId, out var title))
                    continue;

                var mine = rows.Where(r => r.BookId == snapshot.BookId).ToList();
                if (!mine.Select(r => r.CollectionId).ToHashSet().SetEquals(snapshot.After))
                {
                    conflicts.Add($"Kept the collections of \"{title}\": they were changed after this was applied.");
                    continue;
                }

                db.BookCollections.RemoveRange(mine);
                revertibleMemberships.Add(snapshot);
            }

            if (revertibleMemberships.Count > 0)
                await db.SaveChangesAsync(token);

            // Created collections that must stay because a book that cannot get all of
            // its "before" collections back still needs them (see the end of step 2).
            var keptForBooks = new Dictionary<Guid, int>();
            var conflictsBeforeTree = conflicts.Count;
            var restoredBeforeTree = restored;
            bool strandedBooks;
            do
            {
                strandedBooks = false;

                // 2. Restore the collection tree: remove empty collections the changeset
                //    created (deepest first), return renamed/moved collections, and
                //    bring back deleted collections under their original id (parents
                //    before children). Iterating to a fixed point lets a deleted
                //    collection reclaim its name once the changeset's replacement is
                //    gone, and lets a collection moved under a newly created parent
                //    move back so that parent can be removed.
                var created = new HashSet<Guid>();
                foreach (var snapshot in undo.Collections.Where(s => s.Before is null && s.After is not null))
                {
                    if (!current.TryGetValue(snapshot.Id, out var model))
                        continue;

                    if (model.Name != snapshot.After!.Name || model.ParentId != snapshot.After.ParentId)
                    {
                        conflicts.Add($"Kept \"{model.Name}\": it was changed after this was applied.");
                        continue;
                    }

                    created.Add(snapshot.Id);
                }
                var createdAtStart = created.ToHashSet();
                var postApplyCounts = createdAtStart.Count == 0
                    ? new Dictionary<Guid, int>()
                    : await db.BookCollections.AsNoTracking()
                        .Where(bc => createdAtStart.Contains(bc.CollectionId))
                        .GroupBy(bc => bc.CollectionId)
                        .Select(g => new { g.Key, Count = g.Count() })
                        .ToDictionaryAsync(g => g.Key, g => g.Count, token);
                var memberCounts = new Dictionary<Guid, int>(postApplyCounts);
                foreach (var (keptId, keptCount) in keptForBooks)
                    memberCounts[keptId] = memberCounts.GetValueOrDefault(keptId) + keptCount;

                var deletedPending = undo.Collections
                    .Where(s => s.Before is not null && s.After is null && !current.ContainsKey(s.Id))
                    .ToList();

                var updatedPending = new List<CollectionSnapshot>();
                foreach (var snapshot in undo.Collections.Where(s => s.Before is not null && s.After is not null))
                {
                    if (!current.TryGetValue(snapshot.Id, out var model)
                        || model.Name != snapshot.After!.Name
                        || model.ParentId != snapshot.After.ParentId)
                    {
                        conflicts.Add($"Kept \"{snapshot.After!.Name}\" as it is: it was changed after this was applied.");
                        continue;
                    }

                    updatedPending.Add(snapshot);
                }

                var updatedById = updatedPending.ToDictionary(s => s.Id);
                var restoredDeleted = new List<CollectionSnapshot>();
                var restoredUpdated = new List<CollectionSnapshot>();

                bool CanClearCreatedSubtreeForDeleted(Guid createdRootId, Guid restoringDeletedId)
                {
                    var deletedTree = new HashSet<Guid> { restoringDeletedId };
                    bool grew;
                    do
                    {
                        grew = false;
                        foreach (var d in deletedPending)
                        {
                            if (d.Before!.ParentId is { } parent && deletedTree.Contains(parent) && deletedTree.Add(d.Id))
                                grew = true;
                        }
                    }
                    while (grew);

                    var stack = new Stack<Guid>();
                    stack.Push(createdRootId);
                    while (stack.Count > 0)
                    {
                        var id = stack.Pop();
                        if (created.Contains(id))
                        {
                            if (memberCounts.GetValueOrDefault(id) > 0)
                                return false;

                            foreach (var child in current.Values.Where(c => c.ParentId == id))
                                stack.Push(child.Id);
                        }
                        else if (updatedById.TryGetValue(id, out var moved))
                        {
                            if (moved.Before!.ParentId is not { } targetParent || !deletedTree.Contains(targetParent))
                                return false;
                        }
                        else
                        {
                            return false;
                        }
                    }

                    return true;
                }

                bool progressed;
                do
                {
                    progressed = false;

                    // 2a. Remove created collections that are now childless and empty.
                    foreach (var id in created.ToList())
                    {
                        if (memberCounts.GetValueOrDefault(id) > 0 || current.Values.Any(c => c.ParentId == id))
                            continue;

                        created.Remove(id);
                        db.Collections.Remove(current[id]);
                        current.Remove(id);
                        restored++;
                        progressed = true;
                    }

                    // 2b. Revert renamed or moved collections whose target slot is open.
                    foreach (var snapshot in updatedPending.ToList())
                    {
                        var state = snapshot.Before!;
                        if ((state.ParentId is { } parent && !current.ContainsKey(parent))
                            || SiblingNameTaken(snapshot.Id, state.ParentId, state.Name))
                            continue;

                        var model = current[snapshot.Id];
                        model.Name = state.Name;
                        model.ParentId = state.ParentId;
                        updatedPending.Remove(snapshot);
                        updatedById.Remove(snapshot.Id);
                        restoredUpdated.Add(snapshot);
                        restored++;
                        progressed = true;
                    }

                    // 2c. Restore deleted collections whose parent exists and name slot is open.
                    foreach (var snapshot in deletedPending.ToList())
                    {
                        var state = snapshot.Before!;
                        if ((state.ParentId is { } parent && !current.ContainsKey(parent))
                            || SiblingNameTaken(snapshot.Id, state.ParentId, state.Name))
                            continue;

                        var model = new CollectionModel { Id = snapshot.Id, Name = state.Name, ParentId = state.ParentId };
                        db.Collections.Add(model);
                        current[model.Id] = model;
                        deletedPending.Remove(snapshot);
                        restoredDeleted.Add(snapshot);
                        restored++;
                        progressed = true;
                    }

                    if (progressed)
                        continue;

                    // 2d. Break a rename/move swap cycle among updated collections (e.g. A <-> B).
                    var swapCandidates = updatedPending
                        .Where(s => s.Before!.ParentId is null || current.ContainsKey(s.Before.ParentId.Value))
                        .ToList();
                    var swapIds = swapCandidates.Select(s => s.Id).ToHashSet();
                    bool pruned;
                    do
                    {
                        pruned = false;
                        foreach (var s in swapCandidates.ToList())
                        {
                            if (SiblingNameTaken(s.Id, s.Before!.ParentId, s.Before.Name, swapIds))
                            {
                                swapCandidates.Remove(s);
                                swapIds.Remove(s.Id);
                                pruned = true;
                            }
                        }
                    }
                    while (pruned && swapCandidates.Count > 0);

                    if (swapCandidates.Count > 0)
                    {
                        foreach (var snapshot in swapCandidates)
                        {
                            var model = current[snapshot.Id];
                            model.Name = snapshot.Before!.Name;
                            model.ParentId = snapshot.Before.ParentId;
                            updatedPending.Remove(snapshot);
                            updatedById.Remove(snapshot.Id);
                            restoredUpdated.Add(snapshot);
                            restored++;
                        }
                        progressed = true;
                        continue;
                    }

                    // 2e. Break a delete + recreate-same-name + reparented-child cycle:
                    //     restore the deleted parent first when every clashing sibling is
                    //     a removable created collection whose subtree will vacate into it.
                    foreach (var snapshot in deletedPending.ToList())
                    {
                        var state = snapshot.Before!;
                        if (state.ParentId is { } parent && !current.ContainsKey(parent))
                            continue;

                        var normalized = BookIdentityNormalizer.NormalizeTitle(state.Name);
                        var clashers = current.Values
                            .Where(c => c.Id != snapshot.Id && c.ParentId == state.ParentId
                                && BookIdentityNormalizer.NormalizeTitle(c.Name) == normalized)
                            .ToList();

                        if (clashers.Count > 0
                            && clashers.All(c => created.Contains(c.Id) && CanClearCreatedSubtreeForDeleted(c.Id, snapshot.Id)))
                        {
                            var model = new CollectionModel { Id = snapshot.Id, Name = state.Name, ParentId = state.ParentId };
                            db.Collections.Add(model);
                            current[model.Id] = model;
                            deletedPending.Remove(snapshot);
                            restoredDeleted.Add(snapshot);
                            restored++;
                            progressed = true;
                            break;
                        }
                    }
                }
                while (progressed);

                // Guard against any edge case (e.g. step 2e) that left a restored
                // collection alongside a same-name sibling under the same parent.
                bool removedClash;
                do
                {
                    removedClash = false;
                    foreach (var snapshot in restoredDeleted.ToList())
                    {
                        if (!current.TryGetValue(snapshot.Id, out var model)
                            || !SiblingNameTaken(snapshot.Id, model.ParentId, model.Name)
                            || current.Values.Any(c => c.ParentId == snapshot.Id))
                            continue;

                        db.Collections.Remove(model);
                        current.Remove(snapshot.Id);
                        restoredDeleted.Remove(snapshot);
                        restored--;
                        conflicts.Add($"Did not restore \"{model.Name}\": another collection there now has that name.");
                        removedClash = true;
                    }
                }
                while (removedClash);

                foreach (var snapshot in restoredDeleted)
                {
                    if (current.TryGetValue(snapshot.Id, out var model)
                        && SiblingNameTaken(snapshot.Id, model.ParentId, model.Name))
                    {
                        conflicts.Add($"Kept \"{model.Name}\" alongside another collection with the same name.");
                    }
                }

                foreach (var snapshot in restoredUpdated)
                {
                    if (current.TryGetValue(snapshot.Id, out var model)
                        && SiblingNameTaken(snapshot.Id, model.ParentId, model.Name))
                    {
                        conflicts.Add($"Kept \"{model.Name}\" alongside another collection with the same name.");
                    }
                }

                foreach (var snapshot in deletedPending)
                {
                    var state = snapshot.Before!;
                    conflicts.Add(state.ParentId is { } parent && !current.ContainsKey(parent)
                        ? $"Did not restore \"{state.Name}\": its parent collection no longer exists."
                        : $"Did not restore \"{state.Name}\": another collection there now has that name.");
                }

                foreach (var snapshot in updatedPending)
                    conflicts.Add($"Kept \"{current[snapshot.Id].Name}\" as it is: it cannot go back to \"{snapshot.Before!.Name}\" without a clash.");

                foreach (var id in created)
                {
                    conflicts.Add(current.Values.Any(c => c.ParentId == id)
                        ? $"Kept \"{current[id].Name}\": it now holds other collections."
                        : postApplyCounts.GetValueOrDefault(id) > 0
                            ? $"Kept \"{current[id].Name}\": books were added to it after this was applied."
                            : $"Kept \"{current[id].Name}\": a book in it could not go back to its previous collections.");
                }

                // A book that cannot get every "before" collection back keeps its
                // "after" collections (step 3). Those may include a created collection
                // that step 2a just removed as empty, which would strand the book. Work
                // out which ones, undo this pass, and run it again with them kept.
                foreach (var snapshot in revertibleMemberships)
                {
                    if (snapshot.Before.All(current.ContainsKey))
                        continue;

                    foreach (var collectionId in snapshot.After.Where(cid => createdAtStart.Contains(cid) && !current.ContainsKey(cid)))
                    {
                        keptForBooks[collectionId] = keptForBooks.GetValueOrDefault(collectionId) + 1;
                        strandedBooks = true;
                    }
                }

                if (strandedBooks)
                {
                    db.ChangeTracker.Clear();
                    current = await db.Collections.ToDictionaryAsync(c => c.Id, token);
                    conflicts.RemoveRange(conflictsBeforeTree, conflicts.Count - conflictsBeforeTree);
                    restored = restoredBeforeTree;
                }
            }
            while (strandedBooks);

            await db.SaveChangesAsync(token);

            // 3. Re-attach the original memberships now that restored collections exist.
            //    If any of a book's "before" collections is not present, keep/restore its
            //    still-existing "after" memberships instead of stranding the book, and
            //    report a conflict for that book.
            foreach (var snapshot in revertibleMemberships)
            {
                if (snapshot.Before.All(current.ContainsKey))
                {
                    foreach (var collectionId in snapshot.Before)
                        db.BookCollections.Add(new BookCollectionModel { BookId = snapshot.BookId, CollectionId = collectionId });
                    restored++;
                }
                else
                {
                    foreach (var collectionId in snapshot.After.Where(current.ContainsKey))
                        db.BookCollections.Add(new BookCollectionModel { BookId = snapshot.BookId, CollectionId = collectionId });
                    conflicts.Add($"Kept the collections of \"{existingBooks[snapshot.BookId]}\": one of its previous collections could not be restored.");
                }
            }

            // 4. Book fields return to what they were, where nobody changed them since.
            var editedIds = undo.Books.Select(b => b.BookId).ToList();
            var edited = await db.Books.Where(b => editedIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, token);
            foreach (var snapshot in undo.Books)
            {
                if (!edited.TryGetValue(snapshot.BookId, out var book))
                    continue;

                var now = new BookFieldsState(book.Title, book.Author, book.Metadata.Description,
                    book.Work!.Rating, book.Work.IsFavorite, book.Work.PersonalReview,
                    book.Progress.FinishedAt, book.Progress.ProgressPercent,
                    snapshot.After.EditionRating is null ? null : book.Progress.Rating);
                // Rating, favourite and review are the book's, so two editions of one
                // work share them: when an earlier snapshot of this same undo already
                // put them back, that is not an edit made after the changeset.
                var bookValuesNow = (now.Rating, now.IsFavorite, now.PersonalReview);
                var bookValuesOk = bookValuesNow == (snapshot.After.Rating, snapshot.After.IsFavorite, snapshot.After.PersonalReview)
                    || bookValuesNow == (snapshot.Before.Rating, snapshot.Before.IsFavorite, snapshot.Before.PersonalReview);
                if (!bookValuesOk || now with { Rating = snapshot.After.Rating, IsFavorite = snapshot.After.IsFavorite, PersonalReview = snapshot.After.PersonalReview } != snapshot.After)
                {
                    conflicts.Add($"Kept \"{book.Title}\" as it is: it was edited after this was applied.");
                    continue;
                }

                var was = snapshot.Before;
                book.Title = was.Title;
                book.Author = was.Author;
                book.Metadata.Description = was.Description;
                book.Work.Rating = was.Rating;
                book.Work.IsFavorite = was.IsFavorite;
                book.Work.PersonalReview = was.PersonalReview;
                book.Progress.FinishedAt = was.FinishedAt;
                book.Progress.ProgressPercent = was.ProgressPercent;
                if (was.EditionRating is { } editionRating) book.Progress.Rating = editionRating;
                restored++;
            }

            await db.SaveChangesAsync(token);
            var version = await LibraryChangesetDiff.CurrentVersionAsync(db, token);
            return (true, new LibraryRevertResult(true, restored, conflicts, version));
        }, ct);
}

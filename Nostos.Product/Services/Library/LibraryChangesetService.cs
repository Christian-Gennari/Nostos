using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
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
            var before = await LibraryChangesetDiff.SnapshotAsync(db, editedBookIds, token);

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

            var after = await LibraryChangesetDiff.SnapshotAsync(db, editedBookIds, token);
            var diff = await LibraryChangesetDiff.BuildAsync(db, before, after, token);
            var hash = LibraryChangesetDiff.Hash(diff);
            var stateVersion = await LibraryChangesetDiff.CurrentVersionAsync(db, token);

            if (!commit)
                return (false, new LibraryChangesetResult(true, null, null, null, diff, hash, before.Version, false, null));

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
        for (var index = 0; index < ops.Count; index++)
        {
            foreach (var used in ReferencesUsedBy(ops[index]))
            {
                if (used.Ref is { } name && !created.Contains(name))
                    return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.UnknownReference,
                        $"Operation {index + 1} names collection '{name}' before it is created.", "0", index);
            }

            if (ops[index] is CreateCollectionOp create && !created.Add(create.Ref))
                return LibraryChangesetResult.Fail(LibraryChangesetErrorCodes.DuplicateReference,
                    $"Collection reference '{create.Ref}' is created twice.", "0", index);
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

            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.GetType().Name, "Unknown changeset operation.");
        }
    }

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

            var current = await db.Collections.ToDictionaryAsync(c => c.Id, token);
            bool SiblingNameTaken(Guid self, Guid? parentId, string name)
            {
                var normalized = BookIdentityNormalizer.NormalizeTitle(name);
                return current.Values.Any(c => c.Id != self && c.ParentId == parentId
                    && BookIdentityNormalizer.NormalizeTitle(c.Name) == normalized);
            }

            // 1. Collections the changeset deleted come back under their old id,
            //    parents before children.
            var pending = undo.Collections.Where(s => s.Before is not null && s.After is null).ToList();
            bool progressed;
            do
            {
                progressed = false;
                foreach (var snapshot in pending.ToList())
                {
                    var state = snapshot.Before!;
                    if (current.ContainsKey(snapshot.Id))
                    {
                        pending.Remove(snapshot);
                        continue;
                    }
                    if (state.ParentId is { } parent && !current.ContainsKey(parent))
                        continue;

                    pending.Remove(snapshot);
                    progressed = true;
                    if (SiblingNameTaken(snapshot.Id, state.ParentId, state.Name))
                    {
                        conflicts.Add($"Did not restore \"{state.Name}\": another collection there now has that name.");
                        continue;
                    }

                    var model = new CollectionModel { Id = snapshot.Id, Name = state.Name, ParentId = state.ParentId };
                    db.Collections.Add(model);
                    current[model.Id] = model;
                    restored++;
                }
            }
            while (progressed && pending.Count > 0);

            foreach (var snapshot in pending)
                conflicts.Add($"Did not restore \"{snapshot.Before!.Name}\": its parent collection no longer exists.");

            // 2. Renamed or moved collections go back where they were.
            foreach (var snapshot in undo.Collections.Where(s => s.Before is not null && s.After is not null))
            {
                if (!current.TryGetValue(snapshot.Id, out var model)
                    || model.Name != snapshot.After!.Name
                    || model.ParentId != snapshot.After.ParentId)
                {
                    conflicts.Add($"Kept \"{snapshot.After!.Name}\" as it is: it was changed after this was applied.");
                    continue;
                }

                var state = snapshot.Before!;
                if ((state.ParentId is { } parent && !current.ContainsKey(parent))
                    || SiblingNameTaken(snapshot.Id, state.ParentId, state.Name))
                {
                    conflicts.Add($"Kept \"{model.Name}\" as it is: it cannot go back to \"{state.Name}\" without a clash.");
                    continue;
                }

                model.Name = state.Name;
                model.ParentId = state.ParentId;
                restored++;
            }

            await db.SaveChangesAsync(token);

            // 3. Memberships return to what they were, where nobody changed them since.
            var bookIds = undo.Memberships.Select(m => m.BookId).ToList();
            var existingBooks = await db.Books.AsNoTracking()
                .Where(b => bookIds.Contains(b.Id))
                .Select(b => new { b.Id, b.Title })
                .ToDictionaryAsync(b => b.Id, b => b.Title, token);
            var rows = await db.BookCollections
                .Where(bc => bookIds.Contains(bc.BookId))
                .ToListAsync(token);

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
                foreach (var collectionId in snapshot.Before.Where(current.ContainsKey))
                    db.BookCollections.Add(new BookCollectionModel { BookId = snapshot.BookId, CollectionId = collectionId });
                restored++;
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

            // 5. Collections the changeset created go, deepest first, once empty.
            var created = undo.Collections
                .Where(s => s.Before is null && s.After is not null && current.ContainsKey(s.Id))
                .Select(s => s.Id)
                .ToHashSet();
            var memberCounts = await db.BookCollections.AsNoTracking()
                .Where(bc => created.Contains(bc.CollectionId))
                .GroupBy(bc => bc.CollectionId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, token);

            do
            {
                progressed = false;
                foreach (var id in created.ToList())
                {
                    if (current.Values.Any(c => c.ParentId == id))
                        continue;

                    created.Remove(id);
                    progressed = true;
                    if (memberCounts.GetValueOrDefault(id) > 0)
                    {
                        conflicts.Add($"Kept \"{current[id].Name}\": books were added to it after this was applied.");
                        continue;
                    }

                    db.Collections.Remove(current[id]);
                    current.Remove(id);
                    restored++;
                }
            }
            while (progressed && created.Count > 0);

            foreach (var id in created)
                conflicts.Add($"Kept \"{current[id].Name}\": it now holds other collections.");

            await db.SaveChangesAsync(token);
            var version = await LibraryChangesetDiff.CurrentVersionAsync(db, token);
            return (true, new LibraryRevertResult(true, restored, conflicts, version));
        }, ct);
}

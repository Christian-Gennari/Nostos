using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Collection create/rename/move/update/delete (issue #1611), including the
/// hierarchy rules: no self or descendant parent, and no normalized sibling-name
/// collision at the final destination. Membership rows are the only record of
/// which books a collection holds, so deleting a collection removes them first
/// and never touches a book.
/// </summary>
public sealed class LibraryCollectionService(LibraryMutationExecutor mutations)
{
    public Task<LibraryCommandResultDto> CreateAsync(
        LibraryCreateCollectionRequest request,
        CancellationToken ct = default) =>
        mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "CreateCollection",
            (db, token) => CreateCollectionCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> RenameAsync(
        LibraryRenameCollectionRequest request,
        CancellationToken ct = default) =>
        mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "RenameCollection",
            (db, token) => RenameCollectionCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> MoveAsync(
        LibraryMoveCollectionRequest request,
        CancellationToken ct = default) =>
        mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "MoveCollection",
            (db, token) => MoveCollectionCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> UpdateAsync(
        string clientId,
        string idempotencyKey,
        Guid collectionId,
        string name,
        Guid? parentId,
        CancellationToken ct = default) =>
        mutations.ExecuteAsync(clientId, idempotencyKey, "UpdateCollection",
            (db, token) => UpdateCollectionCoreAsync(db, collectionId, name, parentId,
                treatUnchangedAsNoOp: true, token), ct);

    public Task<LibraryCommandResultDto> DeleteAsync(
        LibraryDeleteCollectionRequest request,
        CancellationToken ct = default) =>
        mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "DeleteCollection",
            (db, token) => DeleteCollectionCoreAsync(db, request, token), ct);

    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> CreateCollectionCoreAsync(
        NostosDbContext db,
        LibraryCreateCollectionRequest request,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        if (string.IsNullOrWhiteSpace(request.Name))
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_collection_name",
                "Collection name is required.", state.StateVersion));

        if (request.ParentId.HasValue)
        {
            var parentExists = await db.Collections.AsNoTracking()
                .AnyAsync(c => c.Id == request.ParentId.Value, ct);
            if (!parentExists)
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_collection_parent",
                    LibraryReplyFormatter.CollectionParentNotFound, state.StateVersion));
        }

        // Duplicate sibling name: return the existing collection.
        var nName = BookIdentityNormalizer.NormalizeTitle(request.Name);
        var existing = await db.Collections.AsNoTracking()
            .Where(c => c.ParentId == request.ParentId)
            .ToListAsync(ct);
        var duplicate = existing.FirstOrDefault(c =>
            string.Equals(BookIdentityNormalizer.NormalizeTitle(c.Name), nName, StringComparison.Ordinal));
        if (duplicate is not null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Result(
                LibraryReplyFormatter.CollectionExists(duplicate.Name),
                new CollectionDto(duplicate.Id, duplicate.Name, duplicate.ParentId),
                state.StateVersion));

        var model = new CollectionModel { Name = request.Name.Trim(), ParentId = request.ParentId };
        db.Collections.Add(model);
        await db.SaveChangesAsync(ct);

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.CollectionCreated(model.Name),
            new CollectionDto(model.Id, model.Name, model.ParentId),
            state.StateVersion));
    }

    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> RenameCollectionCoreAsync(
        NostosDbContext db,
        LibraryRenameCollectionRequest request,
        CancellationToken ct)
    {
        // Preserve the historical check order (invalid name wins over
        // collection_not_found); the shared core validates the name after
        // loading the collection.
        var state = await LibraryStateGuard.EnsureAsync(db, ct);
        if (string.IsNullOrWhiteSpace(request.Name))
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_collection_name",
                "Collection name is required.", state.StateVersion));

        var outcome = await UpdateCollectionCoreAsync(db, request.CollectionId, request.Name,
            Optional<Guid?>.None, treatUnchangedAsNoOp: false, ct);
        if (!outcome.DidChange)
            return outcome;
        return LibraryCommandResults.Change(outcome.Result with
        {
            Reply = LibraryReplyFormatter.CollectionRenamed(((CollectionDto)outcome.Result.Data!).Name),
        });
    }

    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> MoveCollectionCoreAsync(
        NostosDbContext db,
        LibraryMoveCollectionRequest request,
        CancellationToken ct)
    {
        var outcome = await UpdateCollectionCoreAsync(db, request.CollectionId, Optional<string>.None,
            request.NewParentId, treatUnchangedAsNoOp: false, ct);
        if (!outcome.DidChange)
            return outcome;
        return LibraryCommandResults.Change(outcome.Result with
        {
            Reply = LibraryReplyFormatter.CollectionMoved(((CollectionDto)outcome.Result.Data!).Name),
        });
    }

    /// <summary>
    /// Shared atomic rename/move core (collections Phase 1). Runs inside one
    /// MutateAsync transaction: loads the collection, validates the proposed
    /// name, rejects invalid/self/descendant parents, checks the normalized
    /// final name against the final parent's siblings, then assigns both
    /// values with a single SaveChanges. When
    /// <paramref name="treatUnchangedAsNoOp"/> is set (combined update), a
    /// request whose name and parent are both unchanged succeeds without a
    /// stateVersion bump; the separate rename/move ops keep their
    /// historical always-bump-on-success behavior.
    /// </summary>
    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> UpdateCollectionCoreAsync(
        NostosDbContext db,
        Guid collectionId,
        Optional<string> name,
        Optional<Guid?> parentId,
        bool treatUnchangedAsNoOp,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_not_found",
                LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

        if (name.HasValue && string.IsNullOrWhiteSpace(name.Value))
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_collection_name",
                "Collection name is required.", state.StateVersion));

        var finalName = name.HasValue ? name.Value.Trim() : collection.Name;
        var finalParentId = parentId.HasValue ? parentId.Value : collection.ParentId;

        if (finalParentId.HasValue)
        {
            if (finalParentId.Value == collection.Id)
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_cycle",
                    LibraryReplyFormatter.CollectionCycle, state.StateVersion));

            var parentExists = await db.Collections.AsNoTracking()
                .AnyAsync(c => c.Id == finalParentId.Value, ct);
            if (!parentExists)
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_collection_parent",
                    LibraryReplyFormatter.CollectionParentNotFound, state.StateVersion));

            // Ancestor walk: moving under one of our own descendants cycles.
            var cursor = finalParentId.Value;
            var visited = new HashSet<Guid>();
            while (cursor != Guid.Empty)
            {
                if (cursor == collection.Id)
                    return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_cycle",
                        LibraryReplyFormatter.CollectionCycle, state.StateVersion));
                if (!visited.Add(cursor))
                    return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_cycle",
                        LibraryReplyFormatter.CollectionCycle, state.StateVersion));

                var parent = await db.Collections.AsNoTracking()
                    .Where(c => c.Id == cursor)
                    .Select(c => c.ParentId)
                    .SingleOrDefaultAsync(ct);
                if (parent is null)
                    break;
                cursor = parent.Value;
            }
        }

        // Sibling-name collision at the FINAL destination: the normalized
        // final name must not collide with any OTHER sibling there (the
        // root level is a sibling set like any other, parentId = null).
        var nName = BookIdentityNormalizer.NormalizeTitle(finalName);
        var siblings = await db.Collections.AsNoTracking()
            .Where(c => c.Id != collection.Id && c.ParentId == finalParentId)
            .ToListAsync(ct);
        var collision = siblings.FirstOrDefault(c =>
            string.Equals(BookIdentityNormalizer.NormalizeTitle(c.Name), nName, StringComparison.Ordinal));
        if (collision is not null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_name_conflict",
                LibraryReplyFormatter.CollectionNameConflict(collision.Name), state.StateVersion));

        var dto = new CollectionDto(collection.Id, collection.Name, collection.ParentId);

        // No-op policy (combined update only): same name and same parent is
        // a successful no-op — one receipt is still written, but the
        // stateVersion is not bumped.
        if (treatUnchangedAsNoOp &&
            string.Equals(collection.Name, finalName, StringComparison.Ordinal) &&
            collection.ParentId == finalParentId)
        {
            return LibraryCommandResults.NoChange(LibraryCommandResults.Result(
                LibraryReplyFormatter.CollectionUpdated(finalName),
                dto, state.StateVersion));
        }

        collection.Name = finalName;
        collection.ParentId = finalParentId;
        await db.SaveChangesAsync(ct);

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.CollectionUpdated(collection.Name),
            new CollectionDto(collection.Id, collection.Name, collection.ParentId),
            state.StateVersion));
    }

    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> DeleteCollectionCoreAsync(
        NostosDbContext db,
        LibraryDeleteCollectionRequest request,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        if (!request.Confirm)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("confirmation_required",
                LibraryReplyFormatter.DeleteConfirmationRequired, state.StateVersion));

        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == request.CollectionId, ct);
        if (collection is null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_not_found",
                LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

        // Children must be moved/deleted first; never cascade silently.
        var childCount = await db.Collections.CountAsync(c => c.ParentId == collection.Id, ct);
        if (childCount > 0)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_has_children",
                LibraryReplyFormatter.CollectionHasChildren, state.StateVersion));

        var bookCount = await db.BookCollections
            .CountAsync(bc => bc.CollectionId == collection.Id, ct);

        // Membership rows go first: their collection FK is RESTRICT, so leaving
        // them would block the delete. Books themselves survive untouched.
        // The revision row is advanced FIRST, before the portable rows, so every
        // portable writer takes the singleton lock in the same order. The
        // CollectionModel delete below would also advance it, and the
        // per-transaction marker keeps that to exactly one advance.
        await LibraryRevision.AdvanceAndGetAsync(db, ct);
        await db.BookCollections
            .Where(bc => bc.CollectionId == collection.Id)
            .ExecuteDeleteAsync(ct);

        db.Collections.Remove(collection);
        await db.SaveChangesAsync(ct);

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.CollectionDeleted(collection.Name),
            new LibraryDeleteCollectionResultDto(collection.Id, bookCount, 0),
            state.StateVersion));
    }

    // Tri-state argument for the shared rename/move core: HasValue=false
    // means "leave the field unchanged" (distinct from an explicit null).
    internal readonly record struct Optional<T>(T Value, bool HasValue)
    {
        public static implicit operator Optional<T>(T value) => new(value, true);

        public static Optional<T> None => default;
    }
}

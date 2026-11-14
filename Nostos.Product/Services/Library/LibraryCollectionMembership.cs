using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// The only record of a book's collection membership is <c>BookCollections</c>
/// (issue #1611, AGENTS.md "Traps found the hard way"): the singular
/// <c>collectionId</c> on an update replaces every membership, so both the set
/// and the legacy singular shape are translated here into the same replacement,
/// and the two can never disagree.
/// </summary>
internal static class LibraryCollectionMembership
{
    /// <summary>True when every named collection exists; an empty set trivially does.</summary>
    public static async Task<bool> AllExistAsync(
        NostosDbContext db,
        IReadOnlyList<Guid> collectionIds,
        CancellationToken ct)
    {
        if (collectionIds.Count == 0)
            return true;

        var known = await db.Collections.AsNoTracking()
            .Where(c => collectionIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);
        return known.Count == collectionIds.Count;
    }

    /// <summary>
    /// Replaces the book's memberships with exactly <paramref name="collectionIds"/>.
    /// Returns false, having changed nothing, when one of them does not exist.
    /// Rows that are already correct are left alone.
    /// </summary>
    public static async Task<bool> ReplaceAsync(
        NostosDbContext db,
        Guid bookId,
        IReadOnlyList<Guid> collectionIds,
        CancellationToken ct)
    {
        var wanted = collectionIds.Distinct().ToList();
        if (!await AllExistAsync(db, wanted, ct))
            return false;

        var current = await db.BookCollections
            .Where(bc => bc.BookId == bookId)
            .ToListAsync(ct);

        db.BookCollections.RemoveRange(current.Where(bc => !wanted.Contains(bc.CollectionId)));
        foreach (var collectionId in wanted.Where(id => current.All(bc => bc.CollectionId != id)))
            db.BookCollections.Add(new BookCollectionModel
            {
                BookId = bookId,
                CollectionId = collectionId,
            });

        return true;
    }

    /// <summary>Adds join rows for a book that was just created.</summary>
    public static void Add(
        NostosDbContext db,
        Guid bookId,
        IEnumerable<Guid> collectionIds,
        DateTime addedAt)
    {
        foreach (var collectionId in collectionIds)
            db.BookCollections.Add(new BookCollectionModel
            {
                BookId = bookId,
                CollectionId = collectionId,
                AddedAt = addedAt,
            });
    }
}

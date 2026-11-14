using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// The cleanup every importer's undo shares (issue #1625). Calibre, reading
/// history and highlights each carried their own copy of it, and the copies had
/// already drifted apart: one judged a created book by its reading progress, the
/// other two by "physical, no file, no notes".
///
/// There is one rule now. A book an import added goes with it only when nothing
/// has come to it since: no note, no reading progress, and no file other than
/// the one the import itself wrote (or none at all, for an import that brings no
/// files). Everything it deletes goes through <see cref="ILibraryService"/>, so
/// the delete takes the library gate, the revision advance and the change feed
/// with every other write.
/// </summary>
internal static class ImportBatchUndo
{
    /// <summary>One committed book-import batch, as the import screens list it.</summary>
    internal sealed record BatchListing(
        Guid Id,
        string Source,
        string? FileName,
        DateTime CreatedAtUtc,
        int BookCount,
        int ChangeCount);

    /// <summary>
    /// A book an import created. <paramref name="FileEntityTag"/> is the tag of
    /// the file the import wrote for it, when it wrote one: the only way to know
    /// a file still there is that same file.
    /// </summary>
    internal readonly record struct CreatedBook(Guid BookId, string? FileEntityTag = null);

    /// <summary>
    /// Book-import batches, newest first, for one source (every source when
    /// <paramref name="source"/> is null). Both importers used to carry their
    /// own copy of this query.
    /// </summary>
    public static async Task<IReadOnlyList<BatchListing>> ListBatchesAsync(
        NostosDbContext db,
        string? source,
        int take,
        CancellationToken ct)
    {
        var query = db.BookImportBatches
            .AsNoTracking()
            .Where(batch => batch.Books.Count > 0);
        if (source is not null)
            query = query.Where(batch => batch.Source == source);

        var rows = await query
            .OrderByDescending(batch => batch.CreatedAtUtc)
            .Take(take)
            .Select(batch => new
            {
                batch.Id,
                batch.Source,
                batch.FileName,
                batch.CreatedAtUtc,
                batch.ChangeCount,
                BookCount = batch.Books.Count,
            })
            .ToListAsync(ct);

        return rows
            .Select(batch => new BatchListing(
                batch.Id, batch.Source, batch.FileName, batch.CreatedAtUtc, batch.BookCount, batch.ChangeCount))
            .ToList();
    }

    /// <summary>
    /// Deletes the collections an import created that nothing holds any more: a
    /// book another import or the owner has put in one keeps it, and so does a
    /// collection with children, exactly as a direct delete refuses one.
    /// </summary>
    public static async Task RemoveCreatedCollectionsAsync(
        NostosDbContext db,
        ILibraryService library,
        string clientId,
        IEnumerable<Guid> createdCollectionIds,
        CancellationToken ct)
    {
        var created = createdCollectionIds.Distinct().ToList();
        if (created.Count == 0)
            return;

        var empty = await db.Collections
            .AsNoTracking()
            .Where(collection => created.Contains(collection.Id))
            .Where(collection => !db.BookCollections.Any(membership => membership.CollectionId == collection.Id))
            .Where(collection => !db.Collections.Any(child => child.ParentId == collection.Id))
            .Select(collection => collection.Id)
            .ToListAsync(ct);

        foreach (var collectionId in empty)
        {
            await library.DeleteCollectionAsync(
                new LibraryDeleteCollectionRequest(
                    clientId,
                    $"{clientId}-{Guid.NewGuid():N}",
                    collectionId,
                    Confirm: true),
                ct);
        }
    }

    /// <summary>
    /// Deletes the books an import added that nothing has come to since, and
    /// answers how many had to be kept. <paramref name="beforeDelete"/> lets an
    /// importer drop its own rows that point at the book first, because a foreign
    /// key would otherwise refuse the delete.
    /// </summary>
    public static async Task<int> RemoveCreatedBooksAsync(
        NostosDbContext db,
        ILibraryService library,
        IBookAssetStorage? storage,
        IReadOnlyCollection<CreatedBook> created,
        CancellationToken ct,
        Func<NostosDbContext, Guid, CancellationToken, Task>? beforeDelete = null)
    {
        var kept = 0;
        foreach (var candidate in created.DistinctBy(book => book.BookId))
        {
            var book = await db.Books
                .AsNoTracking()
                .Where(entry => entry.Id == candidate.BookId)
                .Select(entry => new
                {
                    entry.FileDetails.HasFile,
                    entry.FileDetails.FileName,
                    entry.Progress.ProgressPercent,
                    entry.Progress.LastLocation,
                    entry.Progress.LastReadAt,
                    entry.Progress.FinishedAt,
                    HasNotes = db.Notes.Any(note => note.BookId == candidate.BookId),
                })
                .FirstOrDefaultAsync(ct);
            if (book is null)
                continue;

            if (book.HasNotes
                || book.ProgressPercent > 0
                || book.LastLocation is not null
                || book.LastReadAt is not null
                || book.FinishedAt is not null)
            {
                kept++;
                continue;
            }

            // The file, when there is one: either the import recorded the file it
            // wrote and this is still exactly that file, or the import wrote none
            // (reading history, highlights) and the book has none either.
            var fileIsTheImports = candidate.FileEntityTag is { } entityTag
                ? storage is not null
                    && string.Equals(
                        (await storage.GetBookFileInfoAsync(candidate.BookId, ct))?.EntityTag,
                        entityTag,
                        StringComparison.Ordinal)
                : !book.HasFile && string.IsNullOrEmpty(book.FileName);
            if (!fileIsTheImports)
            {
                kept++;
                continue;
            }

            if (beforeDelete is not null)
                await beforeDelete(db, candidate.BookId, ct);

            var deleted = await library.DeleteBookAsync(candidate.BookId, ct);
            if (deleted.Data is null)
            {
                kept++;
                continue;
            }

            if (storage is not null)
                await storage.DeleteBookFilesAsync(candidate.BookId, ct);
        }

        return kept;
    }
}

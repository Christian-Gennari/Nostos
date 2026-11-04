using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// Reads and removes the persisted external references of a book. Nothing is
/// cached: a restore or a library switch replaces the database underneath a
/// running server, and a remembered reference would then name another
/// library's file.
/// </summary>
public sealed class ExternalMediaLinkStore(IDbContextFactory<NostosDbContext> contexts)
{
    public async Task<ExternalMediaLinkModel?> FindAsync(Guid bookId, int trackNumber, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ExternalMediaLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.BookId == bookId && l.TrackNumber == trackNumber, ct);
    }

    /// <summary>Forgets the book's primary-file reference. The file is untouched.</summary>
    public Task<bool> RemovePrimaryAsync(Guid bookId, CancellationToken ct) =>
        RemoveAsync(bookId, l => l.TrackNumber == ExternalMediaLinkModel.PrimaryFile, ct);

    /// <summary>Forgets one track reference. The file is untouched.</summary>
    public Task<bool> RemoveTrackAsync(Guid bookId, int trackNumber, CancellationToken ct) =>
        RemoveAsync(bookId, l => l.TrackNumber == trackNumber, ct);

    /// <summary>Forgets every track reference of the book. The files are untouched.</summary>
    public Task<bool> RemoveTracksAsync(Guid bookId, CancellationToken ct) =>
        RemoveAsync(bookId, l => l.TrackNumber != ExternalMediaLinkModel.PrimaryFile, ct);

    /// <summary>Forgets every reference of the book. The files are untouched.</summary>
    public Task<bool> RemoveAllAsync(Guid bookId, CancellationToken ct) =>
        RemoveAsync(bookId, _ => true, ct);

    private async Task<bool> RemoveAsync(
        Guid bookId,
        System.Linq.Expressions.Expression<Func<ExternalMediaLinkModel, bool>> which,
        CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var matching = db.ExternalMediaLinks
            .Where(l => l.BookId == bookId)
            .Where(which);

        // Read first: a delete takes the database write lock even when no row
        // matches. A portable import saves media while its own write
        // transaction is open on another connection, so on SQLite that delete
        // would wait out the busy timeout and fail the whole import. A read
        // does not wait, and a book the import just created has no references.
        if (!await matching.AnyAsync(ct))
            return false;

        return await matching.ExecuteDeleteAsync(ct) > 0;
    }
}

using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// The Carrel Sync Protocol's cursors: one row per owner with a file counter
/// and a library counter, taken under a write lock so cursor order is commit
/// order.
///
/// <para>Every change takes the row's write lock first, then receives cursor
/// values, then commits. A device that stores a published cursor can therefore
/// never skip a change that commits later, and the cursor a request returns is
/// never ahead of the changes it actually returned.</para>
/// </summary>
internal static class CarrelCursors
{
    /// <summary>Creates this owner's cursor row once, outside the request's transaction.</summary>
    public static async Task EnsureAsync(
        ICarrelDeviceDirectory directory, string ownerKey, CancellationToken ct)
    {
        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        if (await db.CarrelSyncStates.AsNoTracking().AnyAsync(s => s.OwnerKey == ownerKey, ct))
            return;

        db.CarrelSyncStates.Add(new CarrelSyncStateModel { OwnerKey = ownerKey });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request created it first; the row is all that matters.
        }
    }

    /// <summary>
    /// Takes the owner's cursor row for the rest of the transaction. An update
    /// of the row locks it (SQLite serialises writers; PostgreSQL locks the
    /// row), so two changes cannot be handed cursors in one order and commit
    /// in another.
    /// </summary>
    public static async Task LockAsync(
        NostosDbContext db, string ownerKey, CancellationToken ct) =>
        await db.CarrelSyncStates
            .Where(s => s.OwnerKey == ownerKey)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAtUtc, x => x.UpdatedAtUtc), ct);

    public static async Task<long> AllocateAsync(
        NostosDbContext db, string ownerKey, int count, bool library, DateTime now, CancellationToken ct)
    {
        if (count <= 0)
            return 0;

        var row = db.CarrelSyncStates.Where(s => s.OwnerKey == ownerKey);
        if (library)
        {
            await row.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LibraryCursor, x => x.LibraryCursor + count)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                ct);
            var end = await db.CarrelSyncStates
                .AsNoTracking()
                .Where(s => s.OwnerKey == ownerKey)
                .Select(s => s.LibraryCursor)
                .SingleAsync(ct);
            return end - count;
        }

        await row.ExecuteUpdateAsync(
            s => s.SetProperty(x => x.FilesCursor, x => x.FilesCursor + count)
                .SetProperty(x => x.UpdatedAtUtc, now),
            ct);
        var filesEnd = await db.CarrelSyncStates
            .AsNoTracking()
            .Where(s => s.OwnerKey == ownerKey)
            .Select(s => s.FilesCursor)
            .SingleAsync(ct);
        return filesEnd - count;
    }

    /// <summary>The cursor of the last change for every touched file row, in one allocation.</summary>
    public static async Task StampFilesAsync(
        NostosDbContext db,
        string ownerKey,
        IReadOnlyList<CarrelFileModel> touched,
        DateTime now,
        CancellationToken ct)
    {
        if (touched.Count == 0)
            return;

        var next = await AllocateAsync(db, ownerKey, touched.Count, library: false, now, ct);
        foreach (var file in touched)
            file.Cursor = ++next;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The cursor of the last change for every touched library row, in one allocation.</summary>
    public static async Task StampLibraryAsync(
        NostosDbContext db,
        string ownerKey,
        IReadOnlyList<CarrelLibraryItemModel> touched,
        DateTime now,
        CancellationToken ct)
    {
        if (touched.Count == 0)
            return;

        var next = await AllocateAsync(db, ownerKey, touched.Count, library: true, now, ct);
        foreach (var item in touched)
            item.Cursor = ++next;
        await db.SaveChangesAsync(ct);
    }
}

using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Reads the library singleton, creating it when a database was bootstrapped
/// without the seed row. Every library write needs it before it can report (or
/// advance) the state version, so it lives next to the writers rather than in
/// one of them (issue #1611).
/// </summary>
internal static class LibraryStateGuard
{
    public static async Task<LibraryState> EnsureAsync(NostosDbContext db, CancellationToken ct)
    {
        var state = await db.LibraryStates.SingleOrDefaultAsync(ct);
        if (state is not null)
            return state;

        state = new LibraryState
        {
            Id = LibraryState.WellKnownId,
            SingletonSlot = LibraryState.SingletonSentinel,
        };
        db.LibraryStates.Add(state);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent ensure: the singleton already exists.
            db.ChangeTracker.Clear();
            state = await db.LibraryStates.SingleAsync(ct);
        }

        return state;
    }
}

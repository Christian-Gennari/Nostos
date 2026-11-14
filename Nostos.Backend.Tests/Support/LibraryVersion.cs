using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// Reads the live library revision the way activation and the change feed read
/// it: from a fresh context over the database file, after the write committed.
/// An import that writes nothing must leave it alone; every portable write must
/// advance it so open screens know to refresh.
/// </summary>
internal static class LibraryVersion
{
    public static async Task<long> ReadAsync(string databasePath)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        await using var db = new NostosDbContext(options);

        var stored = await db.LibraryStates.AsNoTracking()
            .Select(state => state.StateVersion)
            .FirstOrDefaultAsync();
        return stored is null ? 0 : long.Parse(stored);
    }
}

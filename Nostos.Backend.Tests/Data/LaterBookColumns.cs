using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// The upgrade tests for older migrations roll a disposable database back to
/// the schema before their migration and then seed it through the CURRENT
/// model. That model reads and writes every Books column, including ones a
/// later migration adds, so a rolled-back Books table is no longer something
/// the model can use. This puts those later columns back, leaving the test's
/// own migration as the only schema difference it exercises. Each later
/// migration proves its own upgrade in its own test.
/// </summary>
internal static class LaterBookColumns
{
    public static async Task RestoreAsync(DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        // AddBookTracks (issue #835).
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Books\" ADD COLUMN \"FileDetails_TracksJson\" TEXT NULL");
    }
}

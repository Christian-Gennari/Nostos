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
        // AddNoteHighlightColour (issue #861). Not a Books column, but the same
        // case: the current model writes it for every note these tests seed.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Notes\" ADD COLUMN \"HighlightColour\" TEXT NULL");
        await RestorePassageColumnsAsync(db);
        await RestoreTopicDescriptionAsync(db);
        await RestoreCoverVersionAsync(db);
        await RestoreTopicNormalizedNameAsync(db);
        await RestoreWritingPageSetupAsync(db);
    }

    /// <summary>
    /// AddWritingPageSetup (issue #1164): the export page setup the current
    /// model reads and writes on every writing.
    /// </summary>
    public static async Task RestoreWritingPageSetupAsync(NostosDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Writings\" ADD COLUMN \"PageSetup\" TEXT NULL");

    /// <summary>
    /// AddTopicNormalizedName (issue #1116): the key the current model writes
    /// on every topic it saves.
    /// </summary>
    public static async Task RestoreTopicNormalizedNameAsync(NostosDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Topics\" ADD COLUMN \"NormalizedName\" TEXT NOT NULL DEFAULT ''");

    /// <summary>
    /// AddCoverVersion: the cover cache token the current model reads and
    /// writes on every book.
    /// </summary>
    public static async Task RestoreCoverVersionAsync(NostosDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Books\" ADD COLUMN \"FileDetails_CoverVersion\" TEXT NULL");

    /// <summary>
    /// AddNotePassages (issue #1014): the passage columns the current model
    /// reads and writes on every note.
    /// </summary>
    public static async Task RestorePassageColumnsAsync(NostosDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Notes\" ADD COLUMN \"Kind\" TEXT NOT NULL DEFAULT 'note'");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Notes\" ADD COLUMN \"PassageTitle\" TEXT NULL");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Notes\" ADD COLUMN \"PassageEnd\" TEXT NULL");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Notes\" ADD COLUMN \"PassageHtml\" TEXT NULL");
    }

    /// <summary>
    /// AddTopicDescription: the reader's own words, which the current model
    /// reads and writes on every topic.
    /// </summary>
    public static async Task RestoreTopicDescriptionAsync(NostosDbContext db) =>
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Topics\" ADD COLUMN \"Description\" TEXT NULL");
}

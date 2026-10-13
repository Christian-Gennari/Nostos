using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Counts the rows of a library. Preflight, activation and recovery all report
/// these numbers to the owner and decide "empty or populated" from them, so
/// they are read in one place and cannot disagree.
/// </summary>
public static class LibraryRowCounts
{
    /// <summary>
    /// The rows that are the owner's data. The assistant settings row counts
    /// only once a capture mode has been chosen: a row holding <c>NULL</c>
    /// means "never chosen" and carries nothing an import could overwrite.
    /// </summary>
    public static Task<MigrationExistingCounts> ReadUserDataAsync(
        NostosDbContext db,
        CancellationToken ct) =>
        ReadAsync(db, onlyChosenAssistantSettings: true, ct);

    /// <summary>
    /// Every row, chosen or not. This is the second signal inside the
    /// destination revision token, where any added or removed row must change
    /// the token; it is not a statement about what belongs to the owner.
    /// </summary>
    public static Task<MigrationExistingCounts> ReadAllAsync(
        NostosDbContext db,
        CancellationToken ct) =>
        ReadAsync(db, onlyChosenAssistantSettings: false, ct);

    private static async Task<MigrationExistingCounts> ReadAsync(
        NostosDbContext db,
        bool onlyChosenAssistantSettings,
        CancellationToken ct) =>
        new(
            Works: await db.Works.CountAsync(ct),
            Books: await db.Books.CountAsync(ct),
            Notes: await db.Notes.CountAsync(ct),
            Topics: await db.Topics.CountAsync(ct),
            NoteTopics: await db.NoteTopics.CountAsync(ct),
            Writings: await db.Writings.CountAsync(ct),
            WritingNotes: await db.WritingNotes.CountAsync(ct),
            Collections: await db.Collections.CountAsync(ct),
            BookCollections: await db.BookCollections.CountAsync(ct),
            Acquisitions: await db.BookAcquisitions.CountAsync(ct),
            NoteImportBookLinks: await db.NoteImportBookLinks.CountAsync(ct),
            AssistantSettings: onlyChosenAssistantSettings
                ? await db.AssistantSettings.CountAsync(
                    settings => settings.CaptureProcessingMode != null, ct)
                : await db.AssistantSettings.CountAsync(ct),
            ReadingListEntries: await db.ReadingListEntries.CountAsync(ct));
}

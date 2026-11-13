using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.SavePipeline;

/// <summary>
/// Every write to a note moves its <see cref="NoteModel.UpdatedAt"/> (issue #1404),
/// so a second tab holding an older copy can be told apart. One hook covers every
/// path that saves a note: edit, topic link, location, restore, import.
/// </summary>
internal static class NoteUpdateStamper
{
    public static void Stamp(ChangeTracker changeTracker)
    {
        DateTime? now = null;
        foreach (var entry in changeTracker.Entries<NoteModel>())
        {
            // Edits only: a note that is added (made, imported, restored) is
            // versioned by its CreatedAt until its first edit, so a rebuilt
            // library stays byte-identical to a clean build.
            if (entry.State is not EntityState.Modified)
                continue;

            // Strictly after the version it replaces (to the millisecond the
            // conflict check compares at), so two quick saves never share one.
            var note = entry.Entity;
            var stamp = now ??= DateTime.UtcNow;
            var previous = note.UpdatedAt ?? note.CreatedAt;
            note.UpdatedAt = stamp > previous.AddMilliseconds(1) ? stamp : previous.AddMilliseconds(1);
        }
    }
}

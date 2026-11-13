using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.SavePipeline;

/// <summary>
/// A physical book has no file: it is metadata-only (#332). This is the one
/// place the rule is enforced for writes, so a code path that forgets it
/// fails loudly here instead of creating a row that shows paper controls
/// beside a working Read button.
///
/// Only the save that writes the file state is refused: a new physical book
/// that arrives with one, or a physical row whose file fields are being
/// changed. A row that already carried a file because it predates #332 — the
/// migration converts those, and leaves a genuinely ambiguous one physical —
/// can still have its title or rating saved.
/// </summary>
internal static class PhysicalBookFileGuard
{
    public static void EnsurePhysicalBooksHaveNoFile(ChangeTracker changeTracker)
    {
        // A book whose file fields this save writes: EF marks the owned
        // FileInfoDetails entry Modified and leaves its owner Unchanged when only
        // the file changed.
        var fileEntries = changeTracker.Entries<FileInfoDetails>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .Select(entry => entry.Entity)
            .ToHashSet(ReferenceEqualityComparer.Instance);

        PhysicalBookModel? offender = null;
        foreach (var entry in changeTracker.Entries<PhysicalBookModel>())
        {
            if (entry.State is EntityState.Deleted or EntityState.Detached)
                continue;

            var book = entry.Entity;
            if (!book.FileDetails.HasFile)
                continue;
            if (entry.State is not EntityState.Added && !fileEntries.Contains(book.FileDetails))
            {
                // A row that already carried a file (the migration leaves a
                // genuinely ambiguous one physical) can still have its title,
                // rating or notes saved: only writing the file is refused.
                continue;
            }

            offender = book;
            break;
        }

        if (offender is null)
            return;

        throw new InvalidOperationException(
            $"Book {offender.Id} (\"{offender.Title}\") is physical and carries a file " +
            $"({offender.FileDetails.FileName ?? "unnamed"}). A physical book is metadata-only: " +
            "store the file on the digital edition it belongs to.");
    }
}

using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Single read seam for the opaque destination revision bound into migration
/// preflight and persisted on a job at creation. Activation (#681) compares the
/// stored revision with the current one to detect an intervening portable write.
/// </summary>
public interface ILibraryDestinationRevisionProvider
{
    /// <summary>Returns the current opaque revision token for the live library.</summary>
    Task<string> GetCurrentAsync(CancellationToken ct);
}

/// <summary>
/// Current SelfHosted implementation: composes the singleton
/// <see cref="LibraryState.StateVersion"/> with the portable row counts, the
/// same token <see cref="SelfHostedMigrationPreflightService"/> returns to the
/// client.
/// </summary>
/// <remarks>
/// <b>Known limitation.</b> The underlying <see cref="LibraryState.StateVersion"/>
/// currently advances only for mutations that travel through
/// <c>LibraryMutationExecutor</c>; several portable mutation paths (for example
/// direct note writes) can change portable state without bumping it. The counts
/// catch row additions/removals but not content-only edits on such paths. A
/// dedicated follow-up slice must make one monotonic generation advance on every
/// portable mutation (plan section 9, "Destination revision not authoritative");
/// until then this provider is the only place that reads the token and activation
/// must not treat it as a complete portable-state generation.
/// </remarks>
public sealed class LibraryStateDestinationRevisionProvider(NostosDbContext db)
    : ILibraryDestinationRevisionProvider
{
    public async Task<string> GetCurrentAsync(CancellationToken ct)
    {
        var counts = new MigrationExistingCounts(
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
            AssistantSettings: await db.AssistantSettings.CountAsync(ct));

        var stateVersion = await db.LibraryStates.AsNoTracking()
            .Where(s => s.Id == LibraryState.WellKnownId)
            .Select(s => s.StateVersion)
            .SingleOrDefaultAsync(ct);

        return MigrationDestinationRevision.Compute(stateVersion ?? "0", counts);
    }
}

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
/// <see cref="LibraryState.StateVersion"/> is advanced by
/// <see cref="Data.LibraryRevision"/> for every committed create, update or
/// delete of portable user-owned state — including content-only edits, owned
/// values and bulk operations — exactly once per transaction, so this token
/// changes on every portable mutation. The counts remain part of the token as
/// a second signal for row additions/removals.
/// </remarks>
public sealed class LibraryStateDestinationRevisionProvider(NostosDbContext db)
    : ILibraryDestinationRevisionProvider
{
    public async Task<string> GetCurrentAsync(CancellationToken ct)
    {
        var counts = await LibraryRowCounts.ReadAllAsync(db, ct);

        var stateVersion = await db.LibraryStates.AsNoTracking()
            .Where(s => s.Id == LibraryState.WellKnownId)
            .Select(s => s.StateVersion)
            .SingleOrDefaultAsync(ct);

        return MigrationDestinationRevision.Compute(stateVersion ?? "0", counts);
    }
}

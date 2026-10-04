using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Migration;

// All filesystem-changing operations first lock the owning job row with a
// conditional UPDATE. SQLite uses BEGIN IMMEDIATE (no read-to-write upgrade);
// other providers lock that row on UPDATE. Session/receipt/accounting changes
// commit in the same transaction. Local locks are never the correctness boundary.
internal static class MigrationMutation
{
    internal static async Task<IDbContextTransaction> BeginAsync(NostosDbContext db, CancellationToken ct)
    {
        // Microsoft.Data.Sqlite defaults to deferred:false; EF owns and disposes
        // the native transaction. Avoid UseTransaction with an unowned handle.
        return await db.Database.BeginTransactionAsync(ct);
    }

    internal static IQueryable<MigrationJobRecord> Active(NostosDbContext db, Guid jobId, DateTime now) =>
        db.MigrationJobRecords.Where(j => j.Id == jobId && j.ExpiresAtUtc > now
            && j.State >= (int)MigrationJobState.Pending && j.State <= (int)MigrationJobState.Validating);

    internal static async Task LockUploadJobAsync(NostosDbContext db, Guid jobId, DateTime now, CancellationToken ct)
    {
        if (await Active(db, jobId, now).Where(j => j.Direction == (int)MigrationDirection.Import)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Version, j => j.Version + 1), ct) != 1)
            throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
    }

    internal static IQueryable<MigrationJobRecord> Leased(NostosDbContext db, Guid jobId, string token, DateTime now) =>
        Active(db, jobId, now).Where(j => j.MigrationLeaseToken == token && j.LeaseExpiresAtUtc > now);

    internal static async Task LockLeaseAsync(NostosDbContext db, Guid jobId, string token, DateTime now, CancellationToken ct)
    {
        if (await Leased(db, jobId, token, now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Version, j => j.Version + 1), ct) != 1)
            throw MigrationJobStoreException.LeaseConflict(jobId);
    }
}

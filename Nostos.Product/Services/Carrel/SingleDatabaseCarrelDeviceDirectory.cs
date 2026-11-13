using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// The directory of a host with one database, which is every SelfHosted
/// install: pairings and the owner's data share it, and the token index is the
/// device table itself, so nothing is stored twice.
///
/// <para>The owner key it answers for a token is the one stored on the device
/// row, which is what the code did before this seam existed.</para>
/// </summary>
public sealed class SingleDatabaseCarrelDeviceDirectory(IDbContextFactory<NostosDbContext> contexts)
    : ICarrelDeviceDirectory
{
    public async ValueTask<NostosDbContext> OpenPairingsAsync(CancellationToken ct) =>
        await contexts.CreateDbContextAsync(ct);

    public async ValueTask<NostosDbContext> OpenOwnerAsync(string ownerKey, CancellationToken ct) =>
        await contexts.CreateDbContextAsync(ct);

    public async ValueTask<string?> FindOwnerAsync(string tokenHash, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.CarrelDevices.AsNoTracking()
            .Where(d => d.TokenHash == tokenHash)
            .Select(d => d.OwnerKey)
            .FirstOrDefaultAsync(ct);
    }

    public ValueTask RegisterDeviceAsync(string ownerKey, Guid deviceId, string tokenHash, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask UnregisterDeviceAsync(string ownerKey, Guid deviceId, CancellationToken ct) =>
        ValueTask.CompletedTask;
}

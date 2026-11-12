using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// Where the Carrel Sync Protocol keeps its rows, and how a request that has
/// no signed-in person finds them.
///
/// <para>The device routes are reached before any owner is known: a device
/// asks for a pairing code anonymously, and every later request carries only a
/// device token. A host with one database never notices (see
/// <see cref="SingleDatabaseCarrelDeviceDirectory"/>), but a host that keeps
/// each owner's data apart needs somewhere that is not owner-specific to put a
/// pairing code and to look a token up. This is that seam. It names three
/// kinds of place:</para>
/// <list type="bullet">
/// <item><b>Pairings.</b> The rows of <c>CarrelPairings</c>, written by an
/// anonymous device and read by the person approving its code. They hold the
/// device's name and public key, never a library.</item>
/// <item><b>Owner.</b> One owner's devices, synced files, library cursors and
/// the drafts and notes they map to. Everything the protocol serves comes from
/// here, and only for the owner key that names it.</item>
/// <item><b>Token index.</b> Which owner a device token belongs to. It carries
/// only the token's SHA-256, never the token.</item>
/// </list>
///
/// <para>Isolation does not rest on the index alone: after it names an owner,
/// <see cref="CarrelPairingService.AuthenticateAsync"/> still requires that the
/// owner's own database holds a device row with the same hash, so a wrong or
/// stale index entry can only make a request fail, never reach another
/// owner's data. Revoking a device deletes that row first, so the token stops
/// working at once even if the index entry lingers.</para>
/// </summary>
public interface ICarrelDeviceDirectory
{
    /// <summary>
    /// The database that holds pairing rows. The caller disposes the context.
    /// </summary>
    ValueTask<NostosDbContext> OpenPairingsAsync(CancellationToken ct);

    /// <summary>
    /// The database that holds everything of one owner. The caller disposes
    /// the context. Throws <see cref="CarrelOwnerUnavailableException"/> when
    /// that owner's database cannot be used (an account being deleted, or not
    /// ready): a device request then fails as unauthorized instead of leaking
    /// why.
    /// </summary>
    ValueTask<NostosDbContext> OpenOwnerAsync(string ownerKey, CancellationToken ct);

    /// <summary>
    /// The owner a device token (given as its lowercase hex SHA-256) was
    /// issued to, or null for an unknown token. This only routes; it does not
    /// prove the token.
    /// </summary>
    ValueTask<string?> FindOwnerAsync(string tokenHash, CancellationToken ct);

    /// <summary>
    /// Records that a device now exists for an owner, after its row was
    /// written to the owner's database.
    /// </summary>
    ValueTask RegisterDeviceAsync(string ownerKey, Guid deviceId, string tokenHash, CancellationToken ct);

    /// <summary>
    /// Forgets a device after its row was deleted from the owner's database.
    /// </summary>
    ValueTask UnregisterDeviceAsync(string ownerKey, Guid deviceId, CancellationToken ct);
}

/// <summary>An owner's database is not available to serve a Carrel request.</summary>
public sealed class CarrelOwnerUnavailableException : Exception
{
    public CarrelOwnerUnavailableException()
        : base("The owner's Carrel data is not available.")
    {
    }

    public CarrelOwnerUnavailableException(Exception inner)
        : base("The owner's Carrel data is not available.", inner)
    {
    }
}

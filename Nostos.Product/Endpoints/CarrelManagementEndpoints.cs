using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Carrel;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The signed-in side of Carrel pairing (WP8): the approval page looks a code
/// up, approves or denies it, and Settings → Devices lists and revokes the
/// devices that already sync. The protocol's own routes live under
/// <c>/carrel/v1</c>; these are the web app's API, scoped to the owner the host
/// names (<see cref="ICarrelOwnerProvider"/>).
/// </summary>
public static class CarrelManagementEndpoints
{
    public const string Route = "/api/carrel";

    /// <summary>What the approval page shows for a waiting code.</summary>
    public sealed record PairingLookup(string Code, string DeviceName, DateTime ExpiresAt);

    /// <summary>The answer to approving a code.</summary>
    public sealed record PairingApproved(Guid DeviceId, string DeviceName);

    public sealed record DeviceList(IReadOnlyList<CarrelDeviceDto> Devices);

    /// <summary>
    /// Maps the routes when the host registered the feature; returns whether it
    /// did. <paramref name="authorizationPolicy"/> is the host's signed-in
    /// policy (Cloud); SelfHosted has one local owner and passes null.
    /// </summary>
    public static bool MapCarrelManagementEndpoints(
        this IEndpointRouteBuilder routes,
        string? authorizationPolicy = null,
        string? rateLimitPolicy = null)
    {
        if (routes.ServiceProvider.GetService<CarrelSyncService>() is null)
            return false;

        var group = routes.MapGroup(Route);
        if (!string.IsNullOrEmpty(authorizationPolicy))
            group.RequireAuthorization(authorizationPolicy);
        if (!string.IsNullOrEmpty(rateLimitPolicy))
            group.RequireRateLimiting(rateLimitPolicy);

        group.MapGet("/pairing/{code}", async (
            string code,
            ICarrelOwnerProvider owner,
            CarrelPairingService pairing,
            CancellationToken ct) =>
        {
            _ = owner.GetOwnerKey();
            var pending = await pairing.PendingAsync(code, ct);
            return pending is null
                ? Results.NotFound(new { error = "No device is waiting with that code." })
                : Results.Ok(new PairingLookup(pending.Code, pending.DeviceName, pending.ExpiresAtUtc));
        });

        group.MapPost("/pairing/{code}/approve", async (
            string code,
            ICarrelOwnerProvider owner,
            CarrelPairingService pairing,
            CancellationToken ct) =>
        {
            var outcome = await pairing.ApproveAsync(owner.GetOwnerKey(), code, ct);
            return outcome switch
            {
                CarrelApprovalOutcome.Approved approved => Results.Ok(
                    new PairingApproved(approved.DeviceId, approved.DeviceName)),
                CarrelApprovalOutcome.AlreadyApproved => Results.NotFound(
                    new { error = "That pairing is already approved." }),
                CarrelApprovalOutcome.Expired => Results.NotFound(
                    new { error = "That code has expired. Start pairing again." }),
                _ => Results.NotFound(new { error = "No device is waiting with that code." }),
            };
        });

        group.MapPost("/pairing/{code}/deny", async (
            string code,
            ICarrelOwnerProvider owner,
            CarrelPairingService pairing,
            CancellationToken ct) =>
        {
            _ = owner.GetOwnerKey();
            return await pairing.DenyAsync(code, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "No device is waiting with that code." });
        });

        group.MapGet("/devices", async (
            ICarrelOwnerProvider owner,
            CarrelPairingService pairing,
            CancellationToken ct) =>
        {
            var devices = await pairing.ListDevicesAsync(owner.GetOwnerKey(), ct);
            return Results.Ok(new DeviceList(devices));
        });

        group.MapDelete("/devices/{id:guid}", async (
            Guid id,
            ICarrelOwnerProvider owner,
            CarrelPairingService pairing,
            CancellationToken ct) =>
        {
            return await pairing.RevokeAsync(owner.GetOwnerKey(), id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        return true;
    }
}

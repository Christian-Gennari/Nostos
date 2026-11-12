using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Stylos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The signed-in side of Stylos pairing (WP8): the approval page looks a code
/// up, approves or denies it, and Settings → Devices lists and revokes the
/// devices that already sync. The protocol's own routes live under
/// <c>/stylos/v1</c>; these are the web app's API, scoped to the owner the host
/// names (<see cref="IStylosOwnerProvider"/>).
/// </summary>
public static class StylosManagementEndpoints
{
    public const string Route = "/api/stylos";

    /// <summary>What the approval page shows for a waiting code.</summary>
    public sealed record PairingLookup(string Code, string DeviceName, DateTime ExpiresAt);

    /// <summary>The answer to approving a code.</summary>
    public sealed record PairingApproved(Guid DeviceId, string DeviceName);

    public sealed record DeviceList(IReadOnlyList<StylosDeviceDto> Devices);

    /// <summary>
    /// Maps the routes when the host registered the feature; returns whether it
    /// did. <paramref name="authorizationPolicy"/> is the host's signed-in
    /// policy (Cloud); SelfHosted has one local owner and passes null.
    /// </summary>
    public static bool MapStylosManagementEndpoints(
        this IEndpointRouteBuilder routes, string? authorizationPolicy = null)
    {
        if (routes.ServiceProvider.GetService<StylosSyncService>() is null)
            return false;

        var group = routes.MapGroup(Route);
        if (!string.IsNullOrEmpty(authorizationPolicy))
            group.RequireAuthorization(authorizationPolicy);

        group.MapGet("/pairing/{code}", async (
            string code,
            IStylosOwnerProvider owner,
            StylosPairingService pairing,
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
            IStylosOwnerProvider owner,
            StylosPairingService pairing,
            CancellationToken ct) =>
        {
            var outcome = await pairing.ApproveAsync(owner.GetOwnerKey(), code, ct);
            return outcome switch
            {
                StylosApprovalOutcome.Approved approved => Results.Ok(
                    new PairingApproved(approved.DeviceId, approved.DeviceName)),
                StylosApprovalOutcome.AlreadyApproved => Results.NotFound(
                    new { error = "That pairing is already approved." }),
                StylosApprovalOutcome.Expired => Results.NotFound(
                    new { error = "That code has expired. Start pairing again." }),
                _ => Results.NotFound(new { error = "No device is waiting with that code." }),
            };
        });

        group.MapPost("/pairing/{code}/deny", async (
            string code,
            IStylosOwnerProvider owner,
            StylosPairingService pairing,
            CancellationToken ct) =>
        {
            _ = owner.GetOwnerKey();
            return await pairing.DenyAsync(code, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "No device is waiting with that code." });
        });

        group.MapGet("/devices", async (
            IStylosOwnerProvider owner,
            StylosPairingService pairing,
            CancellationToken ct) =>
        {
            var devices = await pairing.ListDevicesAsync(owner.GetOwnerKey(), ct);
            return Results.Ok(new DeviceList(devices));
        });

        group.MapDelete("/devices/{id:guid}", async (
            Guid id,
            IStylosOwnerProvider owner,
            StylosPairingService pairing,
            CancellationToken ct) =>
        {
            return await pairing.RevokeAsync(owner.GetOwnerKey(), id, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        return true;
    }
}

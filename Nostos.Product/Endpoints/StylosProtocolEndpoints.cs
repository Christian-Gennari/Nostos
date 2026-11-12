using System.Globalization;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Stylos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The Stylos Sync Protocol v1 endpoints, exactly as
/// <c>stylos-protocol/src/types.rs</c> spells them: <c>/pair/start</c>,
/// <c>/pair/{pair_id}</c>, <c>/files</c>, <c>/files/{file_id}</c> and
/// <c>/library</c>, under <c>/stylos/v1</c>.
///
/// The group is mapped only when the host registered the feature
/// (<see cref="StylosRegistration.AddNostosStylosSync"/>), and every file or
/// library route requires a device token. Pairing is anonymous by design: a
/// device has no credential until pairing hands it one.
///
/// The device builds its QR approval URL as <c>{server origin}/approve</c>
/// (the crate's <c>PairingSession::approval_url</c>), while Nostos's page is
/// <c>/pair</c>; <c>/approve</c> redirects there with the query kept.
/// </summary>
public static class StylosProtocolEndpoints
{
    public const string Route = "/stylos/v1";

    public static bool MapStylosProtocolEndpoints(this IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetService<StylosSyncService>() is null)
            return false;

        var group = routes.MapGroup(Route);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapPost("/pair/start", async (
            StylosPairStartRequest? request,
            StylosPairingService pairing,
            CancellationToken ct) =>
        {
            var deviceName = request?.DeviceName?.Trim() ?? string.Empty;
            if (deviceName.Length is 0 or > StylosDeviceModel.MaxNameLength)
            {
                return Results.Json(
                    new StylosErrorResponse(
                        $"device_name must be 1-{StylosDeviceModel.MaxNameLength} characters"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var publicKey = request?.DevicePublicKey?.Trim() ?? string.Empty;
            if (publicKey.Length is 0 or > StylosPairingModel.MaxPublicKeyLength)
            {
                return Results.Json(
                    new StylosErrorResponse(
                        $"device_pubkey must be 1-{StylosPairingModel.MaxPublicKeyLength} characters"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var started = await pairing.StartAsync(deviceName, publicKey, ct);
            return started is null
                ? Results.Json(
                    new StylosErrorResponse("could not allocate a pairing code"),
                    statusCode: StatusCodes.Status500InternalServerError)
                : Results.Ok(started);
        }).AllowAnonymous();

        group.MapGet("/pair/{pairId}", async (
            string pairId,
            StylosPairingService pairing,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(pairId, out var id))
                return NotFound("unknown pairing");

            var status = await pairing.StatusAsync(id, ct);
            return status is null ? NotFound("unknown pairing") : Results.Ok(status);
        }).AllowAnonymous();

        var files = routes
            .MapGroup(Route)
            .RequireAuthorization(StylosRegistration.Policy);
        files.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        files.MapGet("/files", async (
            string? since,
            HttpContext http,
            StylosSyncService sync,
            CancellationToken ct) =>
        {
            try
            {
                var list = await sync.ListFilesAsync(OwnerOf(http), since, ct);
                return Results.Ok(list);
            }
            catch (StylosCursorException exception)
            {
                return BadRequest(exception.Message);
            }
        });

        files.MapGet("/files/{fileId}", async (
            string fileId,
            HttpContext http,
            StylosSyncService sync,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(fileId, out var id))
                return NotFound("file not found");

            var content = await sync.GetFileAsync(OwnerOf(http), id, ct);
            return content is null ? NotFound("file not found") : Results.Ok(content);
        });

        files.MapPut("/files/{fileId}", async (
            string fileId,
            StylosPutFileRequest? request,
            HttpContext http,
            StylosSyncService sync,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(fileId, out var id))
                return BadRequest("invalid file id");
            if (request is null)
                return BadRequest("invalid body");
            if (request.BaseRev is < 0)
                return BadRequest("invalid base_rev");

            var outcome = await sync.PutFileAsync(OwnerOf(http), id, request, ct);
            return outcome switch
            {
                StylosPutOutcome.Success success => Results.Ok(
                    new StylosPutFileResponse(success.Rev, success.Hash)),
                StylosPutOutcome.Conflict conflict => Results.Json(
                    new StylosConflictResponse(conflict.Info),
                    statusCode: StatusCodes.Status409Conflict),
                StylosPutOutcome.Invalid invalid => BadRequest(invalid.Error),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        files.MapDelete("/files/{fileId}", async (
            string fileId,
            string? base_rev,
            HttpContext http,
            StylosSyncService sync,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(fileId, out var id))
                return BadRequest("invalid file id");
            if (string.IsNullOrEmpty(base_rev))
                return BadRequest("base_rev is required");
            if (!long.TryParse(base_rev, NumberStyles.None, CultureInfo.InvariantCulture, out var rev)
                || rev < 0)
                return BadRequest("invalid base_rev");

            var outcome = await sync.DeleteFileAsync(OwnerOf(http), id, rev, ct);
            return outcome switch
            {
                StylosDeleteOutcome.Deleted deleted => Results.Ok(new StylosDeleteResponse(deleted.Rev)),
                StylosDeleteOutcome.Conflict conflict => Results.Json(
                    new StylosConflictResponse(conflict.Info),
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        files.MapGet("/library", async (
            string? since,
            HttpContext http,
            StylosSyncService sync,
            CancellationToken ct) =>
        {
            try
            {
                var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
                var page = await sync.ListLibraryAsync(OwnerOf(http), since, baseUrl, ct);
                return Results.Ok(page);
            }
            catch (StylosCursorException exception)
            {
                return BadRequest(exception.Message);
            }
        });

        routes.MapGet("/approve", (HttpRequest request) =>
        {
            var query = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;
            return Results.Redirect($"/pair{query}");
        }).AllowAnonymous();

        return true;
    }

    private static string OwnerOf(HttpContext http) =>
        http.User.FindFirst(StylosDeviceAuthenticationHandler.OwnerClaimType)?.Value
        ?? throw new InvalidOperationException("A device token without an owner reached a Stylos route.");

    private static IResult NotFound(string error) =>
        Results.Json(new StylosErrorResponse(error), statusCode: StatusCodes.Status404NotFound);

    private static IResult BadRequest(string error) =>
        Results.Json(new StylosErrorResponse(error), statusCode: StatusCodes.Status400BadRequest);
}

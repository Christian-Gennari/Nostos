using System.Globalization;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Carrel;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The Carrel Sync Protocol v1 endpoints, exactly as
/// <c>carrel-protocol/src/types.rs</c> spells them: <c>/pair/start</c>,
/// <c>/pair/{pair_id}</c>, <c>/files</c>, <c>/files/{file_id}</c> and
/// <c>/library</c>, under <c>/carrel/v1</c>.
///
/// The group is mapped only when the host registered the feature
/// (<see cref="CarrelRegistration.AddNostosCarrelSync"/>), and every file or
/// library route requires a device token. Pairing is anonymous by design: a
/// device has no credential until pairing hands it one.
///
/// The device builds its QR approval URL as <c>{server origin}/approve</c>
/// (the crate's <c>PairingSession::approval_url</c>), while Nostos's page is
/// <c>/pair</c>; <c>/approve</c> redirects there with the query kept.
/// </summary>
public static class CarrelProtocolEndpoints
{
    public const string Route = "/carrel/v1";

    public static bool MapCarrelProtocolEndpoints(
        this IEndpointRouteBuilder routes, string? rateLimitPolicy = null)
    {
        if (routes.ServiceProvider.GetService<CarrelSyncService>() is null)
            return false;

        var group = routes.MapGroup(Route);
        if (!string.IsNullOrEmpty(rateLimitPolicy))
            group.RequireRateLimiting(rateLimitPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (CarrelOwnerUnavailableException)
            {
                // The token was good a moment ago but its owner's data is not
                // available now (an account being deleted): say nothing more.
                context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(
                    new CarrelErrorResponse("missing or invalid token"),
                    statusCode: StatusCodes.Status401Unauthorized);
            }
        });

        group.MapPost("/pair/start", async (
            CarrelPairStartRequest? request,
            CarrelPairingService pairing,
            CancellationToken ct) =>
        {
            var deviceName = request?.DeviceName?.Trim() ?? string.Empty;
            if (deviceName.Length is 0 or > CarrelDeviceModel.MaxNameLength)
            {
                return Results.Json(
                    new CarrelErrorResponse(
                        $"device_name must be 1-{CarrelDeviceModel.MaxNameLength} characters"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var publicKey = request?.DevicePublicKey?.Trim() ?? string.Empty;
            if (publicKey.Length is 0 or > CarrelPairingModel.MaxPublicKeyLength)
            {
                return Results.Json(
                    new CarrelErrorResponse(
                        $"device_pubkey must be 1-{CarrelPairingModel.MaxPublicKeyLength} characters"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var started = await pairing.StartAsync(deviceName, publicKey, ct);
            return started is null
                ? Results.Json(
                    new CarrelErrorResponse("could not allocate a pairing code"),
                    statusCode: StatusCodes.Status500InternalServerError)
                : Results.Ok(started);
        }).AllowAnonymous();

        group.MapGet("/pair/{pairId}", async (
            string pairId,
            CarrelPairingService pairing,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(pairId, out var id))
                return NotFound("unknown pairing");

            var status = await pairing.StatusAsync(id, ct);
            return status is null ? NotFound("unknown pairing") : Results.Ok(status);
        }).AllowAnonymous();

        var files = routes
            .MapGroup(Route)
            .RequireAuthorization(CarrelRegistration.Policy);
        files.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        files.MapGet("/files", async (
            string? since,
            HttpContext http,
            CarrelSyncService sync,
            CancellationToken ct) =>
        {
            try
            {
                var list = await sync.ListFilesAsync(OwnerOf(http), since, ct);
                return Results.Ok(list);
            }
            catch (CarrelCursorException exception)
            {
                return BadRequest(exception.Message);
            }
        });

        files.MapGet("/files/{fileId}", async (
            string fileId,
            HttpContext http,
            CarrelSyncService sync,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(fileId, out var id))
                return NotFound("file not found");

            var content = await sync.GetFileAsync(OwnerOf(http), id, ct);
            return content is null ? NotFound("file not found") : Results.Ok(content);
        });

        files.MapPut("/files/{fileId}", async (
            string fileId,
            CarrelPutFileRequest? request,
            HttpContext http,
            CarrelSyncService sync,
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
                CarrelPutOutcome.Success success => Results.Ok(
                    new CarrelPutFileResponse(success.Rev, success.Hash)),
                CarrelPutOutcome.Conflict conflict => Results.Json(
                    new CarrelConflictResponse(conflict.Info),
                    statusCode: StatusCodes.Status409Conflict),
                CarrelPutOutcome.Invalid invalid => BadRequest(invalid.Error),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        files.MapDelete("/files/{fileId}", async (
            string fileId,
            string? base_rev,
            HttpContext http,
            CarrelSyncService sync,
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
                CarrelDeleteOutcome.Deleted deleted => Results.Ok(new CarrelDeleteResponse(deleted.Rev)),
                CarrelDeleteOutcome.Conflict conflict => Results.Json(
                    new CarrelConflictResponse(conflict.Info),
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        });

        files.MapGet("/library", async (
            string? since,
            HttpContext http,
            CarrelSyncService sync,
            CancellationToken ct) =>
        {
            try
            {
                var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
                var page = await sync.ListLibraryAsync(OwnerOf(http), since, baseUrl, ct);
                return Results.Ok(page);
            }
            catch (CarrelCursorException exception)
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
        http.User.FindFirst(CarrelDeviceAuthenticationHandler.OwnerClaimType)?.Value
        ?? throw new InvalidOperationException("A device token without an owner reached a Carrel route.");

    private static IResult NotFound(string error) =>
        Results.Json(new CarrelErrorResponse(error), statusCode: StatusCodes.Status404NotFound);

    private static IResult BadRequest(string error) =>
        Results.Json(new CarrelErrorResponse(error), statusCode: StatusCodes.Status400BadRequest);
}

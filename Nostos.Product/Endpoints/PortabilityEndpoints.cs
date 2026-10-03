using Nostos.Backend.Services.Portability;
using Nostos.Product.Composition;

namespace Nostos.Backend.Endpoints;

public static class PortabilityEndpoints
{
    public const string ArchiveContentType = "application/vnd.nostos.portable+zip";

    public static IEndpointRouteBuilder MapPortabilityEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;

        var group = routes.MapGroup("/api/portability");
        if (!string.IsNullOrWhiteSpace(policies.LargeTransferRateLimitPolicy))
            group.RequireRateLimiting(policies.LargeTransferRateLimitPolicy);

        var export = group.MapGet("/export", async (
            HttpContext context,
            IPortableArchiveExporter exporter,
            CancellationToken cancellationToken) =>
                await exporter.ExportAsync(context, cancellationToken));

        if (!string.IsNullOrWhiteSpace(policies.PortableExportAuthorizationPolicy))
            export.RequireAuthorization(policies.PortableExportAuthorizationPolicy);

        group.MapPost("/import", async (
            HttpRequest request,
            IPortableArchiveService portability,
            CancellationToken cancellationToken) =>
        {
            if (request.ContentLength == 0)
            {
                return Results.BadRequest(new
                {
                    error = "empty_archive",
                    message = "Portable archive request body is empty.",
                });
            }

            try
            {
                var result = await portability.ImportAsync(
                    request.Body,
                    cancellationToken);
                return Results.Ok(result);
            }
            catch (PortableArchiveException exception)
            {
                var statusCode = exception.Code == "destination_not_empty"
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status400BadRequest;

                return Results.Json(
                    new
                    {
                        error = exception.Code,
                        message = exception.Message,
                    },
                    statusCode: statusCode);
            }
        });

        return routes;
    }
}

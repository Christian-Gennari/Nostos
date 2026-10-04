using Nostos.Backend.Configuration;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Middleware;

/// <summary>Endpoint owns its admission leases, allowing shared-to-exclusive handoff.</summary>
public sealed class LibraryMaintenanceControl;

/// <summary>
/// Covers REST, OPDS, MCP, DB readiness, and their complete response/stream lifetimes.
/// Its inner request scope disposes DbContexts before releasing the shared lease.
/// Only liveness, static UI, and backup progress avoid the active library entirely.
/// </summary>
public sealed class LibraryMaintenanceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ILibraryMaintenanceCoordinator maintenance, McpOptions mcp)
    {
        var path = context.Request.Path;
        var libraryPath = path.StartsWithSegments("/api") || path.StartsWithSegments("/opds")
            || path.StartsWithSegments("/health/ready")
            || (mcp.Enabled && path.StartsWithSegments(mcp.Path));
        if (!libraryPath || (HttpMethods.IsGet(context.Request.Method) && path == "/api/backup/progress"))
        {
            await next(context);
            return;
        }

        var control = context.GetEndpoint()?.Metadata.GetMetadata<LibraryMaintenanceControl>() is not null;
        await using var lease = control ? null : maintenance.TryEnterOperation();
        if ((control && maintenance.IsMaintenanceActive) || (!control && lease is null))
        {
            await WriteBusyAsync(context);
            return;
        }

        // RequestServices normally disposes after middleware returns, too late for
        // a drain barrier. Give every library endpoint a scope disposed inside it.
        var previousServices = context.RequestServices;
        try
        {
            await using var scope = previousServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            context.RequestServices = scope.ServiceProvider;
            try { await next(context); }
            catch (MigrationActivationException ex) when (ex.Code == MigrationActivationErrorCodes.Busy && !context.Response.HasStarted)
            {
                await WriteBusyAsync(context);
            }
        }
        finally { context.RequestServices = previousServices; }
    }

    private static Task WriteBusyAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        return context.Response.WriteAsJsonAsync(new
        {
            code = MigrationActivationErrorCodes.Busy,
            error = "The library is in maintenance. Try again later.",
        }, context.RequestAborted);
    }
}

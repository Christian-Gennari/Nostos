using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.ExternalMedia;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Linking media from the administrator-mounted folder (issue #843).
///
/// Mapped by the SelfHosted host only, and every route answers 404 until a
/// folder is configured on the server. No route accepts a server path: a
/// request can name a scanned candidate by its key and nothing else.
/// </summary>
public static class ExternalMediaEndpoints
{
    public const string Route = "/api/external-media";

    private const int DefaultPageSize = 200;
    private const int MaxPageSize = 500;

    public static IEndpointRouteBuilder MapExternalMediaEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(Route).AddEndpointFilter(async (context, next) =>
        {
            var root = context.HttpContext.RequestServices.GetRequiredService<ExternalMediaRoot>();
            return root.IsConfigured
                ? await next(context)
                : Results.NotFound(new
                {
                    code = "external_media_not_configured",
                    message = "No external media folder is configured on this server.",
                });
        });

        group.MapGet("/", async (
            ExternalMediaRoot root,
            ExternalMediaScanService scans,
            IDbContextFactory<NostosDbContext> contexts,
            CancellationToken ct) =>
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var linkedBooks = await db.ExternalMediaLinks
                .Where(link => link.RootId == root.RootId)
                .Select(link => link.BookId)
                .Distinct()
                .CountAsync(ct);

            return Results.Ok(new ExternalMediaStatusResponse(
                Mounted: root.IsMounted,
                LinkedBooks: linkedBooks,
                Scan: ToProgress(scans.State)));
        });

        group.MapPost("/scan", (ExternalMediaScanService scans, IHostApplicationLifetime lifetime) =>
            scans.TryStart(lifetime.ApplicationStopping)
                ? Results.Accepted(Route + "/scan", ToProgress(scans.State))
                : Results.Conflict(new
                {
                    code = "external_media_not_mounted",
                    message = "The external media folder is not available. Check that it is mounted into the server.",
                }));

        group.MapGet("/scan", (ExternalMediaScanService scans, string? status, int? offset, int? limit) =>
        {
            var state = scans.State;
            if (state.Result is not { } result)
                return Results.Ok(new ExternalMediaScanResponse(ToProgress(state), null, [], 0, [], []));

            var matching = string.IsNullOrWhiteSpace(status)
                ? result.Items
                : result.Items.Where(item => item.Status == status).ToList();
            var skip = Math.Max(offset ?? 0, 0);
            var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

            return Results.Ok(new ExternalMediaScanResponse(
                ToProgress(state),
                result.Summary,
                matching.Skip(skip).Take(take).ToList(),
                matching.Count,
                result.UnsupportedSamples,
                result.Missing));
        });

        return routes;
    }

    private static ExternalMediaScanProgress ToProgress(ExternalMediaScanState state) =>
        new(state.Status, state.StartedAt, state.Result?.FinishedAt, state.Examined, state.Total, state.Error);
}

public sealed record ExternalMediaStatusResponse(bool Mounted, int LinkedBooks, ExternalMediaScanProgress Scan);

public sealed record ExternalMediaScanProgress(
    string Status,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int Examined,
    int Total,
    string? Error);

public sealed record ExternalMediaScanResponse(
    ExternalMediaScanProgress Scan,
    ExternalMediaScanSummary? Summary,
    IReadOnlyList<ExternalMediaScanItem> Items,
    int MatchingItems,
    IReadOnlyList<string> UnsupportedSamples,
    IReadOnlyList<ExternalMediaMissingBook> Missing);

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

        // Imports the named candidates of the last scan, or every candidate
        // that is ready. A candidate that needs review is imported only when
        // it is named: "everything" never includes it.
        group.MapPost("/import", (
            ExternalMediaImportRequest request,
            ExternalMediaImportService imports,
            IHostApplicationLifetime lifetime) =>
        {
            if (request.AllReady == (request.Keys is { Count: > 0 }))
            {
                return Results.BadRequest(new
                {
                    code = "external_media_invalid_selection",
                    message = "Choose either every ready book or a list of books.",
                });
            }

            if (request.Keys is { Count: > MaxImportKeys })
            {
                return Results.BadRequest(new
                {
                    code = "external_media_invalid_selection",
                    message = $"At most {MaxImportKeys} books can be chosen by hand at once.",
                });
            }

            var keys = request.AllReady ? null : request.Keys!.ToHashSet(StringComparer.Ordinal);
            return imports.TryStart(keys, lifetime.ApplicationStopping) switch
            {
                ExternalMediaImportService.StartResult.Started =>
                    Results.Accepted(Route + "/import", imports.State),
                ExternalMediaImportService.StartResult.AlreadyRunning => Results.Conflict(new
                {
                    code = "external_media_import_running",
                    message = "An import is already running.",
                }),
                ExternalMediaImportService.StartResult.NoScan => Results.Conflict(new
                {
                    code = "external_media_scan_required",
                    message = "Scan the folder before importing.",
                }),
                _ => Results.Conflict(new
                {
                    code = "external_media_nothing_to_import",
                    message = "None of the chosen books are in the last scan.",
                }),
            };
        });

        group.MapGet("/import", (ExternalMediaImportService imports) => Results.Ok(imports.State));

        // Whether a book's media is read from the folder, and whether every
        // file it references can be read right now. This is what lets the
        // book page say "your original files stay where they are" before a
        // removal, and "external file unavailable" when the folder is gone.
        group.MapGet("/books/{bookId:guid}", async (
            Guid bookId,
            ExternalMediaRoot root,
            IDbContextFactory<NostosDbContext> contexts,
            CancellationToken ct) =>
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var links = await db.ExternalMediaLinks.AsNoTracking()
                .Where(link => link.BookId == bookId)
                .Select(link => new { link.RootId, link.RelativePath })
                .ToListAsync(ct);

            var unavailable = links.Count(link => root.Find(link.RootId, link.RelativePath) is null);
            return Results.Ok(new ExternalMediaBookResponse(
                Linked: links.Count > 0,
                Files: links.Count,
                UnavailableFiles: unavailable));
        });

        return routes;
    }

    private const int MaxImportKeys = 5000;

    private static ExternalMediaScanProgress ToProgress(ExternalMediaScanState state) =>
        new(state.Status, state.StartedAt, state.Result?.FinishedAt, state.Examined, state.Total, state.Error);
}

public sealed record ExternalMediaImportRequest(bool AllReady = false, IReadOnlyList<string>? Keys = null);

public sealed record ExternalMediaBookResponse(bool Linked, int Files, int UnavailableFiles);

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

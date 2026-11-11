using Nostos.Backend.Services.Calibre;
using Nostos.Backend.Services.Library.Imports;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Importing a Calibre library (issue #1527), mapped by the SelfHosted host
/// only. The status route always answers so Settings can explain how to set a
/// library up; reading, importing and undo answer a clear conflict until a
/// library is configured and reachable. No route accepts a path: the owner
/// names a Calibre book by its key from the preview and nothing else.
/// </summary>
public static class CalibreEndpoints
{
    public const string Route = "/api/calibre";

    private const int MaxDecisions = 50_000;

    public static IEndpointRouteBuilder MapCalibreEndpoints(this IEndpointRouteBuilder routes)
    {
        var status = routes.MapGroup(Route);
        var group = routes.MapGroup(Route).AddEndpointFilter(async (context, next) =>
        {
            var source = context.HttpContext.RequestServices.GetRequiredService<ICalibreLibrarySource>();
            if (!source.IsConfigured)
            {
                return Results.Conflict(new
                {
                    code = "calibre_not_configured",
                    message = "No Calibre library folder is configured on this server.",
                });
            }

            if (!source.IsAvailable)
            {
                return Results.Conflict(new
                {
                    code = "calibre_library_unavailable",
                    message = "The Calibre library is not available. Check that its folder is mounted and holds metadata.db.",
                });
            }

            return await next(context);
        });

        status.MapGet("/", (ICalibreLibrarySource source, CalibreImportJob job) =>
            Results.Ok(new CalibreStatusResponse(
                source.IsConfigured,
                source.IsAvailable,
                job.State)));

        // Reads the whole library and proposes every book. Nothing is written.
        group.MapGet("/preview", async (CalibreImportService importer, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await importer.PreviewAsync(ct));
            }
            catch (CalibreLibraryException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });

        group.MapPost("/import", (
            CalibreImportRequest? request,
            CalibreImportJob job,
            IHostApplicationLifetime lifetime) =>
        {
            var decisions = request?.Decisions ?? [];
            if (decisions.Count == 0)
            {
                return Results.BadRequest(new
                {
                    code = "calibre_nothing_selected",
                    message = "Choose at least one book and one format to import.",
                });
            }

            if (decisions.Count > MaxDecisions)
            {
                return Results.BadRequest(new
                {
                    code = "calibre_too_many_books",
                    message = $"At most {MaxDecisions} books can be imported at once.",
                });
            }

            return job.TryStart(decisions, lifetime.ApplicationStopping) switch
            {
                CalibreImportJob.StartResult.Started =>
                    Results.Accepted(Route + "/import", job.State),
                CalibreImportJob.StartResult.AlreadyRunning => Results.Conflict(new
                {
                    code = "calibre_import_running",
                    message = "An import is already running.",
                }),
                CalibreImportJob.StartResult.NothingSelected => Results.BadRequest(new
                {
                    code = "calibre_nothing_selected",
                    message = "Choose at least one book and one format to import.",
                }),
                _ => Results.Conflict(new
                {
                    code = "calibre_library_unavailable",
                    message = "The Calibre library is not available. Check that its folder is mounted and holds metadata.db.",
                }),
            };
        });

        group.MapGet("/import", (CalibreImportJob job) => Results.Ok(job.State));

        group.MapGet("/batches", async (CalibreImportService importer, CancellationToken ct) =>
            Results.Ok(await importer.ListBatchesAsync(5, ct)));

        // UNDO one import: removes the files, covers, memberships and books it
        // added. A book the owner has since read or written in is kept.
        group.MapDelete("/batches/{id:guid}", async (
            Guid id,
            CalibreImportService importer,
            CancellationToken ct) =>
            await importer.UndoAsync(id, ct) is { } result
                ? Results.Ok(result)
                : Results.NotFound());
        return routes;
    }
}

public sealed record CalibreImportRequest(IReadOnlyList<CalibreImportDecision>? Decisions = null);

public sealed record CalibreStatusResponse(
    bool Configured,
    bool Available,
    CalibreImportJobState Import);

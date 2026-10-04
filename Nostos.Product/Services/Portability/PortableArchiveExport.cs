namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Host seam for portable archive export.
///
/// The common endpoint owns the route and wire contract. A host may decorate
/// export with account-lifecycle semantics (for example a deletion grace
/// period) without moving or duplicating the product endpoint.
/// </summary>
public interface IPortableArchiveExporter
{
    Task<IResult> ExportAsync(HttpContext context, CancellationToken cancellationToken = default);
}

public sealed class DefaultPortableArchiveExporter(
    IPortableArchiveService portability) : IPortableArchiveExporter
{
    public async Task<IResult> ExportAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        context.Response.ContentType = "application/vnd.nostos.portable+zip";
        context.Response.Headers.ContentDisposition =
            $"attachment; filename=\"nostos-export-{now:yyyyMMdd-HHmmss}.nostos\"";

        try
        {
            await portability.ExportAsync(context.Response.Body, cancellationToken);
        }
        catch
        {
            // The archive is streamed as it is produced. Once bytes have been
            // sent, a failure or cancellation leaves a truncated body that must
            // never be mistaken for a complete download: abort the connection
            // instead of letting the response end cleanly. Failures before the
            // first byte keep the framework's ordinary error response.
            if (context.Response.HasStarted)
                context.Abort();
            throw;
        }

        return Results.Empty;
    }
}

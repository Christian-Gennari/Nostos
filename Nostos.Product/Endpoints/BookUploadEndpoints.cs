using Microsoft.AspNetCore.Http.Features;
using Nostos.Backend.Services.BookUpload;
using Nostos.Product.Composition;
using Nostos.Product.Http;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Resumable book-file upload contract (#1266 Phase A). A host maps this group
/// only when it registers <see cref="IBookFileUploadStore"/>; the capability
/// endpoint advertises the same registration, so a client can never be told
/// the contract exists by a host that cannot execute it.
/// </summary>
public static class BookUploadEndpoints
{
    public const string BasePath = "/api/books";

    public static IEndpointRouteBuilder MapBookUploadEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies policies)
    {
        var group = routes.MapGroup(BasePath);
        var mutationGroup = string.IsNullOrWhiteSpace(policies.ExpensiveMutationRateLimitPolicy)
            ? group
            : routes.MapGroup(BasePath)
                .RequireRateLimiting(policies.ExpensiveMutationRateLimitPolicy);
        var transferGroup = string.IsNullOrWhiteSpace(policies.LargeTransferRateLimitPolicy)
            ? group
            : routes.MapGroup(BasePath)
                .RequireRateLimiting(policies.LargeTransferRateLimitPolicy);

        // Create, or resume the one active session for this (owner, book).
        mutationGroup.MapPost(
            "/{bookId:guid}/file/uploads",
            async (
                Guid bookId,
                BookUploadCreateHttpRequest? body,
                BookUploadService service,
                CancellationToken ct) =>
            {
                if (body?.FileName is null || body.Sha256 is null)
                    return BookUploadHttpErrors.InvalidRequest();

                try
                {
                    var (status, created) = await service.CreateOrResumeAsync(
                        bookId,
                        new BookUploadCreateRequest(
                            body.TotalBytes,
                            body.FileName,
                            body.ContentType,
                            body.Sha256,
                            body.ClientFingerprint),
                        ct);
                    return created
                        ? Results.Created($"{BasePath}/{bookId}/file/uploads/{status.SessionId}", status)
                        : Results.Ok(status);
                }
                catch (BookUploadException exception)
                {
                    return BookUploadHttpErrors.From(exception);
                }
            });

        // Owner-scoped discovery: what should the page resume when it reloads?
        group.MapGet(
            "/{bookId:guid}/file/uploads",
            async (Guid bookId, BookUploadService service, CancellationToken ct) =>
            {
                try
                {
                    var status = await service.GetCurrentAsync(bookId, ct);
                    return status is null ? Results.NoContent() : Results.Ok(status);
                }
                catch (BookUploadException exception)
                {
                    return BookUploadHttpErrors.From(exception);
                }
            });

        group.MapGet(
            "/{bookId:guid}/file/uploads/{sessionId:guid}",
            async (
                Guid bookId,
                Guid sessionId,
                BookUploadService service,
                CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await service.GetAsync(bookId, sessionId, ct));
                }
                catch (BookUploadException exception)
                {
                    return BookUploadHttpErrors.From(exception);
                }
            });

        transferGroup.MapPut(
            "/{bookId:guid}/file/uploads/{sessionId:guid}/parts/{partIndex:int}",
            async (
                Guid bookId,
                Guid sessionId,
                int partIndex,
                HttpRequest request,
                BookUploadService service,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                if (!MigrationChunkHeaders.TryParseContentRange(
                        request.Headers["Content-Range"],
                        out var start,
                        out var end,
                        out var total)
                    || !MigrationChunkHeaders.IsSha256(
                        request.Headers[MigrationChunkHeaders.ChunkHashHeaderName]))
                {
                    return BookUploadHttpErrors.InvalidRequest();
                }

                // One part is the largest body this route accepts. A declared
                // oversize body is refused before a byte is read; a chunked
                // body that grows past the limit makes Kestrel throw 413.
                if (request.ContentLength > BookUploadLimits.PartBytes)
                    return BookUploadHttpErrors.PartTooLarge();

                var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySize is { IsReadOnly: false })
                    bodySize.MaxRequestBodySize = BookUploadLimits.PartBytes;

                var logger = loggerFactory.CreateLogger("Nostos.BookUpload");
                var counting = new CountingRequestBodyStream(request.Body);
                request.Body = counting;

                try
                {
                    var result = await service.UploadPartAsync(
                        bookId,
                        sessionId,
                        partIndex,
                        start,
                        end,
                        total,
                        request.Headers[MigrationChunkHeaders.ChunkHashHeaderName].ToString(),
                        counting,
                        ct);
                    return Results.Ok(result);
                }
                catch (BadHttpRequestException exception)
                    when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return BookUploadHttpErrors.PartTooLarge();
                }
                catch (BadHttpRequestException exception)
                {
                    // Kestrel reports a client that vanished mid-body as a bad
                    // request rather than an IO failure. It is still exactly the
                    // interruption this warning exists to make diagnosable.
                    LogInterrupted(logger, bookId, partIndex, counting, request, exception);
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    LogInterrupted(logger, bookId, partIndex, counting, request, exception);
                    throw;
                }
                catch (IOException exception)
                {
                    // The connection went away mid-part. The bytes are not
                    // receipted, so the client's resume re-sends this part.
                    LogInterrupted(logger, bookId, partIndex, counting, request, exception);
                    throw;
                }
                catch (BookUploadException exception)
                {
                    // A short body can surface as a range failure instead of
                    // an IO error; the received-byte count still tells a
                    // truncated part from a genuinely malformed request.
                    if (request.ContentLength is { } declared
                        && counting.BytesRead < declared)
                    {
                        LogInterrupted(logger, bookId, partIndex, counting, request, exception);
                    }
                    return BookUploadHttpErrors.From(exception);
                }
            });

        mutationGroup.MapPost(
            "/{bookId:guid}/file/uploads/{sessionId:guid}/complete",
            async (
                Guid bookId,
                Guid sessionId,
                BookUploadService service,
                CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await service.CompleteAsync(bookId, sessionId, ct));
                }
                catch (BookUploadException exception)
                {
                    return BookUploadHttpErrors.From(exception);
                }
            });

        mutationGroup.MapPost(
            "/{bookId:guid}/file/uploads/{sessionId:guid}/pause",
            async (
                Guid bookId,
                Guid sessionId,
                BookUploadService service,
                CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await service.PauseAsync(bookId, sessionId, ct));
                }
                catch (BookUploadException exception)
                {
                    return BookUploadHttpErrors.From(exception);
                }
            });

        mutationGroup.MapDelete(
            "/{bookId:guid}/file/uploads/{sessionId:guid}",
            async (
                Guid bookId,
                Guid sessionId,
                BookUploadService service,
                CancellationToken ct) =>
            {
                try
                {
                    await service.DiscardAsync(bookId, sessionId, ct);
                    return Results.NoContent();
                }
                catch (BookUploadException exception)
                {
                    return BookUploadHttpErrors.From(exception);
                }
            });

        return routes;
    }

    private static void LogInterrupted(
        ILogger logger,
        Guid bookId,
        int partIndex,
        CountingRequestBodyStream counting,
        HttpRequest request,
        Exception? exception = null)
    {
        logger.LogWarning(
            exception,
            "Book upload part {PartIndex} for book {BookId} was interrupted after " +
            "{BytesReceived} of {DeclaredBytes} bytes.",
            partIndex,
            bookId,
            counting.BytesRead,
            request.ContentLength ?? -1);
    }
}

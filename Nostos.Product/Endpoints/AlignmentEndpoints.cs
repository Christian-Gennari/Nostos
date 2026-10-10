using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Product.Alignment;
using Nostos.Product.Composition;
using Nostos.Product.Http;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Endpoints;

public static class AlignmentEndpoints
{
    public sealed record LocateTextRequest(
        double PositionSeconds,
        Guid TargetBookId);

    public static IEndpointRouteBuilder MapAlignmentEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;

        var group = routes.MapGroup("/api/books");

        if (!string.IsNullOrWhiteSpace(
            policies.ExpensiveMutationRateLimitPolicy))
        {
            group.RequireRateLimiting(
                policies.ExpensiveMutationRateLimitPolicy);
        }

        group.MapPost(
            "/{audioBookId:guid}/alignment/locate-text",
            LocateTextAsync);

        return routes;
    }

    private static async Task<IResult> LocateTextAsync(
        Guid audioBookId,
        LocateTextRequest request,
        ILibraryService library,
        IBookRepository books,
        IAlignmentService alignment,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        static IResult Failure(
            string code, int status, string detail) =>
            Results.Problem(
                title: code,
                statusCode: status,
                detail: detail);

        if (audioBookId == Guid.Empty ||
            request.TargetBookId == Guid.Empty ||
            !double.IsFinite(request.PositionSeconds) ||
            request.PositionSeconds <= 0)
        {
            return Failure(
                "alignment_invalid_request",
                400,
                "A valid source, target and positive position are required.");
        }

        var sourceRead = await library.GetBookAsync(audioBookId, ct);

        if (LibraryHttpMapper.MapError(sourceRead) is not null)
        {
            return Failure(
                "alignment_book_not_found",
                StatusCodes.Status404NotFound,
                "The source audiobook was not found or is not accessible.");
        }

        var targetRead = await library.GetBookAsync(
            request.TargetBookId, ct);

        if (LibraryHttpMapper.MapError(targetRead) is not null)
        {
            return Failure(
                "alignment_book_not_found",
                StatusCodes.Status404NotFound,
                "The target ebook was not found or is not accessible.");
        }

        var source = await books.GetByIdAsync(audioBookId);
        var target = await books.GetByIdAsync(request.TargetBookId);

        if (source is null || target is null)
        {
            return Failure(
                "alignment_book_not_found",
                404,
                "One of the requested books was not found.");
        }

        if (source is not AudioBookModel audio)
        {
            return Failure(
                "alignment_source_not_audio",
                422,
                "The source edition must be an audiobook.");
        }

        if (target is not EBookModel epub ||
            !string.Equals(
                Path.GetExtension(epub.FileDetails.FileName),
                ".epub",
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(
                "alignment_target_not_epub",
                422,
                "The target edition must be an EPUB.");
        }

        if (audio.WorkId == Guid.Empty ||
            audio.WorkId != epub.WorkId)
        {
            return Failure(
                "alignment_work_mismatch",
                422,
                "The editions must belong to the same work.");
        }

        if (!await access.IsAllowedAsync(ct))
        {
            return Failure(
                SttErrorCodes.AccessDenied,
                403,
                "The host policy does not permit alignment transcription.");
        }

        try
        {
            var result = await alignment.LocateAsync(
                audio, epub, request.PositionSeconds, ct);

            if (!result.Located)
            {
                return Results.Ok(new
                {
                    located = false,
                    reason = result.Reason,
                });
            }

            return Results.Ok(new
            {
                located = true,
                score = result.Score,
                excerpt = result.Excerpt,
                locator = result.Locator,
            });
        }
        catch (AudioClipException ex)
        {
            var status = ex.Code switch
            {
                AudioClipErrorCodes.InvalidPosition => 400,
                AudioClipErrorCodes.MediaMissing => 422,
                AudioClipErrorCodes.Unavailable => 503,
                AudioClipErrorCodes.Timeout => 504,
                _ => 422,
            };

            return Failure(ex.Code, status, ex.Message);
        }
        catch (AlignmentException ex)
        {
            return Failure(ex.Code, ex.StatusCode, ex.Message);
        }
        catch (AiUsageException ex)
        {
            var (code, status) = ex.Reason switch
            {
                AiUsageBlockReason.AccessDenied =>
                    ("ai_access_denied", 403),
                AiUsageBlockReason.RateLimited =>
                    ("ai_rate_limited", 429),
                AiUsageBlockReason.LimitReached =>
                    ("ai_usage_limit_reached", 429),
                AiUsageBlockReason.Disabled =>
                    ("ai_disabled", 503),
                _ =>
                    ("ai_temporarily_unavailable", 503),
            };

            return Failure(code, status, ex.Message);
        }
        catch (SttException ex)
        {
            var status = ex.Code switch
            {
                SttErrorCodes.AccessDenied => 403,
                SttErrorCodes.Disabled or
                    SttErrorCodes.NotConfigured => 503,
                SttErrorCodes.RateLimited => 429,
                SttErrorCodes.Timeout => 504,
                SttErrorCodes.TooLarge => 413,
                SttErrorCodes.TooLong => 422,
                SttErrorCodes.UnsupportedFormat => 415,
                SttErrorCodes.InvalidRequest => 400,
                _ => 502,
            };

            return Failure(ex.Code, status, ex.Message);
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(
                "alignment_operation_timeout",
                StatusCodes.Status504GatewayTimeout,
                "The alignment operation timed out. It was not retried.");
        }
        catch (Exception)
        {
            return Failure(
                "alignment_internal_error",
                StatusCodes.Status500InternalServerError,
                "Alignment could not be completed. No automatic retry was attempted.");
        }
    }
}

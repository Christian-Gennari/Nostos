using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Product.Composition;
using Nostos.Product.Http;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Endpoints;

public static class AlignmentEndpoints
{
    public sealed record LocateTextRequest(
        double PositionSeconds,
        Guid TargetBookId);

    public sealed record LocateAudioRequest(
        Guid TargetBookId,
        string? Excerpt);

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

        group.MapPost(
            "/{textBookId:guid}/alignment/locate-audio",
            LocateAudioAsync);

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

        var pair = await LoadPairAsync(
            audioBookId,
            request.TargetBookId,
            library,
            books,
            ct);

        if (pair.Error is not null)
            return pair.Error;

        if (pair.Source is not AudioBookModel audio)
        {
            return Failure(
                "alignment_source_not_audio",
                422,
                "The source edition must be an audiobook.");
        }

        if (pair.Target is not EBookModel text ||
            !IsSupportedText(text))
        {
            return Failure(
                "alignment_target_not_epub",
                422,
                "The target edition must be an EPUB or PDF with stored media.");
        }

        if (!SameWork(audio, text))
        {
            return Failure(
                "alignment_work_mismatch",
                422,
                "The editions must belong to the same work.");
        }

        if (!await access.IsAllowedAsync(ct))
            return AccessDenied();

        try
        {
            var result = await alignment.LocateAsync(
                audio,
                text,
                request.PositionSeconds,
                ct);

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
                pdf = result.Pdf,
                sourceText = result.SourceText,
                anchorStored = result.AnchorStored,
            });
        }
        catch (Exception ex)
        {
            return MapException(ex, ct);
        }
    }

    private static async Task<IResult> LocateAudioAsync(
        Guid textBookId,
        LocateAudioRequest request,
        ILibraryService library,
        IBookRepository books,
        IAlignmentService alignment,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        if (textBookId == Guid.Empty ||
            request.TargetBookId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Excerpt) ||
            request.Excerpt.Length > 2000)
        {
            return Failure(
                "alignment_invalid_request",
                400,
                "A valid text edition, audiobook and excerpt are required.");
        }

        var pair = await LoadPairAsync(
            textBookId,
            request.TargetBookId,
            library,
            books,
            ct);

        if (pair.Error is not null)
            return pair.Error;

        if (pair.Source is not EBookModel text ||
            !IsSupportedText(text))
        {
            return Failure(
                "alignment_source_not_epub",
                422,
                "The source edition must be an EPUB or PDF with stored media.");
        }

        if (pair.Target is not AudioBookModel audio)
        {
            return Failure(
                "alignment_target_not_audio",
                422,
                "The target edition must be an audiobook.");
        }

        if (!SameWork(audio, text))
        {
            return Failure(
                "alignment_work_mismatch",
                422,
                "The editions must belong to the same work.");
        }

        if (!await access.IsAllowedAsync(ct))
            return AccessDenied();

        try
        {
            var result = await alignment.LocateAudioAsync(
                text,
                audio,
                request.Excerpt,
                ct);

            if (!result.Located)
            {
                return Results.Ok(new
                {
                    located = false,
                    reason = result.Reason,
                    approximatePositionSeconds =
                        result.ApproximatePositionSeconds,
                    probes = result.Probes,
                });
            }

            return Results.Ok(new
            {
                located = true,
                positionSeconds = result.PositionSeconds,
                probes = result.Probes,
                score = result.Score,
                anchorStored = result.AnchorStored,
            });
        }
        catch (Exception ex)
        {
            return MapException(ex, ct);
        }
    }

    private static async Task<(
        BookModel? Source,
        BookModel? Target,
        IResult? Error)> LoadPairAsync(
        Guid sourceId,
        Guid targetId,
        ILibraryService library,
        IBookRepository books,
        CancellationToken ct)
    {
        var sourceRead = await library.GetBookAsync(sourceId, ct);

        if (LibraryHttpMapper.MapError(sourceRead) is not null)
        {
            return (
                null,
                null,
                Failure(
                    "alignment_book_not_found",
                    StatusCodes.Status404NotFound,
                    "The source book was not found or is not accessible."));
        }

        var targetRead = await library.GetBookAsync(targetId, ct);

        if (LibraryHttpMapper.MapError(targetRead) is not null)
        {
            return (
                null,
                null,
                Failure(
                    "alignment_book_not_found",
                    StatusCodes.Status404NotFound,
                    "The target book was not found or is not accessible."));
        }

        var source = await books.GetByIdAsync(sourceId);
        var target = await books.GetByIdAsync(targetId);

        if (source is null || target is null)
        {
            return (
                null,
                null,
                Failure(
                    "alignment_book_not_found",
                    404,
                    "One of the requested books was not found."));
        }

        return (source, target, null);
    }

    private static bool IsSupportedText(EBookModel book) =>
        book.FileDetails.HasFile &&
        BookTextFormatResolver.TryResolve(
            book.FileDetails.FileName, out var format) &&
        format is
            (BookTextSourceFormat.Epub or BookTextSourceFormat.Pdf);

    private static bool SameWork(
        AudioBookModel audio,
        EBookModel epub) =>
        audio.WorkId != Guid.Empty &&
        audio.WorkId == epub.WorkId;

    private static IResult AccessDenied() =>
        Failure(
            SttErrorCodes.AccessDenied,
            403,
            "The host policy does not permit alignment transcription.");

    private static IResult Failure(
        string code,
        int status,
        string detail) =>
        Results.Problem(
            title: code,
            statusCode: status,
            detail: detail);

    private static IResult MapException(
        Exception ex,
        CancellationToken ct)
    {
        switch (ex)
        {
            case AudioClipException clip:
            {
                var status = clip.Code switch
                {
                    AudioClipErrorCodes.InvalidPosition => 400,
                    AudioClipErrorCodes.MediaMissing => 422,
                    AudioClipErrorCodes.Unavailable => 503,
                    AudioClipErrorCodes.Timeout => 504,
                    _ => 422,
                };

                return Failure(clip.Code, status, clip.Message);
            }

            case AlignmentException alignment:
                return Failure(
                    alignment.Code,
                    alignment.StatusCode,
                    alignment.Message);

            case AiUsageException usage:
                return Failure(usage.Code, usage.StatusCode, usage.Message);

            case SttException stt:
            {
                var status = stt.Code switch
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

                return Failure(stt.Code, status, stt.Message);
            }

            case OperationCanceledException
                when ct.IsCancellationRequested:
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);
                throw new InvalidOperationException("Unreachable.");

            case OperationCanceledException:
                return Failure(
                    "alignment_operation_timeout",
                    StatusCodes.Status504GatewayTimeout,
                    "The alignment operation timed out. It was not retried.");

            default:
                return Failure(
                    "alignment_internal_error",
                    StatusCodes.Status500InternalServerError,
                    "Alignment could not be completed. No automatic retry was attempted.");
        }
    }
}

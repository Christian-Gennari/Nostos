using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// <c>POST /api/assistant/transcribe</c> — voice capture's server-side seam
/// (issue #262 §3).
///
/// The browser uploads audio and receives text; it never holds the provider
/// credential, so the permanent key cannot leak. Raw audio is streamed straight
/// through to the provider and is never written to disk here — in a worktree
/// <c>Nostos.Backend/Storage</c> is a symlink into the live library, so a stray
/// temp copy would be destructive rather than merely untidy.
///
/// Every failure is returned as data with a stable code, so nothing auto-retries
/// and quietly spends the user's rationed transcription quota. The endpoint is
/// mapped even when the integration is disabled, so a client gets a typed
/// "disabled" answer instead of a 404 or a 500.
/// </summary>
public static class TranscriptionEndpoints
{
    public const string Route = "/api/assistant/transcribe";

    public static IEndpointRouteBuilder MapTranscriptionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/assistant");
        group.MapPost("/transcribe", TranscribeAsync);
        return routes;
    }

    private static async Task<IResult> TranscribeAsync(
        HttpRequest request,
        ISTtProvider provider,
        SpeechOptions options,
        IAiProviderConfigResolver config,
        IAiAccessPolicy access,
        IAiUsageAccountingService usage,
        CancellationToken ct)
    {
        if (!await access.IsAllowedAsync(ct))
        {
            return Failure(
                SttErrorCodes.AccessDenied,
                StatusCodes.Status403Forbidden,
                "The host policy does not allow voice transcription for this request.");
        }

        // Availability is derived (enabled && baseUrl && key), exactly like the
        // assistant's gate: an enabled-but-unconfigured surface is a typed 503,
        // never a startup failure or a 500. The credential itself is only tested
        // for presence here.
        var effective = await config.GetEffectiveSttAsync(ct);
        if (!effective.Enabled)
        {
            return Failure(
                SttErrorCodes.Disabled,
                StatusCodes.Status503ServiceUnavailable,
                "Speech-to-text is disabled on this server.");
        }

        if (!effective.IsAvailable)
        {
            return Failure(
                SttErrorCodes.NotConfigured,
                StatusCodes.Status503ServiceUnavailable,
                SttException.NotConfigured(effective.ApiKeyEnvironmentVariable).Message);
        }

        if (!request.HasFormContentType)
        {
            return Failure(
                SttErrorCodes.InvalidRequest,
                StatusCodes.Status400BadRequest,
                "Expected a multipart/form-data request with a 'file' part.");
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (InvalidDataException)
        {
            return Failure(
                SttErrorCodes.InvalidRequest,
                StatusCodes.Status400BadRequest,
                "The multipart form could not be read.");
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Failure(
                SttErrorCodes.InvalidRequest,
                StatusCodes.Status400BadRequest,
                "A non-empty 'file' part is required.");
        }

        if (options.MaxUploadBytes > 0 && file.Length > options.MaxUploadBytes)
        {
            return Failure(
                SttErrorCodes.TooLarge,
                StatusCodes.Status413PayloadTooLarge,
                $"The audio upload exceeds the {options.MaxUploadBytes} byte limit.");
        }

        var language = form["language"].ToString();
        var languageHint = string.IsNullOrWhiteSpace(language) ? null : language.Trim();

        AiUsageLease? usageLease;
        try
        {
            usageLease = await usage.BeginSttAsync(ct);
        }
        catch (AiUsageException ex)
        {
            return UsageFailure(ex);
        }

        SttResult result;
        try
        {
            // Streamed, not copied: the form file's stream is handed to the
            // provider and released when this scope ends. Nothing is written to
            // Storage or to a temp file of our own.
            await using var audio = file.OpenReadStream();
            result = await provider.TranscribeAsync(
                audio,
                file.FileName,
                file.ContentType,
                languageHint,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await usage.CompleteSttAsync(
                usageLease,
                new TranscriptionUsage(1, null, "Cancelled"),
                CancellationToken.None);
            throw;
        }
        catch (SttException ex)
        {
            await usage.CompleteSttAsync(
                usageLease,
                new TranscriptionUsage(1, null, "ProviderError"),
                CancellationToken.None);
            return Failure(ex.Code, StatusFor(ex.Code), ex.Message);
        }
        catch
        {
            await usage.CompleteSttAsync(
                usageLease,
                new TranscriptionUsage(1, null, "ProviderError"),
                CancellationToken.None);
            throw;
        }

        // The duration cap is a policy on what was returned, not a pre-flight
        // check: the gateway has no length probe, so this is the only place it
        // can be applied.
        if (options.MaxDurationSeconds > 0
            && result.DurationSeconds is > 0
            && result.DurationSeconds > options.MaxDurationSeconds)
        {
            await usage.CompleteSttAsync(
                usageLease,
                new TranscriptionUsage(1, result.DurationSeconds, "TooLong"),
                CancellationToken.None);
            return Failure(
                SttErrorCodes.TooLong,
                StatusCodes.Status422UnprocessableEntity,
                $"The audio is {result.DurationSeconds:0.#}s, above the {options.MaxDurationSeconds:0.#}s limit.");
        }

        await usage.CompleteSttAsync(
            usageLease,
            new TranscriptionUsage(1, result.DurationSeconds, "Completed"),
            CancellationToken.None);

        return Results.Ok(new TranscriptionResponse(
            result.Text,
            result.Language,
            result.DurationSeconds));
    }

    private static IResult UsageFailure(AiUsageException exception) =>
        exception.Reason switch
        {
            AiUsageBlockReason.AccessDenied =>
                Failure("ai_access_denied", StatusCodes.Status403Forbidden, exception.Message),
            AiUsageBlockReason.RateLimited =>
                Failure("ai_rate_limited", StatusCodes.Status429TooManyRequests, exception.Message),
            AiUsageBlockReason.LimitReached =>
                Failure("ai_usage_limit_reached", StatusCodes.Status429TooManyRequests, exception.Message),
            AiUsageBlockReason.Disabled =>
                Failure("ai_disabled", StatusCodes.Status503ServiceUnavailable, exception.Message),
            _ =>
                Failure("ai_temporarily_unavailable", StatusCodes.Status503ServiceUnavailable, exception.Message),
        };

    /// <summary>
    /// Code → HTTP status. Kept as data: a permission problem is a bad gateway
    /// (our credential, not the caller's), never a 401 the browser would read as
    /// "the user is not signed in".
    /// </summary>
    private static int StatusFor(string code) => code switch
    {
        SttErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
        SttErrorCodes.Disabled or SttErrorCodes.NotConfigured => StatusCodes.Status503ServiceUnavailable,
        SttErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        SttErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
        SttErrorCodes.TooLarge => StatusCodes.Status413PayloadTooLarge,
        SttErrorCodes.TooLong => StatusCodes.Status422UnprocessableEntity,
        SttErrorCodes.UnsupportedFormat => StatusCodes.Status415UnsupportedMediaType,
        SttErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status502BadGateway,
    };

    private static IResult Failure(string code, int statusCode, string detail) =>
        Results.Problem(statusCode: statusCode, title: code, detail: detail);

    /// <summary>The JSON body returned on success.</summary>
    public sealed record TranscriptionResponse(
        string Text,
        string? Language,
        double? DurationSeconds);
}

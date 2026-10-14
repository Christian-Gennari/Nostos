using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The server side of read-aloud: which voices this host has for a book's
/// language, and one sentence turned into audio.
///
/// The provider is resolved as an optional service. A host that registers none
/// (an older Cloud host, a SelfHosted server with no speech server connected)
/// answers "not available" instead of failing, and the reader uses the device's
/// voice. For the same reason every failure here is typed: the reader falls
/// back on any of them, and must never be handed a 500 it cannot read.
///
/// The browser never holds a provider credential, and no text or audio is
/// written to the library's storage.
/// </summary>
public static class ReadAloudEndpoints
{
    public const string StatusRoute = "/api/read-aloud/status";
    public const string SpeechRoute = "/api/read-aloud/speech";

    /// <summary>Used when no provider is registered to say how long a request may be.</summary>
    private const int DefaultMaxCharacters = 1200;

    public static IEndpointRouteBuilder MapReadAloudEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/read-aloud");
        group.MapGet("/status", StatusAsync);
        group.MapPost("/speech", SpeechAsync);
        return routes;
    }

    private static async Task<IResult> StatusAsync(
        string? language,
        IServiceProvider services,
        IAiAccessPolicy access,
        CancellationToken ct)
    {
        var provider = services.GetService<IReadAloudProvider>();
        if (provider is null)
            return Results.Ok(Unavailable(managed: false, ReadAloudErrorCodes.Disabled));

        // The reader shows why the natural voice is off, so the reason is the
        // same stable code the speech route would fail with.
        var availability = await provider.GetAvailabilityAsync(ct);
        if (!availability.Enabled)
            return Results.Ok(Unavailable(availability.Managed, ReadAloudErrorCodes.Disabled));
        if (!availability.Configured)
            return Results.Ok(Unavailable(availability.Managed, ReadAloudErrorCodes.NotConfigured));
        if (!await access.IsAllowedAsync(ct))
            return Results.Ok(Unavailable(availability.Managed, ReadAloudErrorCodes.AccessDenied));

        var voices = ReadAloudVoiceSelection.ForLanguage(provider.Voices, language);
        return Results.Ok(new ReadAloudStatusResponse(
            Available: voices.Count > 0,
            Managed: availability.Managed,
            DefaultVoice: voices.Count > 0 ? voices[0].Id : null,
            Voices: voices,
            MaxCharacters: availability.MaxCharacters,
            Reason: voices.Count > 0 ? null : ReadAloudErrorCodes.UnsupportedLanguage));
    }

    private static async Task<IResult> SpeechAsync(
        ReadAloudSpeechBody? body,
        IServiceProvider services,
        IAiAccessPolicy access,
        IAiUsageAccountingService usage,
        CancellationToken ct)
    {
        var provider = services.GetService<IReadAloudProvider>();
        if (provider is null)
        {
            return Failure(
                ReadAloudErrorCodes.Disabled,
                StatusCodes.Status503ServiceUnavailable,
                "This server has no voice for read aloud.");
        }

        if (!await access.IsAllowedAsync(ct))
        {
            return Failure(
                ReadAloudErrorCodes.AccessDenied,
                StatusCodes.Status403Forbidden,
                "The host policy does not allow a server voice for this request.");
        }

        var availability = await provider.GetAvailabilityAsync(ct);
        if (!availability.Enabled)
        {
            return Failure(
                ReadAloudErrorCodes.Disabled,
                StatusCodes.Status503ServiceUnavailable,
                "The server voice for read aloud is switched off.");
        }

        if (!availability.Configured)
        {
            return Failure(
                ReadAloudErrorCodes.NotConfigured,
                StatusCodes.Status503ServiceUnavailable,
                "Read aloud is switched on but no speech server is configured.");
        }

        var text = body?.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Failure(
                ReadAloudErrorCodes.InvalidRequest,
                StatusCodes.Status400BadRequest,
                "A non-empty 'text' is required.");
        }

        if (availability.MaxCharacters > 0 && text.Length > availability.MaxCharacters)
        {
            return Failure(
                ReadAloudErrorCodes.TooLong,
                StatusCodes.Status413PayloadTooLarge,
                $"The text is {text.Length} characters, above the {availability.MaxCharacters} character limit.");
        }

        // A named voice must be one this host offers; otherwise the language
        // decides. An unknown voice is never passed through to the provider.
        var voice = string.IsNullOrWhiteSpace(body?.Voice)
            ? null
            : provider.Voices.FirstOrDefault(v => v.Id == body!.Voice!.Trim());
        voice ??= ReadAloudVoiceSelection.ForLanguage(provider.Voices, body?.Language).FirstOrDefault();
        if (voice is null)
        {
            return Failure(
                ReadAloudErrorCodes.UnsupportedLanguage,
                StatusCodes.Status422UnprocessableEntity,
                "The server voice has nothing for this book's language.");
        }

        AiUsageLease? lease;
        try
        {
            lease = await usage.BeginTtsAsync(text.Length, ct);
        }
        catch (AiUsageException ex)
        {
            return Failure(ex.Code, ex.StatusCode, ex.Message);
        }

        ReadAloudAudio audio;
        try
        {
            audio = await provider.SynthesizeAsync(new ReadAloudSpeechRequest(text, voice.Id), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await usage.CompleteTtsAsync(
                lease,
                new SpeechSynthesisUsage(1, 0, "Cancelled"),
                CancellationToken.None);
            throw;
        }
        catch (ReadAloudException ex)
        {
            await usage.CompleteTtsAsync(
                lease,
                new SpeechSynthesisUsage(1, 0, "ProviderError"),
                CancellationToken.None);
            return Failure(ex.Code, StatusFor(ex.Code), ex.Message);
        }
        catch
        {
            await usage.CompleteTtsAsync(
                lease,
                new SpeechSynthesisUsage(1, 0, "ProviderError"),
                CancellationToken.None);
            throw;
        }

        await usage.CompleteTtsAsync(
            lease,
            new SpeechSynthesisUsage(1, text.Length, "Completed"),
            CancellationToken.None);

        return Results.Bytes(audio.Audio, audio.ContentType);
    }

    private static ReadAloudStatusResponse Unavailable(bool managed, string reason) =>
        new(
            Available: false,
            Managed: managed,
            DefaultVoice: null,
            Voices: [],
            MaxCharacters: DefaultMaxCharacters,
            Reason: reason);

    private static int StatusFor(string code) => code switch
    {
        ReadAloudErrorCodes.AccessDenied => StatusCodes.Status403Forbidden,
        ReadAloudErrorCodes.Disabled or ReadAloudErrorCodes.NotConfigured =>
            StatusCodes.Status503ServiceUnavailable,
        ReadAloudErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        ReadAloudErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
        ReadAloudErrorCodes.TooLong => StatusCodes.Status413PayloadTooLarge,
        ReadAloudErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        ReadAloudErrorCodes.UnsupportedLanguage => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status502BadGateway,
    };

    private static IResult Failure(string code, int statusCode, string detail) =>
        Results.Problem(statusCode: statusCode, title: code, detail: detail);

    /// <summary>The JSON body of a speech request.</summary>
    public sealed record ReadAloudSpeechBody(string? Text, string? Voice, string? Language);

    /// <summary>
    /// What the reader needs to decide between the server voice and the
    /// device's. <paramref name="Voices"/> is already filtered to the asked
    /// language. <paramref name="Reason"/> is a <see cref="ReadAloudErrorCodes"/>
    /// value saying why the voice is not available, and null when it is.
    /// </summary>
    public sealed record ReadAloudStatusResponse(
        bool Available,
        bool Managed,
        string? DefaultVoice,
        IReadOnlyList<ReadAloudVoice> Voices,
        int MaxCharacters,
        string? Reason = null);
}

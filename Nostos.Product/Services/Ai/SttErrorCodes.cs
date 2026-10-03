namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Stable codes for every way transcription can fail.
///
/// They exist so a failure is data — the endpoint maps a code to a status, and
/// the client can branch on it — rather than an exception the global handler
/// would turn into a 500. Just as important, a coded failure is terminal: there
/// is no retry on this path, because every retry is another paid call against
/// the user's rationed Groq quota.
/// </summary>
public static class SttErrorCodes
{
    /// <summary>The integration is switched off (<c>Speech:Enabled=false</c>).</summary>
    public const string Disabled = "stt_disabled";

    /// <summary>Enabled, but the configured environment variable holds no key.</summary>
    public const string NotConfigured = "stt_not_configured";

    /// <summary>The host policy does not allow transcription access.</summary>
    public const string AccessDenied = "stt_access_denied";

    /// <summary>The request itself is malformed (not multipart, or no file part).</summary>
    public const string InvalidRequest = "stt_invalid_request";

    /// <summary>The upload exceeds <c>Speech:MaxUploadBytes</c>.</summary>
    public const string TooLarge = "stt_audio_too_large";

    /// <summary>The audio is longer than <c>Speech:MaxDurationSeconds</c>.</summary>
    public const string TooLong = "stt_audio_too_long";

    /// <summary>The provider refuses the container/codec it was sent.</summary>
    public const string UnsupportedFormat = "stt_unsupported_format";

    /// <summary>The provider rejected the configured credential, or is rate limiting.</summary>
    public const string Permission = "stt_permission_denied";

    /// <summary>The transcription provider is temporarily rate limiting.</summary>
    public const string RateLimited = "stt_rate_limited";

    /// <summary>The transcription provider timed out.</summary>
    public const string Timeout = "stt_provider_timeout";

    /// <summary>The provider is unreachable or returned an unexpected status.</summary>
    public const string Provider = "stt_provider_error";

    /// <summary>The provider answered with a body this provider cannot read.</summary>
    public const string InvalidResponse = "stt_response_invalid";
}

/// <summary>
/// A provider-side speech-to-text failure, carrying a
/// <see cref="SttErrorCodes"/> value so the endpoint never has to match on a
/// message. This is the only exception <see cref="ISTtProvider"/> is allowed to
/// throw for a provider problem; cancellation is deliberately NOT wrapped, so
/// it stays an <see cref="OperationCanceledException"/> and is never mistaken
/// for a 500.
/// </summary>
public sealed class SttException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static SttException Disabled() => new(
        SttErrorCodes.Disabled,
        "Speech-to-text is disabled. Set Speech:Enabled=true and configure a provider key to use voice capture.");

    public static SttException NotConfigured(string environmentVariable) => new(
        SttErrorCodes.NotConfigured,
        $"Speech-to-text is enabled but environment variable '{environmentVariable}' is not set or empty.");

    public static SttException TooLarge(long maxBytes) => new(
        SttErrorCodes.TooLarge,
        $"The audio upload exceeds the {maxBytes} byte limit.");

    public static SttException UnsupportedFormat(string? contentType) => new(
        SttErrorCodes.UnsupportedFormat,
        string.IsNullOrWhiteSpace(contentType)
            ? "The transcription provider does not accept this audio format."
            : $"The transcription provider does not accept audio of type '{contentType}'.");

    public static SttException PermissionDenied() => new(
        SttErrorCodes.Permission,
        "The transcription provider rejected the configured credential or is rate limiting.");

    public static SttException RateLimited() => new(
        SttErrorCodes.RateLimited,
        "Voice transcription is temporarily busy. Try again shortly.");

    public static SttException TimedOut() => new(
        SttErrorCodes.Timeout,
        "Voice transcription timed out.");

    public static SttException ProviderFailure(string detail) => new(
        SttErrorCodes.Provider,
        $"The transcription provider failed: {detail}");

    public static SttException InvalidResponse(string detail) => new(
        SttErrorCodes.InvalidResponse,
        $"The transcription provider returned an unexpected response: {detail}");
}

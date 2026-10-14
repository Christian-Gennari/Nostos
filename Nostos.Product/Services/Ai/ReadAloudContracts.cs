namespace Nostos.Product.Services.Ai;

/// <summary>
/// Stable codes for every way reading a sentence aloud can fail on the server.
///
/// A failure is data: the endpoint maps a code to a status and the reader
/// branches on it. That matters more here than elsewhere, because the reader
/// answers any of these by carrying on with the device's own voice instead of
/// stopping the book.
/// </summary>
public static class ReadAloudErrorCodes
{
    /// <summary>The host offers no server voice, or it is switched off.</summary>
    public const string Disabled = "read_aloud_disabled";

    /// <summary>Switched on, but no speech server is addressed.</summary>
    public const string NotConfigured = "read_aloud_not_configured";

    /// <summary>The host policy does not allow this request a server voice.</summary>
    public const string AccessDenied = "read_aloud_access_denied";

    /// <summary>The request has no text, or is not the expected JSON.</summary>
    public const string InvalidRequest = "read_aloud_invalid_request";

    /// <summary>The text is longer than one request may carry.</summary>
    public const string TooLong = "read_aloud_text_too_long";

    /// <summary>The server voice has nothing for the book's language.</summary>
    public const string UnsupportedLanguage = "read_aloud_unsupported_language";

    /// <summary>
    /// Status only: the book does not say its language, so every voice is
    /// listed and none is chosen for the reader.
    /// </summary>
    public const string LanguageUnknown = "read_aloud_language_unknown";

    /// <summary>The speech server rejected the configured credential.</summary>
    public const string Permission = "read_aloud_permission_denied";

    /// <summary>The speech server is rate limiting.</summary>
    public const string RateLimited = "read_aloud_rate_limited";

    /// <summary>The speech server did not answer in time.</summary>
    public const string Timeout = "read_aloud_provider_timeout";

    /// <summary>The speech server is unreachable or returned an unexpected status.</summary>
    public const string Provider = "read_aloud_provider_error";

    /// <summary>The speech server answered with something that is not audio.</summary>
    public const string InvalidResponse = "read_aloud_response_invalid";
}

/// <summary>
/// A provider-side read-aloud failure carrying a <see cref="ReadAloudErrorCodes"/>
/// value. Cancellation is deliberately not wrapped.
/// </summary>
public sealed class ReadAloudException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One voice a host can read with. <paramref name="Language"/> is a BCP 47 tag.</summary>
public sealed record ReadAloudVoice(string Id, string Name, string Language);

/// <summary>
/// Whether a server voice can be used right now. <paramref name="Managed"/> is
/// true when the host supplies and meters the voice (Cloud) and false when the
/// installation's owner connected their own (SelfHosted).
/// </summary>
public sealed record ReadAloudAvailability(
    bool Enabled,
    bool Configured,
    bool Managed,
    int MaxCharacters)
{
    public bool Available => Enabled && Configured;
}

public sealed record ReadAloudSpeechRequest(string Text, string Voice);

/// <summary>Encoded audio for one request. The bytes are a complete file, not a stream.</summary>
public sealed record ReadAloudAudio(byte[] Audio, string ContentType);

/// <summary>
/// Optional host seam: a server-side voice for read-aloud.
///
/// A host that registers nothing has no server voice, and the reader uses the
/// device's. SelfHosted registers a provider that talks to a speech server the
/// owner runs; official Cloud registers a managed one. Product endpoints depend
/// only on this contract and never on a vendor.
/// </summary>
public interface IReadAloudProvider
{
    Task<ReadAloudAvailability> GetAvailabilityAsync(CancellationToken ct = default);

    /// <summary>Every voice this host can read with, across languages.</summary>
    IReadOnlyList<ReadAloudVoice> Voices { get; }

    /// <summary>
    /// Speaks <see cref="ReadAloudSpeechRequest.Text"/> at normal speed. The
    /// reader changes speed on playback, so one rendering serves every speed.
    /// Provider problems are reported as <see cref="ReadAloudException"/>.
    /// </summary>
    Task<ReadAloudAudio> SynthesizeAsync(
        ReadAloudSpeechRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// The voices of the open Kokoro model (v1.0), which both the SelfHosted setup
/// guide and the managed Cloud voice use. Ids are the model's own.
/// </summary>
public static class KokoroVoices
{
    public static readonly IReadOnlyList<ReadAloudVoice> All =
    [
        new("af_heart", "Heart", "en-US"),
        new("af_bella", "Bella", "en-US"),
        new("am_michael", "Michael", "en-US"),
        new("am_fenrir", "Fenrir", "en-US"),
        new("bf_emma", "Emma", "en-GB"),
        new("bf_isabella", "Isabella", "en-GB"),
        new("bm_george", "George", "en-GB"),
        new("bm_fable", "Fable", "en-GB"),
        new("ef_dora", "Dora", "es"),
        new("em_alex", "Alex", "es"),
        new("ff_siwis", "Siwis", "fr"),
        new("hf_alpha", "Alpha", "hi"),
        new("hm_omega", "Omega", "hi"),
        new("if_sara", "Sara", "it"),
        new("im_nicola", "Nicola", "it"),
        new("jf_alpha", "Alpha", "ja"),
        new("jm_kumo", "Kumo", "ja"),
        new("pf_dora", "Dora", "pt-BR"),
        new("pm_alex", "Alex", "pt-BR"),
        new("zf_xiaoxiao", "Xiaoxiao", "zh"),
        new("zm_yunxi", "Yunxi", "zh"),
    ];
}

/// <summary>Matches a book's language to the voices that can read it.</summary>
public static class ReadAloudVoiceSelection
{
    /// <summary>
    /// The voices for <paramref name="language"/>: those of the exact region
    /// ("en-GB") when there are any, otherwise every voice of the language
    /// ("en", or "en-AU" with no Australian voice). A book that declares no
    /// language gets nothing, because a voice for the wrong language reads
    /// every word wrongly.
    /// </summary>
    public static IReadOnlyList<ReadAloudVoice> ForLanguage(
        IReadOnlyList<ReadAloudVoice> voices,
        string? language)
    {
        var wanted = Normalize(language);
        if (wanted.Length == 0)
            return [];

        var primary = Primary(wanted);
        var exact = voices.Where(v => Normalize(v.Language) == wanted).ToList();
        return exact.Count > 0
            ? exact
            : voices.Where(v => Primary(Normalize(v.Language)) == primary).ToList();
    }

    private static string Normalize(string? tag) =>
        (tag ?? string.Empty).Trim().Replace('_', '-').ToLowerInvariant();

    private static string Primary(string tag)
    {
        var dash = tag.IndexOf('-');
        return dash < 0 ? tag : tag[..dash];
    }
}

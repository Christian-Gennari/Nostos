namespace Nostos.Backend.Configuration;

/// <summary>
/// Speech-to-text configuration (issue #262 §2/§3).
///
/// Optional and disabled by default, exactly like <see cref="McpOptions"/>: with
/// <see cref="Enabled"/> false the transcription endpoint is still mapped, but
/// answers with a typed "disabled" error rather than calling a provider or
/// 500-ing. The credential is never read from configuration — only the NAME of
/// the environment variable that holds it — so the key cannot be committed and
/// cannot be shipped to the Angular client.
/// </summary>
public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    /// <summary>Master switch. When false the endpoint is a typed no-op.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Base URL of the OpenAI-compatible gateway (no trailing /v1).</summary>
    public string BaseUrl { get; set; } = "http://omenhub:20128";

    /// <summary>
    /// Model id sent verbatim. The <c>groq/</c> prefix is mandatory: a bare
    /// <c>whisper-1</c> routes to an uncredentialed <c>openai</c> provider and
    /// 400s. This value is never normalized or rewritten.
    /// </summary>
    public string Model { get; set; } = "groq/whisper-large-v3-turbo";

    /// <summary>Name of the environment variable holding the bearer token.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "NOSTOS_STT_TOKEN";

    /// <summary>Largest accepted upload, in bytes (25 MiB).</summary>
    public long MaxUploadBytes { get; set; } = 26_214_400;

    /// <summary>
    /// Longest accepted audio, in seconds. Enforced after the provider reports
    /// the duration; the gateway offers no pre-flight length check.
    /// </summary>
    public double MaxDurationSeconds { get; set; } = 300;
}

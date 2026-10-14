namespace Nostos.Backend.Configuration;

/// <summary>
/// The SelfHosted server voice for read-aloud, bound from the <c>ReadAloud</c>
/// configuration section (or <c>ReadAloud__*</c> environment variables).
///
/// It is off by default: an installation reads with the device's own voice
/// until its owner connects a speech server. Any server that speaks the OpenAI
/// speech route works; the setup guide uses a local Kokoro server, which needs
/// no key and sends no text outside the machine.
/// </summary>
public sealed class ReadAloudOptions
{
    public const string SectionName = "ReadAloud";

    /// <summary>Master switch. When false the reader uses the device voice.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base URL of the speech server, e.g. <c>http://localhost:8880</c>. A URL
    /// whose path already carries a version segment (<c>…/v1</c>,
    /// <c>…/v1/openai</c>) is used as given; otherwise <c>/v1</c> is added.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Model id sent verbatim.</summary>
    public string Model { get; set; } = "kokoro";

    /// <summary>
    /// Name of the environment variable holding a bearer token. A local speech
    /// server needs none, so an unset variable is not an error.
    /// </summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "NOSTOS_READ_ALOUD_TOKEN";

    /// <summary>Longest text one request may carry. The reader sends a sentence at a time.</summary>
    public int MaxCharacters { get; set; } = 1200;

    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Where rendered sentences are kept so that going back, or reading a
    /// chapter again, does not render them twice. Empty means a folder under
    /// the system temp directory. Never inside the library's storage.
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;

    /// <summary>Upper bound of the cache on disk, in bytes (512 MiB). 0 disables it.</summary>
    public long CacheMaxBytes { get; set; } = 536_870_912;
}

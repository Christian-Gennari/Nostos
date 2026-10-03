namespace Nostos.Backend.Configuration;

/// <summary>
/// Passage-embedding configuration (issue #683): the appsettings/environment
/// fallback behind the "Embeddings" card in Settings → AI.
///
/// Optional, exactly like <see cref="SpeechOptions"/>: with no base URL or no
/// key the surface reports unavailable and nothing is ever embedded. Embeddings
/// are a derived search index, never a source of truth, so "unavailable" only
/// means Ask Nostos keeps using lexical retrieval. The credential is never read
/// from configuration — only the NAME of the environment variable that holds it.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>Master switch. When false no passage is sent to a provider.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base URL of the OpenAI-compatible endpoint, including its <c>/v1</c>
    /// suffix (for example <c>https://ai-gateway.vercel.sh/v1</c>). The provider
    /// posts to <c>{BaseUrl}/embeddings</c>.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Model id sent verbatim (Qwen3-Embedding-0.6B, 1024 dimensions).</summary>
    public string Model { get; set; } = "alibaba/qwen3-embedding-0-6b";

    /// <summary>Name of the environment variable holding the bearer token.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "NOSTOS_EMBEDDING_TOKEN";

    /// <summary>Upper bound for one <c>/embeddings</c> request, in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;
}

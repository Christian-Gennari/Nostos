namespace Nostos.Backend.Data.Models;

/// <summary>
/// Singleton row holding the server-wide overrides for the assistant's LLM,
/// voice STT and passage-embedding providers (assistant-milestone plan, "AI
/// provider settings"; embeddings: issue #683).
///
/// Nostos is a single-user, unauthenticated app: there are no accounts, so the
/// provider configuration is server-wide and there is exactly one row. Every
/// column is nullable on purpose — <c>NULL</c> means "no override, use the
/// appsettings/env fallback", so a stored <c>false</c> (an explicit kill switch)
/// is distinguishable from "never set". The base URL/model columns hold the
/// owner's own override; the key columns hold
/// <see cref="Microsoft.AspNetCore.DataProtection"/>-encrypted ciphertext and
/// are NEVER returned to a client.
/// </summary>
public class AiProviderSettingsModel
{
    /// <summary>The fixed primary key of the one settings row.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    // --- LLM (assistant) overrides ---
    public bool? LlmEnabled { get; set; }
    public string? LlmBaseUrl { get; set; }
    public string? LlmModel { get; set; }

    /// <summary>Data-Protection ciphertext of the owner-supplied LLM key, or null.</summary>
    public string? LlmApiKeyEncrypted { get; set; }

    // --- STT (voice capture) overrides ---
    public bool? SttEnabled { get; set; }
    public string? SttBaseUrl { get; set; }
    public string? SttModel { get; set; }

    /// <summary>Data-Protection ciphertext of the owner-supplied STT key, or null.</summary>
    public string? SttApiKeyEncrypted { get; set; }

    // --- Embedding (semantic retrieval) overrides ---
    public bool? EmbeddingEnabled { get; set; }
    public string? EmbeddingBaseUrl { get; set; }
    public string? EmbeddingModel { get; set; }

    /// <summary>Data-Protection ciphertext of the owner-supplied embedding key, or null.</summary>
    public string? EmbeddingApiKeyEncrypted { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

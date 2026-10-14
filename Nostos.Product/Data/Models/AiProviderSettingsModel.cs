namespace Nostos.Backend.Data.Models;

/// <summary>
/// Singleton row holding the server-wide overrides for the assistant's LLM,
/// voice STT, alignment transcription, passage-embedding and read-aloud
/// speech providers.
///
/// Every provider override column is nullable. NULL means "no override",
/// preserving the distinction between an explicit false and an unset value.
/// API key columns contain ASP.NET Data Protection ciphertext, never plaintext.
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

    // --- STT (audiobook alignment) overrides ---
    public bool? AlignmentTranscriptionEnabled { get; set; }
    public string? AlignmentTranscriptionBaseUrl { get; set; }
    public string? AlignmentTranscriptionModel { get; set; }

    /// <summary>
    /// Data-Protection ciphertext of the owner-supplied alignment key, or null.
    /// Uses the same protection purpose as the other provider credentials.
    /// </summary>
    public string? AlignmentTranscriptionApiKeyEncrypted { get; set; }

    /// <summary>
    /// NULL means use voice transcription. An explicit false selects the
    /// dedicated alignment configuration.
    /// </summary>
    public bool? AlignmentTranscriptionUseVoiceTranscription { get; set; }

    /// <summary>
    /// NULL means openai-multipart. Other supported values are defined by
    /// AlignmentTranscriptionApiStyles.
    /// </summary>
    public string? AlignmentTranscriptionApiStyle { get; set; }

    // --- Embedding (semantic retrieval) overrides ---
    public bool? EmbeddingEnabled { get; set; }
    public string? EmbeddingBaseUrl { get; set; }
    public string? EmbeddingModel { get; set; }

    /// <summary>Data-Protection ciphertext of the owner-supplied embedding key, or null.</summary>
    public string? EmbeddingApiKeyEncrypted { get; set; }

    // --- Read aloud (speech server) overrides ---
    public bool? TtsEnabled { get; set; }
    public string? TtsBaseUrl { get; set; }
    public string? TtsModel { get; set; }

    /// <summary>Data-Protection ciphertext of the owner-supplied speech server key, or null.</summary>
    public string? TtsApiKeyEncrypted { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

namespace Nostos.Product.Services.Ai;

/// <summary>
/// The one operation semantic retrieval needs from an embedding model: turn a
/// batch of texts into dense vectors (issue #683).
///
/// Host-supplied, like <c>ILlmProvider</c> and <c>ISTtProvider</c>: SelfHosted
/// registers a BYOK OpenAI-compatible <c>/v1/embeddings</c> client configured in
/// Settings → AI; Nostos Cloud supplies a managed one. Product code depends only
/// on this contract.
///
/// Embeddings are an OPTIONAL, DERIVED index. Every caller must treat an
/// unavailable or failing provider as "no vectors" and keep lexical retrieval
/// working — a provider problem is never allowed to fail ingestion or search.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// The model id vectors would be produced with right now, or <c>null</c>
    /// when embeddings are disabled or not fully configured. Vectors from
    /// different models are not comparable, so the id is stored beside every
    /// vector and a changed id means "re-embed".
    /// </summary>
    Task<string?> GetActiveModelAsync(CancellationToken ct = default);

    /// <summary>
    /// Embeds <paramref name="inputs"/> in one upstream request and returns one
    /// vector per input, in input order. Failure is reported as a typed
    /// <see cref="EmbeddingException"/>; cancellation stays a cancellation.
    /// There is no retry loop here — the caller decides when to try again.
    /// </summary>
    Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken ct = default);
}

/// <summary>
/// One successful embedding request. <see cref="Vectors"/> is index-aligned with
/// the request's inputs and every vector has the same length.
/// <see cref="TotalTokens"/> is whatever the provider reported and may be null.
/// </summary>
public sealed record EmbeddingBatch(
    string Model,
    IReadOnlyList<float[]> Vectors,
    int? TotalTokens);

/// <summary>Stable codes for every way an embedding call can fail.</summary>
public static class EmbeddingErrorCodes
{
    /// <summary>Disabled, or the endpoint/model/key is missing.</summary>
    public const string NotConfigured = "embedding_not_configured";

    /// <summary>The provider rejected the configured credential.</summary>
    public const string Permission = "embedding_permission_denied";

    /// <summary>The provider is temporarily rate limiting requests.</summary>
    public const string RateLimited = "embedding_rate_limited";

    /// <summary>The provider did not answer within its request timeout.</summary>
    public const string Timeout = "embedding_provider_timeout";

    /// <summary>The provider is unreachable or returned an unexpected status.</summary>
    public const string Provider = "embedding_provider_error";

    /// <summary>The provider answered with a body this client cannot use.</summary>
    public const string InvalidResponse = "embedding_response_invalid";
}

/// <summary>
/// The only exception <see cref="IEmbeddingProvider"/> is allowed to throw for a
/// provider problem. Messages never contain the credential or the embedded text.
/// </summary>
public sealed class EmbeddingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static EmbeddingException NotConfigured() => new(
        EmbeddingErrorCodes.NotConfigured,
        "Embeddings are disabled or not fully configured (endpoint, model and API key are all required).");

    public static EmbeddingException PermissionDenied() => new(
        EmbeddingErrorCodes.Permission,
        "The embedding provider rejected the configured credential.");

    public static EmbeddingException RateLimited() => new(
        EmbeddingErrorCodes.RateLimited,
        "The embedding provider is rate limiting requests.");

    public static EmbeddingException TimedOut() => new(
        EmbeddingErrorCodes.Timeout,
        "The embedding provider did not answer in time.");

    public static EmbeddingException ProviderFailure(string detail) => new(
        EmbeddingErrorCodes.Provider,
        $"The embedding provider failed: {detail}");

    public static EmbeddingException InvalidResponse(string detail) => new(
        EmbeddingErrorCodes.InvalidResponse,
        $"The embedding provider returned an unexpected response: {detail}");
}

/// <summary>
/// The provider a host gets when it registers none: embeddings are simply off,
/// so retrieval stays lexical.
/// </summary>
public sealed class NoOpEmbeddingProvider : IEmbeddingProvider
{
    public Task<string?> GetActiveModelAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken ct = default) =>
        throw EmbeddingException.NotConfigured();
}

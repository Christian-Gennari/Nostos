using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Thin facade over AI provider settings persistence (<see cref="AiProviderSettingsStore"/>),
/// OpenAI-compatible model discovery (<see cref="AiProviderModelDiscovery"/>), and
/// live connection probing (<see cref="AiProviderConnectionTester"/>).
/// </summary>
public sealed class AiProviderSettingsService : IAiProviderSettingsService
{
    /// <summary>Registered HTTP client for ordinary settings probes.</summary>
    public const string HttpClientName = "ai-provider-settings";

    private readonly AiProviderSettingsStore _store;
    private readonly AiProviderModelDiscovery _modelDiscovery;
    private readonly AiProviderConnectionTester _connectionTester;

    [ActivatorUtilitiesConstructor]
    public AiProviderSettingsService(
        IDbContextFactory<NostosDbContext> dbFactory,
        AssistantOptions assistantOptions,
        SpeechOptions speechOptions,
        EmbeddingOptions embeddingOptions,
        IDataProtectionProvider dataProtection,
        IHttpClientFactory httpClientFactory,
        ILogger<AiProviderSettingsService> logger,
        ReadAloudOptions? readAloudOptions = null)
    {
        var store = new AiProviderSettingsStore(
            dbFactory,
            assistantOptions,
            speechOptions,
            embeddingOptions,
            dataProtection,
            logger,
            readAloudOptions);
        var httpClient = new AiProviderProbeHttpClient(httpClientFactory);
        var multipartStt = new OpenAiCompatibleSttProvider(
            httpClientFactory,
            speechOptions,
            store,
            NullLogger<OpenAiCompatibleSttProvider>.Instance);
        var jsonStt = new JsonBase64SttProvider(
            httpClientFactory,
            speechOptions,
            store);

        _store = store;
        _modelDiscovery = new AiProviderModelDiscovery(store, httpClient);
        _connectionTester = new AiProviderConnectionTester(
            store, httpClient, multipartStt, jsonStt);
    }

    internal AiProviderSettingsService(
        AiProviderSettingsStore store,
        AiProviderModelDiscovery modelDiscovery,
        AiProviderConnectionTester connectionTester)
    {
        _store = store;
        _modelDiscovery = modelDiscovery;
        _connectionTester = connectionTester;
    }

    public Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(
        CancellationToken ct = default) =>
        _store.GetEffectiveLlmAsync(ct);

    public Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(
        CancellationToken ct = default) =>
        _store.GetEffectiveSttAsync(ct);

    public Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(
        CancellationToken ct = default) =>
        _store.GetEffectiveEmbeddingAsync(ct);

    public Task<EffectiveAlignmentSttConfig> GetEffectiveAlignmentSttAsync(
        CancellationToken ct = default) =>
        _store.GetEffectiveAlignmentSttAsync(ct);

    public Task<EffectiveAiProviderConfig> GetEffectiveTtsAsync(
        CancellationToken ct = default) =>
        _store.GetEffectiveTtsAsync(ct);

    public Task<AiProviderSettingsResponse> GetAsync(
        CancellationToken ct = default) =>
        _store.GetAsync(ct);

    public Task<AiProviderSettingsResponse> UpdateAsync(
        AiProviderSettingsUpdateRequest request,
        CancellationToken ct = default) =>
        _store.UpdateAsync(request, ct);

    public Task<AiProviderModelsResponse> ListModelsAsync(
        AiProviderModelsRequest request,
        CancellationToken ct = default) =>
        _modelDiscovery.ListModelsAsync(request, ct);

    public Task<AiProviderTestResult> TestAsync(
        AiProviderTestRequest request,
        CancellationToken ct = default) =>
        _connectionTester.TestAsync(request, ct);
}

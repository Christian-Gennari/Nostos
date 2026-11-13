using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Composition;

/// <summary>
/// Registers SelfHosted AI transports and settings: BYOK embeddings (before
/// <c>AddNostosProduct</c>), speech-to-text, read-aloud TTS, alignment
/// transcription, the assistant LLM bridge, and encrypted AI provider settings.
/// </summary>
public static class SelfHostedAiComposition
{
    /// <summary>
    /// Passage embeddings (issue #683): BYOK and optional. Must be called before
    /// <c>AddNostosProduct</c> so it wins over the product's no-op fallback. While
    /// the surface is unconfigured the provider reports no active model, so the
    /// book-text embedding pass does nothing and retrieval stays lexical.
    /// </summary>
    public static IServiceCollection AddSelfHostedEmbeddingProvider(this IServiceCollection services)
    {
        services.AddOptions<EmbeddingOptions>()
            .BindConfiguration(EmbeddingOptions.SectionName);
        services.AddHttpClient(OpenAiCompatibleEmbeddingProvider.HttpClientName, (sp, client) =>
        {
            var embeddingOptions = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, embeddingOptions.RequestTimeoutSeconds));
        });
        services.AddSingleton<IEmbeddingProvider, OpenAiCompatibleEmbeddingProvider>();

        return services;
    }

    /// <summary>
    /// Registers SelfHosted STT, Read Aloud TTS, alignment transcription,
    /// assistant LLM HTTP transport, AI access/usage policies, and data-protected
    /// AI provider settings persistence.
    /// </summary>
    public static IServiceCollection AddSelfHostedAiServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath,
        AssistantOptions assistantOptions)
    {
        ArgumentNullException.ThrowIfNull(assistantOptions);

        // --- SPEECH-TO-TEXT (issue #262 §2/§3) ---
        services.AddHttpClient(OpenAiCompatibleSttProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<OpenAiCompatibleSttProvider>();
        services.AddSingleton<ISTtProvider>(
            sp => sp.GetRequiredService<OpenAiCompatibleSttProvider>());

        // --- READ ALOUD: the server voice ---
        // Off until the owner connects a speech server (ReadAloud:Enabled + BaseUrl).
        // Registered either way, so the status endpoint can say "not configured"
        // rather than "this host has no such thing".
        services.AddOptions<ReadAloudOptions>()
            .BindConfiguration(ReadAloudOptions.SectionName);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ReadAloudOptions>>().Value);
        services.AddHttpClient(OpenAiCompatibleReadAloudProvider.HttpClientName, (sp, client) =>
        {
            var readAloudOptions = sp.GetRequiredService<IOptions<ReadAloudOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, readAloudOptions.RequestTimeoutSeconds));
        });
        services.AddSingleton<ReadAloudAudioCache>();
        services.AddSingleton<IReadAloudProvider, OpenAiCompatibleReadAloudProvider>();

        // Alignment transcription is a separate concrete service. It resolves
        // effective settings per call and never changes the voice STT registration.
        services.AddHttpClient(
                JsonBase64SttProvider.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(90))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

        services.AddSingleton<JsonBase64SttProvider>();
        services.AddSingleton<AlignmentSttProvider>();
        services.AddScoped<IAlignmentTranscriber, SettingsAlignmentTranscriber>();

        // --- ASSISTANT LLM BRIDGE (issue #261 §3, §7) ---
        // The transport timeout is the orchestrator's per-request clock plus a small
        // grace: a stalled call normally fails on the typed in-process clock first
        // (#1561). See AssistantOptions for the clock order.
        services.AddHttpClient(OpenAiCompatibleLlmProvider.HttpClientName, client =>
        {
            client.Timeout = assistantOptions.TransportTimeout;
        });
        services.AddSingleton<ILlmProvider, OpenAiCompatibleLlmProvider>();

        services.AddSingleton<IAiAccessPolicy, AllowAllAiAccessPolicy>();
        services.AddSingleton<IAiUsageAccountingService>(NoOpAiUsageAccountingService.Instance);

        // --- AI PROVIDER SETTINGS ---
        services.AddNostosSelfHostedDataProtection(configuration, contentRootPath);

        services.AddHttpClient(AiProviderSettingsService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddSingleton<AiProviderSettingsService>();
        services.AddSingleton<IAiProviderConfigResolver>(
            sp => sp.GetRequiredService<AiProviderSettingsService>());
        services.AddSingleton<IAiProviderSettingsService>(
            sp => sp.GetRequiredService<AiProviderSettingsService>());

        return services;
    }
}

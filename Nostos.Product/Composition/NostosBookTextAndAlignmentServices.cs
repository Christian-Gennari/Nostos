using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;

namespace Nostos.Product.Composition;

internal static class NostosBookTextAndAlignmentServices
{
    internal static IServiceCollection AddNostosBookTextServices(
        this IServiceCollection services)
    {
        services.AddSingleton<IBookTextExtractor, PdfBookTextExtractor>();
        services.AddSingleton<IBookTextExtractor, EpubBookTextExtractor>();
        services.TryAddScoped<IBookTextIndex, NoOpBookTextIndex>();
        services.TryAddScoped<IBookDerivedArtifactStorage, NoOpBookTextArtifactStorage>();
        services.TryAddScoped<IBookTextIngestionScheduler, NoOpBookTextIngestionScheduler>();
        services.TryAddScoped<IBookTextLifecycle, BookTextLifecycle>();
        services.AddScoped<BookTextIngestionEngine>();
        // Embeddings are optional: with no host-supplied provider or vector
        // store these fallbacks keep the embedding pass a no-op.
        services.TryAddSingleton<IEmbeddingProvider, NoOpEmbeddingProvider>();
        services.TryAddScoped<IBookTextEmbeddingIndex, NoOpBookTextEmbeddingIndex>();
        services.AddScoped<BookTextEmbeddingEngine>();
        services.AddScoped<IBookTextSearchService, BookTextSearchService>();
        services.AddScoped<IBookTextQuoteFinder, BookTextQuoteFinder>();
        services.TryAddScoped<IBookPassageAnchors, BookPassageAnchors>();
        services.AddScoped<BookTextBackfillService>();

        return services;
    }

    internal static IServiceCollection AddNostosAlignmentServices(
        this IServiceCollection services)
    {
        services.AddSingleton<AlignmentDocumentCache>();
        services.TryAddSingleton<IAlignmentDocumentProvider>(
            sp => sp.GetRequiredService<AlignmentDocumentCache>());
        services.TryAddScoped<IAudioClipCutter, FfmpegAudioClipCutter>();
        services.TryAddScoped<IAlignmentAnchorStore, EfAlignmentAnchorStore>();
        services.TryAddScoped<IAlignmentAudioRevisionProvider, AlignmentAudioRevisionProvider>();
        services.TryAddScoped<IAlignmentService, AlignmentService>();
        services.TryAddScoped<IAlignmentTranscriber, VoiceAlignmentTranscriber>();

        return services;
    }
}

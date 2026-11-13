using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Providers.LibriVox;
using Nostos.Backend.Providers.Litteraturbanken;
using Nostos.Backend.Providers.StandardEbooks;
using Nostos.Backend.Providers.Wikisource;

namespace Nostos.Product.Composition;

internal static class NostosProviderServices
{
    internal static IServiceCollection AddNostosProviderServices(
        this IServiceCollection services)
    {
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        // Provider enablement (issue #774): the one place the user's stored
        // choices are resolved for discovery, acquisition and Settings.
        services.AddScoped<IProviderEnablementService, ProviderEnablementService>();
        services.AddSingleton<ProviderDiscoveryService>();
        // Replaceable discovery seam: SelfHosted keeps the live provider
        // fan-out as its default, while a host can register its own
        // IProviderDiscovery (for example a catalog-backed implementation)
        // before this method and it wins.
        services.TryAddSingleton<IProviderDiscovery>(sp =>
            sp.GetRequiredService<ProviderDiscoveryService>());
        // Host-overridable metadata lookup for proxied covers. SelfHosted
        // resolves covers through the provider catalog; catalog-backed hosts
        // can reuse discovery metadata and avoid one detail request per card.
        services.TryAddSingleton<IProviderCoverLookup, ProviderCoverLookup>();
        services.TryAddSingleton<
            IAcquisitionWorkingRootProvider,
            DefaultAcquisitionWorkingRootProvider>();
        services.TryAddSingleton<IAudioDurationProbe, AtlAudioDurationProbe>();
        services.AddSingleton<IProviderContentDownloader, ProviderContentDownloader>();
        services.AddSingleton<BookFileMutationGate>();
        services.AddScoped<IAcquisitionService, AcquisitionService>();

        services.AddSingleton<IContentProvider, GutenbergProvider>();
        services.AddSingleton<IContentProvider, StandardEbooksProvider>();
        services.AddSingleton<IContentProvider, WikisourceProvider>();
        services.AddSingleton<IContentProvider, LitteraturbankenProvider>();
        services.AddSingleton<IContentProvider, LibriVoxProvider>();

        return services;
    }
}

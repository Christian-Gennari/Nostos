using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Discovery;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Keeps aggregate discovery honest about which providers this process actually
/// runs.
///
/// A discovery backend may be host-supplied — for example a shared catalog in a
/// Cloud deployment — and may know about providers this host has not registered
/// or the caller is not enabled to see. This boundary drops those rows before
/// they are serialised, so provider visibility is enforced at the HTTP edge
/// regardless of where the results came from.
/// </summary>
internal static class ProviderDiscoveryBoundary
{
    public static ProviderDiscoveryResult Apply(
        ProviderDiscoveryResult result,
        IProviderRegistry registry,
        IReadOnlySet<string>? allowedProviderIds)
    {
        var items = result.Items
            .Where(item => IsVisible(item.ProviderId, registry, allowedProviderIds))
            .ToList();

        var sources = result.Sources
            .Where(source => IsVisible(source.ProviderId, registry, allowedProviderIds))
            .ToList();

        return result with
        {
            Items = items,
            Sources = sources,
        };
    }

    private static bool IsVisible(
        string providerId,
        IProviderRegistry registry,
        IReadOnlySet<string>? allowedProviderIds) =>
        registry.Find(providerId) is not null
        && (allowedProviderIds is null || allowedProviderIds.Contains(providerId));
}

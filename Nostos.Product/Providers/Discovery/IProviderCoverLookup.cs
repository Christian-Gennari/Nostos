using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers;

namespace Nostos.Backend.Providers.Discovery;

/// <summary>
/// Resolves cover metadata for the cover proxy. Hosts with catalog-backed
/// discovery can serve the same snapshot metadata without a live item-detail
/// request for each rendered result. SelfHosted defaults to provider detail.
/// </summary>
public interface IProviderCoverLookup
{
    Task<ProviderCover?> GetCoverAsync(string providerId, string externalId, CancellationToken ct);
}

/// <summary>SelfHosted fallback that asks the registered provider catalog.</summary>
public sealed class ProviderCoverLookup(IProviderRegistry registry) : IProviderCoverLookup
{
    public async Task<ProviderCover?> GetCoverAsync(
        string providerId,
        string externalId,
        CancellationToken ct)
    {
        var catalog = registry.Find(providerId)?.Catalog;
        if (catalog is null)
            return null;

        var item = await catalog.GetItemAsync(externalId, ct);
        return item?.Cover;
    }
}

namespace Nostos.Backend.Providers.Contracts;

/// <summary>
/// Resolves a chosen item (plus an optional asset choice) into everything the
/// acquisition pipeline needs in order to fetch it: the ordered download parts
/// and the format the finished local file should take.
///
/// This is the single place remote URLs come from, and it runs server-side on
/// data the provider itself produced. A client can therefore only ever name a
/// provider, an item and an asset — never a URL — which is what stops the
/// acquisition endpoint from becoming an arbitrary-URL downloader.
/// </summary>
public interface IProviderAcquisitionPlanner
{
    /// <summary>
    /// Null when the item does not exist. Throws <see cref="ProviderException"/>
    /// when the item exists but the requested asset cannot be resolved.
    /// </summary>
    Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(ProviderAcquisitionRequest request, CancellationToken ct);
}

/// <param name="ExternalId">The provider's own item id.</param>
/// <param name="AssetId">Requested asset, or null to take the provider's preferred/default asset.</param>
public sealed record ProviderAcquisitionRequest(string ExternalId, string? AssetId);

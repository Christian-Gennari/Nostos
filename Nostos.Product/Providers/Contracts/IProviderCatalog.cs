namespace Nostos.Backend.Providers.Contracts;

/// <summary>Full detail for a single external item, including its downloadable assets.</summary>
public interface IProviderCatalog
{
    /// <summary>Null when the source has no such item. Throws <see cref="ProviderException"/> on source failure.</summary>
    Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct);
}

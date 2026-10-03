namespace Nostos.Backend.Providers.Contracts;

/// <summary>Find content in an external catalog by a normal user query.</summary>
public interface IProviderSearch
{
    /// <summary>
    /// Never throws for "nothing matched" — an empty page is a normal result.
    /// Throws <see cref="ProviderException"/> when the source itself failed
    /// (unreachable, malformed response) so the caller can tell the two apart.
    /// </summary>
    Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct);
}

using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Discovery;

/// <summary>
/// One aggregate discovery query.
/// </summary>
/// <param name="Query">The user's search text.</param>
/// <param name="Kind">Optional material kind to restrict the search to.</param>
/// <param name="Limit">Maximum number of merged items to return.</param>
/// <param name="ProviderIds">
/// The set of provider ids the caller may see. <see langword="null"/> means
/// every registered provider, which is the SelfHosted default.
/// </param>
public sealed record ProviderDiscoveryRequest(
    string Query,
    ProviderMediaKind? Kind,
    int Limit,
    IReadOnlySet<string>? ProviderIds = null);

/// <summary>
/// Provider-neutral discovery behind <c>GET /api/providers/search</c>. A host
/// (for example a Cloud deployment) can replace the live provider fan-out with
/// a catalog-backed implementation through DI; SelfHosted keeps the live
/// implementation as its default.
/// </summary>
/// <remarks>
/// <para>
/// Results use the existing <see cref="ProviderItem"/>,
/// <see cref="ProviderDiscoveryResult"/> and
/// <see cref="ProviderDiscoverySourceStatus"/> types, so replacing the backend
/// does not change the HTTP contract.
/// </para>
/// <para>
/// An implementation must respect <see cref="ProviderDiscoveryRequest.Kind"/>,
/// <see cref="ProviderDiscoveryRequest.Limit"/> and
/// <see cref="ProviderDiscoveryRequest.ProviderIds"/>: a provider outside the
/// set is neither searched nor reported in
/// <see cref="ProviderDiscoveryResult.Sources"/>.
/// </para>
/// <para>
/// Results are discovery metadata only and may be stale: acquisition always
/// re-resolves the selected item live through the provider adapter and verifies
/// the requested asset is still offered before downloading. An implementation
/// must be able to produce a renderable <see cref="ProviderItem"/>, including
/// cover metadata, without a per-result upstream detail call.
/// </para>
/// </remarks>
public interface IProviderDiscovery
{
    Task<ProviderDiscoveryResult> SearchAsync(
        ProviderDiscoveryRequest request,
        CancellationToken ct);
}

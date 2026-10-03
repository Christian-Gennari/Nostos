using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers;

/// <summary>
/// A provider together with the optional capability implementations it
/// actually supplies, resolved once at startup. Callers ask the registry for
/// this bundle instead of casting a provider to the interfaces it claims.
/// </summary>
public sealed record ProviderRegistration(
    IContentProvider Provider,
    IProviderSearch? Search,
    IProviderCatalog? Catalog,
    IProviderAcquisitionPlanner? Planner,
    IProviderDownloadPolicy? DownloadPolicy,
    IAcquisitionAssembler? Assembler)
{
    public string Id => Provider.Id;
}

/// <summary>Look up an external content source by its stable identifier.</summary>
public interface IProviderRegistry
{
    IReadOnlyList<ProviderRegistration> All { get; }

    /// <summary>Null when no provider is registered under that id.</summary>
    ProviderRegistration? Find(string providerId);
}

/// <summary>
/// Built from the <see cref="IContentProvider"/> services the container knows
/// about, so adding a provider is a normal DI registration and nothing else.
///
/// Construction is deliberately fail-fast. A provider whose declared
/// <see cref="ProviderCapabilities"/> disagree with the interfaces it
/// implements is a wiring bug that would otherwise surface as a confusing
/// runtime failure deep inside acquisition, so it stops the app at startup
/// instead.
/// </summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, ProviderRegistration> _byId = new(StringComparer.Ordinal);

    public ProviderRegistry(IEnumerable<IContentProvider> providers)
    {
        var registrations = new List<ProviderRegistration>();

        foreach (var provider in providers)
        {
            Validate(provider);

            var registration = new ProviderRegistration(
                provider,
                provider as IProviderSearch,
                provider as IProviderCatalog,
                provider as IProviderAcquisitionPlanner,
                provider as IProviderDownloadPolicy,
                provider as IAcquisitionAssembler);

            if (!_byId.TryAdd(registration.Id, registration))
                throw new InvalidOperationException(
                    $"Two content providers claim the id '{registration.Id}'. Provider ids are part of " +
                    "persisted provenance, so they must be unique.");

            registrations.Add(registration);
        }

        All = registrations;
    }

    public IReadOnlyList<ProviderRegistration> All { get; }

    public ProviderRegistration? Find(string providerId) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : _byId.GetValueOrDefault(providerId.Trim());

    private static void Validate(IContentProvider provider)
    {
        var id = provider.Id;
        if (string.IsNullOrWhiteSpace(id) || !IsValidId(id))
            throw new InvalidOperationException(
                $"Provider id '{id}' is invalid. Use lowercase letters, digits and hyphens, " +
                "starting with a letter or digit (max 32 characters).");

        if (string.IsNullOrWhiteSpace(provider.DisplayName))
            throw new InvalidOperationException($"Provider '{id}' must have a display name.");

        var caps = provider.Capabilities;

        CheckPair(id, caps.HasFlag(ProviderCapabilities.Search), provider is IProviderSearch,
            "ProviderCapabilities.Search", nameof(IProviderSearch));
        CheckPair(id, caps.HasFlag(ProviderCapabilities.ItemRetrieval), provider is IProviderCatalog,
            "ProviderCapabilities.ItemRetrieval", nameof(IProviderCatalog));

        var claimsAcquisition = caps.HasFlag(ProviderCapabilities.EbookAcquisition)
            || caps.HasFlag(ProviderCapabilities.AudiobookAcquisition);
        CheckPair(id, claimsAcquisition, provider is IProviderAcquisitionPlanner,
            "ProviderCapabilities.{Ebook,Audiobook}Acquisition", nameof(IProviderAcquisitionPlanner));
        CheckPair(id, claimsAcquisition, provider is IProviderDownloadPolicy,
            "ProviderCapabilities.{Ebook,Audiobook}Acquisition", nameof(IProviderDownloadPolicy));

        // Declaring assembly without an assembler would mean a multi-part item
        // silently ships its parts to the library as if they were the artifact.
        CheckPair(id, caps.HasFlag(ProviderCapabilities.RequiresAssembly), provider is IAcquisitionAssembler,
            "ProviderCapabilities.RequiresAssembly", nameof(IAcquisitionAssembler));

        if (provider is not IProviderDownloadPolicy policy)
            return;

        if (policy.AllowedHosts.Count == 0)
            throw new InvalidOperationException(
                $"Provider '{id}' allows no download hosts, so nothing could ever be acquired from it.");
        if (policy.MaxBytesPerPart <= 0 || policy.MaxTotalBytes <= 0 || policy.MaxParts <= 0)
            throw new InvalidOperationException($"Provider '{id}' must declare positive download limits.");
        if (policy.MaxBytesPerPart > policy.MaxTotalBytes)
            throw new InvalidOperationException(
                $"Provider '{id}' has MaxBytesPerPart above MaxTotalBytes, so the per-part cap would never apply.");
    }

    private static void CheckPair(string id, bool declared, bool implemented, string flag, string iface)
    {
        if (declared != implemented)
            throw new InvalidOperationException(
                $"Provider '{id}' declares {flag} = {declared} but {(implemented ? "does" : "does not")} " +
                $"implement {iface}. Capabilities and interfaces must agree.");
    }

    private static bool IsValidId(string id)
    {
        if (id.Length > 32 || !char.IsAsciiLetterOrDigit(id[0]))
            return false;

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        }

        return id == id.ToLowerInvariant();
    }
}

namespace Nostos.Backend.Providers.Contracts;

/// <summary>
/// Identity of an external content source — "what content exists outside
/// Nostos".
///
/// A provider only *describes* remote content. It must never write library or
/// database state, touch storage files, or carry reader behaviour: turning a
/// described item into a local book is the acquisition layer's job
/// (<c>IAcquisitionService</c>). Additional behaviour is opt-in through the
/// capability interfaces rather than one large interface every provider has to
/// stub out:
///
/// <list type="bullet">
/// <item><see cref="IProviderSearch"/> — find items by user query.</item>
/// <item><see cref="IProviderCatalog"/> — full detail for one item id.</item>
/// <item><see cref="IProviderAcquisitionPlanner"/> — resolve item+asset into concrete downloads.</item>
/// <item><see cref="IProviderDownloadPolicy"/> — which hosts may be fetched, and within what bounds.</item>
/// </list>
/// </summary>
public interface IContentProvider
{
    /// <summary>
    /// Stable identifier. Persisted as acquisition provenance and sent by
    /// clients, so it is part of the app's durable surface: lowercase, and
    /// never rename it once it has shipped (an existing book's provenance row
    /// would stop resolving to a provider).
    /// </summary>
    string Id { get; }

    /// <summary>Name shown to the user when choosing a source.</summary>
    string DisplayName { get; }

    /// <summary>See <see cref="ProviderCapabilities"/>.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// A short, source-supplied rights note shown next to results, e.g.
    /// "Public domain in the USA (Project Gutenberg)". Deliberately a statement
    /// quoted from the source rather than a Nostos claim: no provider states
    /// that an item is unrestricted in every jurisdiction, and Nostos must not
    /// imply it either. Null when the source offers no statement.
    /// </summary>
    string? RightsNotice { get; }
}

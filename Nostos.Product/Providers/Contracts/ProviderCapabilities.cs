namespace Nostos.Backend.Providers.Contracts;

/// <summary>
/// What a provider can offer. Declared explicitly so a caller can decide what
/// to ask a source for without casting to a concrete provider type, and so the
/// UI can avoid offering a source that cannot do what the user wants.
///
/// <see cref="ProviderRegistry"/> cross-checks these flags against the optional
/// capability interfaces the provider actually implements and refuses to start
/// on a mismatch, so the flags cannot quietly drift away from the code.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,

    /// <summary>Implements <see cref="IProviderSearch"/>.</summary>
    Search = 1 << 0,

    /// <summary>Implements <see cref="IProviderCatalog"/>.</summary>
    ItemRetrieval = 1 << 1,

    /// <summary>Implements <see cref="IProviderAcquisitionPlanner"/> and can deliver ebooks.</summary>
    EbookAcquisition = 1 << 2,

    /// <summary>Implements <see cref="IProviderAcquisitionPlanner"/> and can deliver audiobooks.</summary>
    AudiobookAcquisition = 1 << 3,

    /// <summary>Item detail includes cover artwork.</summary>
    CoverArt = 1 << 4,

    /// <summary>Item detail includes a rights/public-domain statement from the source.</summary>
    RightsInformation = 1 << 5,

    /// <summary>
    /// Implements <see cref="IAcquisitionAssembler"/>: what the source delivers
    /// is not yet one playable file and has to be combined first.
    /// </summary>
    RequiresAssembly = 1 << 6,
}

/// <summary>The kind of reading material an item or asset represents.</summary>
public enum ProviderMediaKind
{
    Ebook = 0,
    Audiobook = 1,
}

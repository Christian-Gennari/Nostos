namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Public prepared-import entry point of the portable archive engine.
/// </summary>
/// <remarks>
/// <para>
/// A hosted adapter in another assembly uses this seam to run the streaming
/// prepared import without going through the SelfHosted migration job engine:
/// it supplies its own <see cref="IPortableArchiveSource"/> and
/// <see cref="IPortableImportStaging"/> and receives the durable
/// <see cref="IPreparedPortableImport"/> that the public verifier
/// (<see cref="IPortableLibraryVerifier"/>) and a host-owned activation step
/// consume.
/// </para>
/// <para>
/// The implementation is the product's archive reader. It fully validates the
/// archive — manifest, relational payload, inventory, media lengths and hashes —
/// and stages every byte before returning; on any failure or cancellation the
/// staging area is deleted before the typed error is rethrown. Preparation never
/// mutates the active library, the database, or host asset storage. The reader
/// owns its range cache and buffer budget; callers only see this interface.
/// </para>
/// </remarks>
public interface IPortableImportPreparer
{
    /// <summary>
    /// Validates a portable archive from a position-independent source and
    /// stages its relational payload, manifest and every media entry into
    /// <paramref name="staging"/>.
    /// </summary>
    /// <param name="source">The archive bytes; read only through bounded ranges.</param>
    /// <param name="staging">The host-controlled prepared-import staging area.</param>
    /// <param name="progress">Optional progress sink, independent of any migration job.</param>
    /// <param name="cancellationToken">Cancels preparation; staged bytes are deleted.</param>
    /// <returns>The verified prepared import, reconstructible from staging alone.</returns>
    Task<IPreparedPortableImport> PrepareImportAsync(
        IPortableArchiveSource source,
        IPortableImportStaging staging,
        IProgress<PortableArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

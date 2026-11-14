using Nostos.Backend.Data;
using Nostos.Product.BookText;
using Nostos.Product.Storage;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// The portable archive entry point: export and legacy import behind
/// <see cref="IPortableArchiveService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The work lives in two focused collaborators:
/// <see cref="PortableArchiveExportWriter"/> (snapshot, media pin, streaming ZIP)
/// and <see cref="PortableArchiveImportReader"/> (scratch spool, prepare, apply,
/// verify, commit/rollback). This class owns the service contract, the host
/// dependencies and the test seams the host and the migration job install.
/// </para>
/// <para>
/// Both paths share the portable primitives rather than their own copies: the
/// snapshot projection (<see cref="PortableLibrarySnapshot"/>), the hashing helpers
/// (<see cref="PortableArchiveHashing"/>), the relational restore
/// (<see cref="PortableLibraryDatabaseMaterializer"/>) and post-import verification
/// (<see cref="Verification.PortableImportIntegrityVerifier"/>).
/// </para>
/// </remarks>
public sealed class PortableArchiveService(
    NostosDbContext db,
    IBookAssetStorage assets,
    ILogger<PortableArchiveService> logger,
    IBookTextIngestionScheduler? bookTextScheduler = null,
    TimeProvider? timeProvider = null,
    IBookTrackStorage? trackStorage = null,
    IProductScratchStorage? scratchStorage = null)
    : IPortableArchiveService
{
    private readonly PortableArchiveExportWriter _exportWriter = new(
        db,
        assets,
        trackStorage,
        timeProvider ?? TimeProvider.System);

    private readonly PortableArchiveImportReader _importReader = new(
        db,
        assets,
        trackStorage,
        logger,
        bookTextScheduler,
        timeProvider ?? TimeProvider.System);

    // The relational JSON cap is injectable so the preflight boundary can be
    // exercised without serializing a 64 MiB payload.
    internal long MaxExportDataBytes { get; init; } = PortableArchiveLimits.MaxDataBytes;

    // Test seam: the directory that receives the per-import scratch directory
    // (upload spool plus local staging). A host that names a scratch root —
    // SelfHosted puts it on the library's volume — wins over the process temp
    // path, which is the fallback for a host that has not adopted the seam.
    internal string ScratchRoot { get; init; } = scratchStorage?.Root ?? ProductScratchStorage.TempPath.Root;

    // Test seam: free-space probe for the scratch volume. Production uses DriveInfo.
    internal Func<string, long?>? ScratchFreeSpaceProbe { get; init; }

    public Task<PortableExportResult> ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default) =>
        _exportWriter.ExportAsync(
            destination,
            progress: null,
            PortableArchiveExportWriter.CreateBufferBudget(),
            MaxExportDataBytes,
            cancellationToken);

    public async Task<PortableExportResult> ExportAsync(
        IPortableArchiveSink destination,
        IProgress<PortableArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var writer = await destination.OpenWriteAsync(cancellationToken);
        return await _exportWriter.ExportAsync(
            writer,
            progress,
            PortableArchiveExportWriter.CreateBufferBudget(),
            MaxExportDataBytes,
            cancellationToken);
    }

    // Shared-budget overload for callers that must account the export against
    // an operation-wide PortableArchiveBufferBudget (and for the adapter
    // high-water tests).
    internal Task<PortableExportResult> ExportAsync(
        Stream destination,
        PortableArchiveBufferBudget exportBufferBudget,
        CancellationToken cancellationToken = default) =>
        _exportWriter.ExportAsync(
            destination,
            progress: null,
            exportBufferBudget,
            MaxExportDataBytes,
            cancellationToken);

    // Shared-budget sink overload for the migration export job: the 16 MiB
    // synchronous capture lease is charged to the caller's per-job operation
    // budget, so concurrent exports each account their own capture buffer.
    internal async Task<PortableExportResult> ExportAsync(
        IPortableArchiveSink destination,
        IProgress<PortableArchiveProgress>? progress,
        PortableArchiveBufferBudget exportBufferBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(exportBufferBudget);

        await using var writer = await destination.OpenWriteAsync(cancellationToken);
        return await _exportWriter.ExportAsync(
            writer,
            progress,
            exportBufferBudget,
            MaxExportDataBytes,
            cancellationToken);
    }

    public Task<PortableImportResult> ImportAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        _importReader.ImportAsync(
            source,
            ScratchRoot,
            ScratchFreeSpaceProbe,
            cancellationToken);
}

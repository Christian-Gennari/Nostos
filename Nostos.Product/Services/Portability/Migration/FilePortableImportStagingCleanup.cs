using Microsoft.Extensions.Logging;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Sweeps abandoned file-backed prepared-import staging areas. The cleanup
/// engine supplies a full TTL cutoff and the set of staging identifiers that
/// non-terminal jobs still own; only generated GUID-N directories older than
/// the cutoff and absent from that set are deleted through the provider's
/// durable deletion marker. Unknown siblings, operator files, and linked
/// components are never touched. Registered by the SelfHosted host beside the
/// staging provider, not by the transport-independent engine.
/// </summary>
public sealed class FilePortableImportStagingCleanup(
    TransferPathResolver paths,
    ILogger<FilePortableImportStagingCleanup> logger) : IMigrationStagingCleanup
{
    public Task DeleteAsync(PortableStagingId stagingId, CancellationToken ct) =>
        new FilePortableImportStaging(paths).DeleteAsync(stagingId, ct);

    public async Task CleanupAbandonedAsync(
        DateTimeOffset cutoffUtc,
        IReadOnlySet<PortableStagingId> protectedIds,
        CancellationToken ct)
    {
        var root = paths.VerifyPathWithinRoot(paths.GetStagingRoot());
        if (!Directory.Exists(root))
        {
            return;
        }

        var provider = new FilePortableImportStaging(paths);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var value)
                || value == Guid.Empty
                || protectedIds.Contains(new PortableStagingId(value)))
            {
                continue;
            }

            try
            {
                paths.VerifyPathWithinRoot(directory);
                var tombstoned = File.Exists(
                    Path.Combine(directory, FilePortableImportStaging.DeletedMarkerFileName));

                // A durably tombstoned area is already logically deleted: its
                // physical leftovers are sweepable immediately, because the
                // marker (not the directory) is what hides the staging id and
                // prevents a new writer from adopting it. Non-tombstoned areas
                // keep the full TTL grace measured by the newest write anywhere
                // inside them, so an area still receiving media is never
                // mistaken for an abandoned one.
                if (!tombstoned
                    && TransferPathResolver.NewestWriteTimeUtc(directory) > cutoffUtc.UtcDateTime)
                {
                    // The full TTL grace has not elapsed for this area.
                    continue;
                }

                await provider.DeleteAsync(new PortableStagingId(value), ct);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or TransferPathException
                    or PortableStagingException)
            {
                logger.LogWarning(
                    exception,
                    "Abandoned staging cleanup will retry {StagingId}",
                    value);
            }
        }
    }
}

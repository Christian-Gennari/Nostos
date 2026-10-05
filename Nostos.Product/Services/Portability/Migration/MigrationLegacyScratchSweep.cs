using Microsoft.Extensions.Logging;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Runs the legacy compatibility-scratch sweep as part of the same cleanup
/// worker batch as the durable transfer sweep (issue #679 Slice 11). The worker
/// already holds maintenance admission and disposes its scope before releasing
/// it, so this hook inherits the same gate without a second admission.
///
/// <para>The scratch root is the process temp path, matching
/// <c>PortableArchiveService.ScratchRoot</c> on a real host. Tests inject an
/// isolated root through <see cref="ScratchRoot"/>.</para>
/// </summary>
public sealed class MigrationLegacyScratchSweep(
    LegacyPortabilityScratchCleanup cleanup,
    ILogger<MigrationLegacyScratchSweep> logger)
{
    /// <summary>The root the legacy import path spools into; overridable for tests.</summary>
    public string ScratchRoot { get; set; } = Path.GetTempPath();

    public void Sweep(DateTimeOffset nowUtc, CancellationToken ct)
    {
        try
        {
            cleanup.Sweep(
                ScratchRoot,
                nowUtc - LegacyPortabilityScratchCleanup.MinimumAge,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Legacy portability scratch sweep failed; the next sweep will retry");
        }
    }
}

using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Physical free-space measurement for an arbitrary path's volume. Injected so
/// boundary tests can simulate full volumes and so separate database/media
/// volumes are measured independently (issue #681, plan section 16.3).
/// </summary>
internal interface ISelfHostedVolumeSpaceProbe
{
    long AvailableFreeSpaceBytes(string path);

    long TotalSizeBytes(string path);

    /// <summary>True when both paths resolve to the same mounted volume.</summary>
    bool AreSameVolume(string first, string second);
}

/// <summary>
/// Real volume probe. Linux uses <c>statvfs</c> on the path (so a bind mount or
/// nested mount is measured for itself, not for the filesystem root) and
/// <c>statx_mnt_id</c> for volume identity; Windows uses the path's drive root.
/// Other platforms refuse activation rather than guessing which volume a path
/// is on.
/// </summary>
internal sealed class VolumeSpaceProbe : ISelfHostedVolumeSpaceProbe
{
    private readonly ActivationVolume _identity = new();

    public long AvailableFreeSpaceBytes(string path) => Measure(path).Available;

    public long TotalSizeBytes(string path) => Measure(path).Total;

    public bool AreSameVolume(string first, string second) => _identity.SameVolume(first, second);

    private static (long Available, long Total) Measure(string path)
    {
        var existing = path;
        while (!File.Exists(existing) && !Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? throw new IOException("No existing volume ancestor.");

        if (OperatingSystem.IsLinux())
        {
            var buffer = new byte[256];
            if (statvfs(existing, buffer) != 0)
                throw new IOException("Cannot measure the activation volume.");
            var fragment = (long)BitConverter.ToUInt64(buffer, 8);
            var blocks = (long)BitConverter.ToUInt64(buffer, 16);
            var available = (long)BitConverter.ToUInt64(buffer, 32);
            return (available * fragment, blocks * fragment);
        }

        if (OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(Path.GetPathRoot(existing)
                ?? throw new IOException("The activation path has no drive root."));
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }

        throw new PlatformNotSupportedException("Activation volume measurement supports Linux and Windows.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statvfs(string path, [Out] byte[] buffer);
}

/// <summary>
/// Exact activation requirements measured before the maintenance window. The
/// candidate bytes are new physical bytes; the previous library bytes are
/// already on disk and are retained by rename, so they are not charged again
/// as new space — they become a durable capacity claim instead.
/// </summary>
internal sealed record SelfHostedActivationSizing(
    long CandidateDatabaseBytes,
    long CandidateMediaBytes,
    long PreviousDatabaseBytes,
    long PreviousMediaBytes,
    long StagedBytes = 0)
{
    internal long PreviousBytes => PreviousDatabaseBytes + PreviousMediaBytes;
}

/// <summary>
/// Activation admission arithmetic. Components are grouped by the volume that
/// will physically hold them — database candidate, media candidate and any
/// remaining staging — and the sum of each group's new bytes must fit that
/// volume's free space after the same global safety margin the transfer
/// capacity service applies, while the previously live library remains on
/// disk. A shared volume therefore charges both candidates together; separate
/// volumes are admitted independently.
/// <para>
/// The durable reservation remains the race-control mechanism: retention is
/// only admitted when its extra claim fits the transfer volume's usable
/// capacity, and finalisation re-checks at the boundary. Failures are typed
/// and never carry paths.
/// </para>
/// </summary>
internal sealed class SelfHostedActivationCapacity(
    ISelfHostedVolumeSpaceProbe probe,
    IOptions<TransferStorageOptions> options)
{
    internal void EnsureActivationFits(
        string liveDatabase,
        string liveMedia,
        string? transferRoot,
        SelfHostedActivationSizing sizing)
    {
        ArgumentNullException.ThrowIfNull(sizing);
        var components = new List<(string Path, long Bytes)>
        {
            (liveDatabase, sizing.CandidateDatabaseBytes),
            (liveMedia, sizing.CandidateMediaBytes),
        };
        if (transferRoot is not null) components.Add((transferRoot, sizing.StagedBytes));

        var groups = new List<(string Path, long Bytes)>();
        foreach (var component in components)
        {
            if (component.Bytes <= 0) continue;
            var index = groups.FindIndex(group => probe.AreSameVolume(group.Path, component.Path));
            if (index >= 0) groups[index] = (groups[index].Path, checked(groups[index].Bytes + component.Bytes));
            else groups.Add(component);
        }

        foreach (var group in groups) EnsureFitsOnVolume(group.Path, group.Bytes);
    }

    /// <summary>
    /// The retention reservation adds only the part of the previous library not
    /// already covered by this job's outstanding transfer claim. That added
    /// amount must fit the transfer volume's usable capacity.
    /// </summary>
    internal void EnsureRetentionClaimFits(TransferCapacitySnapshot snapshot, long previousBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var requiredAdditional = Math.Max(previousBytes - snapshot.OutstandingReservedBytes, 0);
        if (requiredAdditional > snapshot.UsableAvailableBytes) throw Exhausted();
    }

    private void EnsureFitsOnVolume(string componentPath, long newBytes)
    {
        var total = probe.TotalSizeBytes(componentPath);
        var available = probe.AvailableFreeSpaceBytes(componentPath);
        var margin = TransferCapacityMath.CalculateGlobalSafetyMarginBytes(total, options.Value);
        if (available - margin < newBytes) throw Exhausted();
    }

    internal static MigrationActivationException Exhausted() => new(MigrationActivationErrorCodes.StorageExhausted,
        "There is not enough storage to build and retain the library safely.");
}

using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services;

/// <summary>
/// Measures the physical volume that actually holds the configured transfer
/// root (issue #679, plan section 2.6). The capacity service depends on
/// <see cref="ITransferVolume"/> so tests can simulate a full disk without
/// filling the developer machine.
/// </summary>
public sealed class DriveInfoTransferVolume : ITransferVolume
{
    private readonly string _volumeRoot;

    public DriveInfoTransferVolume(string transferRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferRootPath);

        var root = Path.GetFullPath(transferRootPath);
        _volumeRoot = Path.GetPathRoot(root)
            ?? throw new InvalidOperationException(
                "The configured transfer root does not resolve to a measurable volume.");
    }

    public long AvailableFreeSpaceBytes => new DriveInfo(_volumeRoot).AvailableFreeSpace;

    public long TotalSizeBytes => new DriveInfo(_volumeRoot).TotalSize;
}

using System.Runtime.InteropServices;
using System.Text;

namespace Nostos.Backend.Services.Portability.Activation;

internal interface IActivationVolume
{
    bool SameVolume(string first, string second);
}

/// <summary>
/// Linux compares mount IDs, including bind mounts (device IDs alone miss EXDEV).
/// Windows compares volume mount paths. Other platforms refuse activation rather
/// than treating a common pathname prefix as proof of atomic rename support.
/// </summary>
internal sealed class ActivationVolume : IActivationVolume
{
    public bool SameVolume(string first, string second) => Identity(first) == Identity(second);

    private static string Identity(string path)
    {
        while (!File.Exists(path) && !Directory.Exists(path))
            path = Path.GetDirectoryName(path) ?? throw new IOException("No existing volume ancestor.");
        if (OperatingSystem.IsLinux())
        {
            var stat = new byte[256];
            if (statx(-100, path, 0, 0x1000, stat) != 0 || (BitConverter.ToUInt32(stat, 0) & 0x1000) == 0)
                throw new IOException("Cannot identify activation volume.");
            return BitConverter.ToUInt64(stat, 144).ToString(); // statx_mnt_id
        }
        if (OperatingSystem.IsWindows())
        {
            var result = new StringBuilder(1024);
            if (!GetVolumePathName(path, result, result.Capacity))
                throw new IOException("Cannot identify activation volume.");
            return result.ToString().ToUpperInvariant();
        }
        throw new PlatformNotSupportedException("Activation volume checks support Linux and Windows.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string path, int flags, uint mask, [Out] byte[] stat);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volumePath, int length);
}

/// <summary>
/// Flushes file contents before journal publication. On Linux fsyncs directories
/// after creating ancestors and after every rename (both parents for a move).
/// Directory fsync errors are fatal. Windows uses MoveFileEx WRITE_THROUGH;
/// portable directory fsync is unavailable there: process-crash recovery is
/// supported, but directory-entry survival on power loss depends on the host FS.
/// Storage hardware must honor flushes. No copy fallback is permitted.
/// </summary>
internal static class ActivationFileSystem
{
    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fd = open(path, 0x10000); // O_RDONLY | O_DIRECTORY
        if (fd < 0) throw new IOException("Cannot open activation directory for durability.");
        try
        {
            if (fsync(fd) != 0) throw new IOException("Cannot flush activation directory.");
        }
        finally { close(fd); }
    }

    public static void Rename(string source, string target, bool overwrite = false)
    {
        if (!overwrite && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException("Activation rename destination already exists.");
        if (OperatingSystem.IsLinux())
        {
            if (rename(source, target) != 0) throw new IOException("Atomic activation rename failed.");
        }
        else if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(source, target, 8 | (overwrite ? 1 : 0)))
                throw new IOException("Atomic activation rename failed.");
        }
        else throw new PlatformNotSupportedException("Atomic activation renames support Linux and Windows.");
        FlushDirectory(Path.GetDirectoryName(source)!);
        if (Path.GetDirectoryName(source) != Path.GetDirectoryName(target))
            FlushDirectory(Path.GetDirectoryName(target)!);
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int rename(string source, string target);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string target, int flags);
}

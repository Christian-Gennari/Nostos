using System.Collections.Concurrent;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Process-local registry of generated scratch directories with a live legacy
/// import (issue #679 Slice 11). The cleanup sweep consults it before deleting
/// anything, so an import that outlives its directory timestamp cannot be
/// removed underneath its request.
/// </summary>
public static class LegacyPortabilityScratchRegistry
{
    private static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.Ordinal);

    public static void Register(string directory) =>
        Active.TryAdd(Normalize(directory), 0);

    public static void Unregister(string directory) =>
        Active.TryRemove(Normalize(directory), out _);

    public static bool IsActive(string directory) =>
        Active.ContainsKey(Normalize(directory));

    private static string Normalize(string directory) => Path.GetFullPath(directory);
}

/// <summary>
/// Exclusive ownership lease for one generated legacy-import scratch tree.
///
/// <para>The lease holds <c>import.lock</c> with <see cref="FileShare.None"/>
/// for the whole import and registers the directory in
/// <see cref="LegacyPortabilityScratchRegistry"/>. The sweep skips any
/// directory that is registered active or whose lock is held, and only applies
/// its age gate to directories with no live owner. On Unix, .NET maps
/// <see cref="FileShare.None"/> to an advisory <c>flock</c>, so the lock also
/// excludes another .NET process on the same host; non-.NET writers are outside
/// the threat model.</para>
/// </summary>
public sealed class LegacyPortabilityScratchLease : IDisposable
{
    public const string LockFileName = "import.lock";

    private readonly FileStream _stream;
    private readonly string _directory;

    private LegacyPortabilityScratchLease(FileStream stream, string directory)
    {
        _stream = stream;
        _directory = directory;
    }

    /// <summary>
    /// Acquires the lease for <paramref name="directory"/>, creating the lock
    /// file when missing. Throws <see cref="IOException"/> when another owner
    /// already holds it.
    /// </summary>
    public static LegacyPortabilityScratchLease Acquire(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var full = Path.GetFullPath(directory);
        Directory.CreateDirectory(full);
        var stream = new FileStream(
            Path.Combine(full, LockFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
        LegacyPortabilityScratchRegistry.Register(full);
        return new LegacyPortabilityScratchLease(stream, full);
    }

    /// <summary>
    /// True when another owner holds the lock (this or another .NET process).
    /// A missing lock file or an acquirable one means no live owner.
    /// </summary>
    public static bool IsHeld(string directory)
    {
        var lockPath = Path.Combine(Path.GetFullPath(directory), LockFileName);
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // A lock file we cannot even open is owned by someone else.
            return true;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        LegacyPortabilityScratchRegistry.Unregister(_directory);
    }
}

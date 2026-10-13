using Microsoft.Extensions.Options;

namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// The configured external folder, and the only code that turns a reference
/// into an open file.
///
/// Every open walks the reference from the root one segment at a time and
/// refuses a symbolic link at any step, so a reference validated when it was
/// scanned is validated again when it is read. Files are only ever opened for
/// reading; this type has no member that writes, renames or deletes.
/// </summary>
public sealed class ExternalMediaRoot : IExternalMediaAvailability
{
    private const int ReadBufferSize = 81920;

    private readonly string? _root;

    public ExternalMediaRoot(IOptions<ExternalMediaOptions> options)
    {
        var value = options.Value;
        var configured = value.Root?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            RootId = ExternalMediaOptions.DefaultRootId;
            return;
        }

        if (!Path.IsPathFullyQualified(configured))
        {
            throw new InvalidOperationException(
                $"'{ExternalMediaOptions.SectionName}:Root' must be an absolute path to the mounted media folder.");
        }

        var rootId = value.RootId?.Trim();
        if (string.IsNullOrEmpty(rootId) || rootId.Length > 64)
        {
            throw new InvalidOperationException(
                $"'{ExternalMediaOptions.SectionName}:RootId' must be 1 to 64 characters.");
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        RootId = rootId;
    }

    public bool IsConfigured => _root is not null;

    public string RootId { get; }

    /// <summary>False when the root is unset, or set but not currently mounted.</summary>
    public bool IsMounted => _root is not null && Directory.Exists(_root);

    /// <summary>
    /// Describes the file a reference names, or null when the reference is
    /// for another root, is malformed, passes through a symbolic link, or
    /// names nothing that is a readable regular file right now.
    /// </summary>
    public ExternalMediaFile? Find(string rootId, string relativePath)
    {
        var path = Resolve(rootId, relativePath);
        if (path is null)
            return null;

        var file = new FileInfo(path);
        return new ExternalMediaFile(
            relativePath,
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    /// <summary>Opens the file a reference names for reading, under the same checks as <see cref="Find"/>.</summary>
    public FileStream? OpenRead(string rootId, string relativePath)
    {
        var path = Resolve(rootId, relativePath);
        if (path is null)
            return null;

        try
        {
            // ReadWrite sharing: the folder belongs to the administrator's
            // other tools, and reading it must never get in their way.
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists every regular file below the root, depth first, as paths that
    /// <see cref="OpenRead"/> would accept. Symbolic links and hidden entries
    /// are skipped rather than followed, a folder that cannot be read is
    /// skipped rather than fatal, and the walk stops at
    /// <paramref name="maxFiles"/> so a mistaken mount cannot run unbounded.
    /// </summary>
    public ExternalMediaListing List(int maxFiles, CancellationToken ct)
    {
        var files = new List<ExternalMediaFile>();
        var listing = new ExternalMediaListing(files);
        if (_root is null || !Directory.Exists(_root))
            return listing;

        var pending = new Stack<(string Path, string Relative, int Depth)>();
        pending.Push((_root, string.Empty, 0));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, relative, depth) = pending.Pop();

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(directory).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                listing.UnreadableFolders++;
                continue;
            }

            Array.Sort(entries, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            // Reversed onto the stack so folders are visited in name order.
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry.Name.StartsWith('.'))
                    continue;

                if (IsLink(entry))
                {
                    listing.SkippedLinks++;
                    continue;
                }

                var entryRelative = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (!ExternalMediaRelativePath.IsValid(entryRelative))
                {
                    listing.UnusableNames++;
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    if (depth + 1 >= MaxDepth)
                        listing.UnreadableFolders++;
                    else
                        pending.Push((entry.FullName, entryRelative, depth + 1));
                }
            }

            foreach (var entry in entries)
            {
                if (entry is not FileInfo file || entry.Name.StartsWith('.') || IsLink(entry))
                    continue;

                var entryRelative = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (!ExternalMediaRelativePath.IsValid(entryRelative))
                    continue;

                if (files.Count >= maxFiles)
                {
                    listing.Truncated = true;
                    return listing;
                }

                files.Add(new ExternalMediaFile(
                    entryRelative,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }

        return listing;
    }

    private const int MaxDepth = 32;

    private string? Resolve(string rootId, string relativePath)
    {
        if (_root is null
            || !string.Equals(rootId, RootId, StringComparison.Ordinal)
            || !ExternalMediaRelativePath.TryGetSegments(relativePath, out var segments))
            return null;

        try
        {
            var current = _root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                FileSystemInfo entry = i == segments.Length - 1
                    ? new FileInfo(current)
                    : new DirectoryInfo(current);

                if (!entry.Exists || IsLink(entry))
                    return null;
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}

/// <summary>A file below the external root, as it is right now.</summary>
public sealed record ExternalMediaFile(string RelativePath, long Length, DateTimeOffset LastModified);

/// <summary>The files found by one walk of the root, and what the walk passed over.</summary>
public sealed class ExternalMediaListing(IReadOnlyList<ExternalMediaFile> files)
{
    public IReadOnlyList<ExternalMediaFile> Files { get; } = files;

    /// <summary>Symbolic links that were not followed.</summary>
    public int SkippedLinks { get; set; }

    /// <summary>Folders that could not be read or were nested too deeply.</summary>
    public int UnreadableFolders { get; set; }

    /// <summary>Entries whose name cannot be stored as a reference.</summary>
    public int UnusableNames { get; set; }

    /// <summary>True when the walk stopped at its file limit.</summary>
    public bool Truncated { get; set; }
}

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

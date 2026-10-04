// Nostos.Product/Services/Portability/Transfers/TransferPathResolver.cs

namespace Nostos.Backend.Services.Portability.Transfers;

/// <summary>
/// Typed failure raised when a transfer path or storage key would escape the
/// configured transfer root or is otherwise malformed.
/// </summary>
public sealed class TransferPathException : Exception
{
    public const string InvalidPath = "transfer_path_invalid";
    public const string OutsideRoot = "transfer_path_outside_root";
    public const string ReparsePoint = "transfer_path_reparse_point";

    public TransferPathException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>
/// The single path authority for local transfer storage (issue #679, plan
/// sections 2.2 and 2.5). Every transfer service resolves directories and
/// files through this type; no user-supplied name is ever concatenated into a
/// filesystem path.
///
/// Guarantees:
/// <list type="bullet">
/// <item>generated paths always live strictly under the configured root;</item>
/// <item>persisted storage keys are relative, slash-separated, and re-validated
/// before any filesystem use;</item>
/// <item>traversal segments, rooted paths, alternate separators, invalid
/// characters, and Windows reserved device names are rejected;</item>
/// <item>existing components below the root that are symlinks or reparse
/// points are refused before a write can traverse them.</item>
/// </list>
/// </summary>
public sealed class TransferPathResolver
{
    public const string UploadsDirectoryName = "uploads";
    public const string StagingDirectoryName = "staging";
    public const string ExportsDirectoryName = "exports";
    public const string MediaDirectoryName = "media";
    public const string ArchivePartFileName = "archive.part";
    public const string ArchiveFileName = "archive.nostos";
    public const string ExportTempFileName = "library.nostos.tmp";
    public const string ExportFileName = "library.nostos";

    private const int MaxStorageKeyLength = 512;
    private const int MaxSegmentLength = 255;
    private const int MaxTokenLength = 128;

    private static readonly char[] InvalidSegmentCharacters =
        ['\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly string[] ReservedWindowsNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly string _rootPath;
    private readonly StringComparison _comparison;

    public TransferPathResolver(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var full = Path.GetFullPath(rootPath);
        if (string.IsNullOrEmpty(full)
            || string.Equals(full, Path.GetPathRoot(full), PathComparison(full)))
        {
            throw new TransferPathException(
                TransferPathException.InvalidPath,
                "A transfer root must be a directory below the filesystem root.");
        }

        _rootPath = full;
        _comparison = PathComparison(full);
    }

    /// <summary>The canonical absolute transfer root.</summary>
    public string RootPath => _rootPath;

    /// <summary>
    /// Creates the transfer root when missing. The root itself may be a
    /// symlink or mounted volume the operator configured; only components
    /// <em>below</em> the root are checked for reparse points.
    /// </summary>
    public static string EnsureRootDirectory(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var full = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// A strict opaque token grammar for generated media references: non-empty
    /// lowercase hexadecimal, at most <see cref="MaxTokenLength"/> characters.
    /// Separators, dots, and rooted forms cannot occur by construction.
    /// </summary>
    public static bool IsValidOpaqueToken(string? token) =>
        token is { Length: > 0 and <= MaxTokenLength }
        && token.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public string GetUploadsRoot() => Path.Combine(_rootPath, UploadsDirectoryName);

    public string GetUploadSessionDirectory(Guid sessionId) =>
        Path.Combine(GetUploadsRoot(), FormatScopeId(sessionId, nameof(sessionId)));

    public string GetUploadArchivePartPath(Guid sessionId) =>
        Path.Combine(GetUploadSessionDirectory(sessionId), ArchivePartFileName);

    public string GetUploadArchivePath(Guid sessionId) =>
        Path.Combine(GetUploadSessionDirectory(sessionId), ArchiveFileName);

    public string GetUploadChunkTempPath(Guid sessionId, int chunkIndex)
    {
        if (chunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkIndex),
                chunkIndex,
                "A chunk index cannot be negative.");
        }

        return Path.Combine(
            GetUploadSessionDirectory(sessionId),
            $".chunk-{chunkIndex:D8}.{Guid.NewGuid():N}.tmp");
    }

    public string GetUploadArchiveStorageKey(Guid sessionId) =>
        BuildStorageKey(UploadsDirectoryName, FormatScopeId(sessionId, nameof(sessionId)), ArchiveFileName);

    public string GetStagingRoot() => Path.Combine(_rootPath, StagingDirectoryName);

    public string GetStagingDirectory(Guid stagingId) =>
        Path.Combine(GetStagingRoot(), FormatScopeId(stagingId, nameof(stagingId)));

    public string GetStagingMediaDirectory(Guid stagingId) =>
        Path.Combine(GetStagingDirectory(stagingId), MediaDirectoryName);

    public string GetStagingMediaPath(Guid stagingId, string mediaReference)
    {
        if (!IsValidOpaqueToken(mediaReference))
        {
            throw new TransferPathException(
                TransferPathException.InvalidPath,
                "A staged media reference must be a non-empty lowercase hexadecimal token.");
        }

        return Path.Combine(GetStagingMediaDirectory(stagingId), mediaReference);
    }

    public string GetExportsRoot() => Path.Combine(_rootPath, ExportsDirectoryName);

    public string GetExportDirectory(Guid jobId) =>
        Path.Combine(GetExportsRoot(), FormatScopeId(jobId, nameof(jobId)));

    public string GetExportTempPath(Guid jobId) =>
        Path.Combine(GetExportDirectory(jobId), ExportTempFileName);

    public string GetExportArtifactPath(Guid jobId) =>
        Path.Combine(GetExportDirectory(jobId), ExportFileName);

    public string GetExportArtifactStorageKey(Guid jobId) =>
        BuildStorageKey(ExportsDirectoryName, FormatScopeId(jobId, nameof(jobId)), ExportFileName);

    /// <summary>
    /// Resolves a persisted relative storage key to an absolute path strictly
    /// under the root, rejecting malformed keys and symlinked components.
    /// </summary>
    public string ResolveStorageKey(string? storageKey)
    {
        var segments = SplitStorageKey(storageKey);
        var candidate = Path.GetFullPath(
            Path.Combine([_rootPath, .. segments]));
        EnsureUnderRoot(candidate);
        EnsureNoReparsePointComponents(candidate);
        return candidate;
    }

    /// <summary>Non-throwing form of <see cref="ResolveStorageKey"/>.</summary>
    public bool TryResolveStorageKey(string? storageKey, out string absolutePath)
    {
        try
        {
            absolutePath = ResolveStorageKey(storageKey);
            return true;
        }
        catch (TransferPathException)
        {
            absolutePath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Converts an absolute path under the root into the relative,
    /// slash-separated storage key that is persisted in the database. Absolute
    /// paths never enter durable records.
    /// </summary>
    public string ToStorageKey(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            throw new TransferPathException(
                TransferPathException.InvalidPath,
                "An absolute transfer path is required to derive a storage key.");
        }

        var full = Path.GetFullPath(absolutePath);
        EnsureUnderRoot(full);
        EnsureNoReparsePointComponents(full);

        var relative = Path.GetRelativePath(_rootPath, full);
        var key = relative.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            key = key.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        // Defensive: a generated path must itself satisfy the persisted-key
        // grammar before it can be written to the database.
        SplitStorageKey(key);
        return key;
    }

    /// <summary>
    /// Creates a directory strictly under the root (and any missing parents),
    /// refusing to traverse an existing symlink/reparse point. Returns the
    /// canonical absolute directory path.
    /// </summary>
    public string EnsureDirectoryExists(string absoluteDirectory)
    {
        if (string.IsNullOrWhiteSpace(absoluteDirectory))
        {
            throw InvalidKey("An absolute transfer directory is required.");
        }

        var candidate = Path.GetFullPath(absoluteDirectory);
        if (string.Equals(candidate, _rootPath, _comparison))
        {
            return _rootPath;
        }

        var full = CanonicalizeWithinRoot(candidate);
        var relative = Path.GetRelativePath(_rootPath, full);
        var current = _rootPath;
        foreach (var segment in SplitRelativeSegments(relative))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                throw ReparsePointFailure(current);
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
                if (IsReparsePoint(current))
                {
                    throw ReparsePointFailure(current);
                }
            }
        }

        return full;
    }

    /// <summary>Creates the parent directory of a file path under the root.</summary>
    public string EnsureParentDirectoryExists(string absoluteFilePath)
    {
        var full = CanonicalizeWithinRoot(absoluteFilePath);
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
        {
            throw new TransferPathException(
                TransferPathException.InvalidPath,
                "A transfer file path must have a parent directory under the transfer root.");
        }

        return EnsureDirectoryExists(parent);
    }

    /// <summary>
    /// Returns the canonical path after refusing an existing symlink/reparse
    /// point at the final component. Callers open new files with
    /// <see cref="FileMode.CreateNew"/>; this guard covers overwrite paths.
    /// </summary>
    public string EnsureFileIsNotReparsePoint(string absoluteFilePath)
    {
        var full = CanonicalizeWithinRoot(absoluteFilePath);
        if (IsReparsePoint(full))
        {
            throw ReparsePointFailure(full);
        }

        return full;
    }

    private string[] SplitStorageKey(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw InvalidKey("A transfer storage key is required.");
        }

        if (storageKey.Length > MaxStorageKeyLength)
        {
            throw InvalidKey($"A transfer storage key cannot exceed {MaxStorageKeyLength} characters.");
        }

        if (storageKey.Contains('\\'))
        {
            throw InvalidKey("A transfer storage key must use '/' separators only.");
        }

        if (Path.IsPathRooted(storageKey))
        {
            throw InvalidKey("A transfer storage key must be relative, never rooted.");
        }

        var segments = storageKey.Split('/');
        foreach (var segment in segments)
        {
            ValidateSegment(segment);
        }

        return segments;
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Length is 0 or > MaxSegmentLength)
        {
            throw InvalidKey("A transfer storage key cannot contain empty or oversized segments.");
        }

        if (segment is "." or "..")
        {
            throw InvalidKey("A transfer storage key cannot contain '.' or '..' segments.");
        }

        if (segment[^1] is '.' or ' ')
        {
            throw InvalidKey("A transfer storage key segment cannot end with a dot or space.");
        }

        if (segment.Any(c => c < ' ' || Array.IndexOf(InvalidSegmentCharacters, c) >= 0))
        {
            throw InvalidKey("A transfer storage key contains invalid path characters.");
        }

        var baseName = segment.Split('.')[0];
        if (ReservedWindowsNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            throw InvalidKey("A transfer storage key cannot contain a reserved device name.");
        }
    }

    private string CanonicalizeWithinRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw InvalidKey("An absolute transfer path is required.");
        }

        var full = Path.GetFullPath(path);
        EnsureUnderRoot(full);
        return full;
    }

    private void EnsureUnderRoot(string fullPath)
    {
        if (string.Equals(fullPath, _rootPath, _comparison))
        {
            throw new TransferPathException(
                TransferPathException.OutsideRoot,
                "The transfer root itself is not a valid file or storage key target.");
        }

        var prefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, _comparison))
        {
            throw new TransferPathException(
                TransferPathException.OutsideRoot,
                "The resolved transfer path is outside the configured transfer root.");
        }
    }

    private void EnsureNoReparsePointComponents(string fullPath)
    {
        var relative = Path.GetRelativePath(_rootPath, fullPath);
        var current = _rootPath;
        foreach (var segment in SplitRelativeSegments(relative))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                throw ReparsePointFailure(current);
            }
        }
    }

    private static IEnumerable<string> SplitRelativeSegments(string relativePath) =>
        relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private TransferPathException ReparsePointFailure(string path) =>
        new(
            TransferPathException.ReparsePoint,
            $"The transfer path '{Path.GetFileName(path)}' is a symlink or reparse point; " +
            "transfer storage never follows links below its root.");

    private static TransferPathException InvalidKey(string message) =>
        new(TransferPathException.InvalidPath, message);

    private static string FormatScopeId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "Transfer scope identifiers must not be empty.",
                parameterName);
        }

        return id.ToString("N");
    }

    private static string BuildStorageKey(params string[] segments) =>
        string.Join('/', segments);

    private static StringComparison PathComparison(string path) =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

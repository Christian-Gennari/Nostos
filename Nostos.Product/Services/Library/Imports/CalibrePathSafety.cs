namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Small filesystem checks shared by the folder and uploaded Calibre sources.
/// They are deliberately here rather than in a host type so both sources
/// apply the same rule to a library that came from a mount and one that came
/// out of a zip.
/// </summary>
internal static class CalibrePathSafety
{
    /// <summary>Longest relative reference a Calibre row may name.</summary>
    public const int MaxRelativePathLength = 1024;

    /// <summary>
    /// Splits a relative path into segments, or returns false for anything that
    /// is not strictly below the root it will be combined with. A backslash is
    /// legal on some systems and a separator on others; refusing it keeps one
    /// meaning.
    /// </summary>
    public static bool TryGetSegments(string? relativePath, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > MaxRelativePathLength)
            return false;

        foreach (var c in relativePath)
        {
            if (c == '\\' || char.IsControl(c))
                return false;
        }

        var parts = relativePath.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or "..")
                return false;
            if (part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
                return false;
        }

        segments = parts;
        return true;
    }

    /// <summary>True for a symbolic link or a Windows reparse point.</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try
        {
            if (info.LinkTarget is not null)
                return true;
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>True for an ordinary file: exists, is not a directory and is not a link.</summary>
    public static bool IsRegularFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && !IsLink(info);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

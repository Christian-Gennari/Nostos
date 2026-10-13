using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// The only shape a reference into an external root may have: forward-slash
/// segments, none of them empty, <c>.</c> or <c>..</c>.
/// </summary>
public static class ExternalMediaRelativePath
{
    /// <summary>
    /// Splits a path into its segments, or returns false for anything that
    /// is not strictly below the root it will be combined with.
    /// </summary>
    public static bool TryGetSegments(string? relativePath, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(relativePath)
            || relativePath.Length > ExternalMediaLinkModel.MaxRelativePathLength)
            return false;

        // A backslash is a legal file name character on the systems this
        // runs on and a separator on others; refusing it keeps one meaning.
        foreach (var c in relativePath)
        {
            if (c == '\\' || char.IsControl(c))
                return false;
        }

        var parts = relativePath.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or "..")
                return false;
        }

        segments = parts;
        return true;
    }

    public static bool IsValid(string? relativePath) => TryGetSegments(relativePath, out _);
}

using System.Text;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// The protocol's file-path rules, ported from <c>carrel-protocol</c>'s
/// <c>paths.rs</c>: a path is relative, <c>/</c>-separated, has no empty,
/// <c>.</c>, <c>..</c> or hidden component, and ends in <c>.md</c>. The path
/// is what a device writes into, so this validation is a security boundary,
/// not a style check.
/// </summary>
public static class CarrelPaths
{
    /// <summary>Longest allowed relative path, in UTF-8 bytes (the crate's limit).</summary>
    public const int MaxPathBytes = 1024;

    /// <summary>Largest file body accepted by the protocol, in UTF-8 bytes.</summary>
    public const int MaxFileBytes = 16 * 1024 * 1024;

    /// <summary>The path's rules, in the crate's own order, or null when it is valid.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "path is empty";
        if (Encoding.UTF8.GetByteCount(path) > MaxPathBytes)
            return "path is too long";
        if (path[0] == '/')
            return "path must be relative";
        if (path.Contains('\\'))
            return "path must use '/' separators";
        if (path.Contains('\0'))
            return "path contains a NUL byte";
        if (!path.EndsWith(".md", StringComparison.Ordinal))
            return "path must end in '.md'";
        foreach (var component in path.Split('/'))
        {
            if (component.Length == 0)
                return "path contains an empty component";
            if (component == ".")
                return "path contains a '.' component";
            if (component == "..")
                return "path contains a '..' component";
            if (component[0] == '.')
                return "path contains a hidden component";
        }

        return null;
    }

    public static bool IsValid(string? path) => Validate(path) is null;

    /// <summary>
    /// The folder names and the document name of a valid path:
    /// <c>notes/2026/one.md</c> → (<c>["notes", "2026"]</c>, <c>"one"</c>).
    /// </summary>
    public static (IReadOnlyList<string> Folders, string Name) Split(string path)
    {
        var components = path.Split('/');
        var name = components[^1];
        name = name[..^".md".Length];
        return (components[..^1], name);
    }

    /// <summary>
    /// One path component from a Studio name: <c>/</c> cannot appear inside a
    /// path component, a leading dot would make the component hidden, and an
    /// empty name would make the path invalid.
    /// </summary>
    public static string Component(string? name)
    {
        var value = (name ?? string.Empty).Replace('/', '-').Replace('\\', '-').Replace("\0", string.Empty).Trim();
        if (value.Length == 0)
            return "Untitled";
        if (value[0] == '.')
            value = "_" + value[1..];
        return value;
    }

    /// <summary>
    /// The protocol path of a document: its folder chain, then its own name and
    /// <c>.md</c>. The result always validates.
    /// </summary>
    public static string ForDocument(IEnumerable<string> folders, string? name)
    {
        var parts = folders.Select(Component).Append(Component(name) + ".md").ToList();
        var path = string.Join('/', parts);
        return Fit(path);
    }

    /// <summary>
    /// A sibling path that is free in <paramref name="taken"/>: the path itself
    /// first, then <c>name (2).md</c>, <c>name (3).md</c>, so two Studio drafts
    /// with the same name never present one device path.
    /// </summary>
    public static string Unique(string path, ISet<string> taken)
    {
        if (taken.Add(path))
            return path;
        var stem = path[..^".md".Length];
        for (var n = 2; ; n++)
        {
            var candidate = Fit($"{stem} ({n}).md");
            if (taken.Add(candidate))
                return candidate;
        }
    }

    /// <summary>Trim a path to the protocol byte limit, keeping its <c>.md</c> suffix.</summary>
    private static string Fit(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) <= MaxPathBytes)
            return path;

        var suffix = ".md";
        var body = path[..^suffix.Length];
        var builder = new StringBuilder();
        var used = suffix.Length;
        foreach (var rune in body.EnumerateRunes())
        {
            used += Encoding.UTF8.GetByteCount(rune.ToString());
            if (used > MaxPathBytes)
                break;
            builder.Append(rune.ToString());
        }

        return builder.Append(suffix).ToString();
    }
}

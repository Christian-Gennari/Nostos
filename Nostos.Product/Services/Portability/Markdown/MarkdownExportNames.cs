using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nostos.Backend.Services.Portability.Markdown;

/// <summary>
/// File names for the Markdown export (issue #1221) that every common system
/// accepts, the rules of the Studio's <c>exportFilename</c> (issue #1164) plus
/// what breaks a wikilink. Letters in any script and emoji stay as written.
///
/// <list type="bullet">
/// <item>Windows refuses <c>&lt; &gt; : " / \ | ? *</c> and control characters,
/// a name ending in a dot or a space, and the device names (CON, PRN, AUX,
/// NUL, COM1–9, LPT1–9) with or without an extension.</item>
/// <item>macOS and Linux refuse <c>/</c> and NUL; a leading dot hides the file.</item>
/// <item>Obsidian reads <c># ^ [ ] |</c> in a link as a heading, a block, a link
/// end or an alias, so a file named with them could never be linked to.</item>
/// <item>Names are composed (NFC), so macOS and Linux spell them the same, and
/// cut at a whole character to 200 UTF-8 bytes, under every 255-unit limit.</item>
/// <item>Invisible bidirectional controls are removed: in a file name they can
/// make "report‮fdp.exe" read as a PDF.</item>
/// </list>
/// </summary>
public static partial class MarkdownExportNames
{
    public const int MaxNameBytes = 200;

    [GeneratedRegex(@"^(con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedRegex();

    [GeneratedRegex(@"[‪-‮⁦-⁩‎‏]")]
    private static partial Regex BidiRegex();

    [GeneratedRegex(@"[\u0000-\u001F\u007F-\u009F]")]
    private static partial Regex ControlRegex();

    [GeneratedRegex(@"[<>:""/\\|?*#^\[\]]")]
    private static partial Regex RefusedRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    /// <summary>
    /// <paramref name="name"/> as one path segment every system accepts, without
    /// an extension; <paramref name="fallback"/> when nothing usable is left.
    /// </summary>
    public static string Segment(string? name, string fallback = "Untitled", int maxBytes = MaxNameBytes)
    {
        var text = (name ?? string.Empty).Normalize(NormalizationForm.FormC);
        text = BidiRegex().Replace(text, string.Empty);
        text = ControlRegex().Replace(text, " ");
        text = RefusedRegex().Replace(text, " ");
        text = SpaceRegex().Replace(text, " ").Trim().TrimStart('.', ' ').TrimEnd('.', ' ');
        if (text.Length == 0) text = fallback;
        if (ReservedRegex().IsMatch(text)) text += "_";
        text = CutToBytes(text, maxBytes).TrimEnd('.', ' ');
        return text.Length == 0 ? fallback : text;
    }

    /// <summary>Cuts at a whole character (a text element), never inside one.</summary>
    internal static string CutToBytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var builder = new StringBuilder();
        var bytes = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (bytes + size > maxBytes) break;
            builder.Append(element);
            bytes += size;
        }
        return builder.ToString();
    }
}

/// <summary>
/// Hands out names that are unique within one folder the way the strictest
/// file systems compare them: ignoring case (Windows, macOS) after composing.
/// The first claim of a name gets it as is; later ones get a qualifier, then
/// a number. Callers claim in a fixed order (oldest first), so the same
/// library exports to the same names, and a new book never renames an old one.
/// </summary>
public sealed class MarkdownNameAllocator
{
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    private static string Key(string name) =>
        name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public bool IsTaken(string name) => _taken.Contains(Key(name));

    /// <summary>Marks <paramref name="name"/> as used without allocating it.</summary>
    public void Reserve(string name) => _taken.Add(Key(name));

    /// <summary>
    /// A free name from <paramref name="wanted"/>: itself, then
    /// "wanted (qualifier)", then "wanted (2)", "wanted (3)"… Every candidate
    /// passes through <see cref="MarkdownExportNames.Segment"/> with room for
    /// <paramref name="extension"/>.
    /// </summary>
    public string Claim(string? wanted, string? qualifier = null, string extension = ".md", string fallback = "Untitled")
    {
        var room = MarkdownExportNames.MaxNameBytes - Encoding.UTF8.GetByteCount(extension);
        var baseName = MarkdownExportNames.Segment(wanted, fallback, room);
        if (_taken.Add(Key(baseName))) return baseName;

        if (!string.IsNullOrWhiteSpace(qualifier))
        {
            var qualified = MarkdownExportNames.Segment($"{baseName} ({qualifier.Trim()})", fallback, room);
            if (_taken.Add(Key(qualified))) return qualified;
        }

        for (var n = 2; ; n++)
        {
            var suffix = $" ({n})";
            var numbered = MarkdownExportNames.CutToBytes(
                baseName, room - Encoding.UTF8.GetByteCount(suffix)).TrimEnd('.', ' ') + suffix;
            if (_taken.Add(Key(numbered))) return numbered;
        }
    }
}

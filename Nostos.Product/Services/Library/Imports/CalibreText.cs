using System.Net;
using System.Text.RegularExpressions;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Calibre keeps comments and some custom columns as HTML. Nostos stores a
/// book's description as text, so the import turns Calibre's markup into the
/// words it wraps rather than showing tags in the book page.
/// </summary>
public static partial class CalibreText
{
    public static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        // Line breaks and block ends are the parts of the markup that carry
        // meaning for a description; everything else is presentation.
        var withBreaks = BlockBreak().Replace(html, "\n");
        withBreaks = TagBreak().Replace(withBreaks, "\n");

        // Strip the remaining tags without an HTML parser's document repair:
        // Calibre's comments are fragments, and a tolerant regex keeps unknown
        // markup from swallowing the text after it.
        var text = RemainingTag().Replace(withBreaks, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = HorizontalSpace().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : null;
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6])\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<\s*/?\s*(p|div|li|ul|ol|h[1-6]|blockquote|tr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TagBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex RemainingTag();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}

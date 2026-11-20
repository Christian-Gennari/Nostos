using System.Text.RegularExpressions;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Chat footnote markers inside a Studio draft the assistant writes (issue #1741).
///
/// In a reply, <c>[^n]</c> points at the turn's evidence row. A draft has no such
/// row: the Studio's own citation is a <c>nostos:cite/</c> link, and a bare
/// <c>[^n]</c> with no definition is a superscript tied to nothing. So the
/// server removes every marker that has no matching <c>[^n]: …</c> definition
/// in the same document; markers the document does define stay as written.
/// Code (fenced or inline) is left exactly as written.
/// </summary>
internal static partial class AssistantDraftMarkers
{
    [GeneratedRegex(@"(?m)^[ \t]{0,3}(```|~~~)[\s\S]*?(^[ \t]{0,3}\1|\z)")]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"(?m)^[ \t]{0,3}\[\^([^\]\s]+)\]:")]
    private static partial Regex Definition();

    [GeneratedRegex(@"[ \t]*\[\^([^\]\s]+)\](?!:)")]
    private static partial Regex Marker();

    /// <summary>
    /// <paramref name="content"/> without the footnote markers that point at
    /// nothing. Text with no marker comes back unchanged.
    /// </summary>
    public static string StripUnmatched(string content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("[^", StringComparison.Ordinal))
            return content;

        var code = FencedCode().Matches(content).Cast<Match>()
            .Concat(InlineCode().Matches(content).Cast<Match>())
            .Select(match => (Start: match.Index, End: match.Index + match.Length))
            .ToList();
        bool InCode(int index) => code.Any(span => index >= span.Start && index < span.End);

        var defined = Definition().Matches(content).Cast<Match>()
            .Where(match => !InCode(match.Index))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        return Marker().Replace(content, match =>
            InCode(match.Index) || defined.Contains(match.Groups[1].Value)
                ? match.Value
                : string.Empty);
    }
}

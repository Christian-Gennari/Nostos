using System.Text;
using System.Text.RegularExpressions;

namespace Nostos.Backend.Services;

/// <summary>
/// The text rules for a note's <c>[[Topic]]</c> references.
///
/// A note's body is the canonical record of its topics: every save re-derives
/// the links from it (<see cref="NoteProcessorService"/>). So linking,
/// unlinking, renaming, merging and deleting a topic must all change the body
/// the same way, or a link removed in one place comes back on the next edit.
/// These are those rules, in one place.
///
/// <list type="bullet">
/// <item>Linking appends the reference as its own paragraph.</item>
/// <item>Unlinking removes a reference that stands on a line of references
/// alone (what linking writes), and turns one inside prose into plain words,
/// so no sentence loses a word.</item>
/// <item>Renaming rewrites the reference in place.</item>
/// </list>
/// Names compare trimmed and case-insensitively, as the processor matches them.
/// </summary>
public static partial class TopicReferences
{
    [GeneratedRegex(@"\[\[(.*?)\]\]")]
    private static partial Regex ReferenceRegex();

    /// <summary>The topic names <paramref name="content"/> declares, trimmed and distinct.</summary>
    public static IReadOnlyList<string> Names(string? content) =>
        ReferenceRegex().Matches(content ?? string.Empty)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool Declares(string? content, string name) =>
        Names(content).Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="content"/> with a reference to <paramref name="name"/>
    /// as its last paragraph, or unchanged when it already declares the topic.
    /// </summary>
    public static string Append(string? content, string name)
    {
        var body = content ?? string.Empty;
        var topic = name.Trim();
        if (topic.Length == 0 || Declares(body, topic)) return body;

        body = body.TrimEnd();
        return body.Length == 0 ? $"[[{topic}]]" : $"{body}\n\n[[{topic}]]";
    }

    /// <summary>
    /// <paramref name="content"/> without its references to <paramref name="name"/>.
    /// A line holding only references loses the reference (and the line, when
    /// nothing is left on it); a reference in prose becomes its plain words.
    /// </summary>
    public static string Remove(string? content, string name) =>
        Rewrite(content ?? string.Empty, name, replacement: null);

    /// <summary>
    /// <paramref name="content"/> with its references to <paramref name="oldName"/>
    /// pointing at <paramref name="newName"/>. When the note already declares the
    /// new name, a reference standing alone is dropped rather than repeated.
    /// </summary>
    public static string Rename(string? content, string oldName, string newName)
    {
        var body = content ?? string.Empty;
        var target = newName.Trim();
        if (string.Equals(oldName.Trim(), target, StringComparison.Ordinal)) return body;
        return Rewrite(body, oldName, target);
    }

    private static string Rewrite(string content, string name, string? replacement)
    {
        var topic = name.Trim();
        if (topic.Length == 0 || !Declares(content, topic)) return content;

        // A merge into a topic the note already declares must not leave the
        // same reference twice on a line of references.
        var replacementDeclared = replacement is not null
            && !string.Equals(topic, replacement, StringComparison.OrdinalIgnoreCase)
            && Declares(content, replacement);

        var lines = content.Split('\n').Select(line => (string?)line).ToList();
        var removedAt = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i]!;
            if (!LineDeclares(line, topic)) continue;

            if (IsReferenceLine(line))
            {
                var rewritten = replacement is null || replacementDeclared
                    ? RemoveFromReferenceLine(line, topic)
                    : ReplaceMatching(line, topic, _ => $"[[{replacement}]]");
                if (string.IsNullOrWhiteSpace(rewritten))
                {
                    removedAt.Add(i);
                    lines[i] = null;
                }
                else
                {
                    lines[i] = rewritten;
                }
            }
            else
            {
                lines[i] = replacement is null
                    ? ReplaceMatching(line, topic, inner => inner.Trim())
                    : ReplaceMatching(line, topic, _ => $"[[{replacement}]]");
            }
        }

        if (removedAt.Count == 0) return string.Join('\n', lines);
        return JoinWithoutRemovedLines(lines);
    }

    private static bool LineDeclares(string line, string topic) =>
        ReferenceRegex().Matches(line)
            .Any(match => string.Equals(match.Groups[1].Value.Trim(), topic, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A line made of references and separators only: what linking writes, or a
    /// hand-typed row of tags. Anything with words outside the brackets is prose.
    /// </summary>
    private static bool IsReferenceLine(string line)
    {
        var rest = ReferenceRegex().Replace(line, string.Empty);
        return rest.All(c => char.IsWhiteSpace(c) || c is ',' or ';' or '·' or '|');
    }

    private static string ReplaceMatching(string line, string topic, Func<string, string> replace) =>
        ReferenceRegex().Replace(line, match =>
            string.Equals(match.Groups[1].Value.Trim(), topic, StringComparison.OrdinalIgnoreCase)
                ? replace(match.Groups[1].Value)
                : match.Value);

    /// <summary>
    /// Drops the matching references from a line of references, with the
    /// separator that followed each, keeping the others and their spacing.
    /// </summary>
    private static string RemoveFromReferenceLine(string line, string topic)
    {
        var tokens = new List<(string Reference, string Separator)>();
        var lead = string.Empty;
        var matches = ReferenceRegex().Matches(line);
        for (var m = 0; m < matches.Count; m++)
        {
            var match = matches[m];
            if (m == 0) lead = line[..match.Index];
            var end = match.Index + match.Length;
            var next = m + 1 < matches.Count ? matches[m + 1].Index : line.Length;
            tokens.Add((match.Value, line[end..next]));
        }

        var kept = tokens
            .Where(token => !string.Equals(
                ReferenceRegex().Match(token.Reference).Groups[1].Value.Trim(), topic,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (kept.Count == 0) return string.Empty;

        var builder = new StringBuilder(lead);
        for (var k = 0; k < kept.Count; k++)
        {
            builder.Append(kept[k].Reference);
            // The last kept reference ends the line the way the line ended.
            builder.Append(k == kept.Count - 1 ? TrailingWhitespace(tokens[^1].Separator) : kept[k].Separator);
        }
        return builder.ToString();
    }

    private static string TrailingWhitespace(string separator)
    {
        var start = separator.Length;
        while (start > 0 && char.IsWhiteSpace(separator[start - 1])) start--;
        return separator[start..];
    }

    /// <summary>
    /// Rejoins the lines that remain. A removed line takes one neighbouring blank
    /// line with it, so the paragraph break linking added goes too and appending
    /// then removing a reference gives back the original text.
    /// </summary>
    private static string JoinWithoutRemovedLines(List<string?> lines)
    {
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] is null)
            {
                // Collapse the blank line before the removed one when the line
                // after it is blank too (or the removed line was the last).
                var nextIsBlankOrEnd = NextKept(lines, i) is not { } next || string.IsNullOrWhiteSpace(next);
                if (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]) && nextIsBlankOrEnd)
                    result.RemoveAt(result.Count - 1);
                continue;
            }
            var kept = lines[i]!;
            // A blank line that opened the note before a removed first line goes too.
            if (result.Count == 0 && string.IsNullOrWhiteSpace(kept) && i > 0 && lines[i - 1] is null)
                continue;
            result.Add(kept);
        }

        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]) && lines[^1] is null)
            result.RemoveAt(result.Count - 1);
        return string.Join('\n', result);
    }

    private static string? NextKept(List<string?> lines, int index)
    {
        for (var j = index + 1; j < lines.Count; j++)
            if (lines[j] is not null) return lines[j];
        return null;
    }
}

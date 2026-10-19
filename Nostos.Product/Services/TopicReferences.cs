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

    public const int MaxNameLength = 200;

    /// <summary>Why <see cref="IsValidName"/> refused a name, in the reader's words.</summary>
    public static readonly string InvalidNameMessage =
        $"A topic name is 1 to {MaxNameLength} characters on one line, without [[, ]] or |, and does not start with [ or end with ].";

    /// <summary>
    /// Whether <paramref name="name"/> can stand inside <c>[[ ]]</c> and be read
    /// back as the same name: one line, no brackets that would close or open a
    /// reference early, no <c>|</c> (which the reader shows as a link alias).
    /// </summary>
    public static bool IsValidName(string? name)
    {
        var topic = name?.Trim() ?? string.Empty;
        return topic.Length is > 0 and <= MaxNameLength
            && !topic.Contains("[[") && !topic.Contains("]]")
            && !topic.EndsWith(']') && !topic.StartsWith('[')
            && !topic.Contains('|')
            && !topic.Contains('\n') && !topic.Contains('\r');
    }

    /// <summary>The topic names <paramref name="content"/> declares, trimmed and distinct.</summary>
    public static IReadOnlyList<string> Names(string? content) =>
        ReferenceRegex().Matches(content ?? string.Empty)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool Declares(string? content, string name)
    {
        var topic = name.Trim();
        return ReferenceRegex().Matches(content ?? string.Empty).Any(match => Is(match, topic));
    }

    /// <summary>
    /// <paramref name="content"/> with a reference to <paramref name="name"/>
    /// as its last paragraph, or unchanged when it already declares the topic.
    /// </summary>
    public static string Append(string? content, string name) => AppendAll(content, [name]);

    /// <summary>
    /// <see cref="Append"/> for each of <paramref name="names"/> the body does
    /// not declare yet, parsing the body once.
    /// </summary>
    public static string AppendAll(string? content, IEnumerable<string> names)
    {
        var body = content ?? string.Empty;
        var declared = Names(body).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var topic = name.Trim();
            if (topic.Length == 0 || !declared.Add(topic)) continue;
            body = body.TrimEnd();
            body = body.Length == 0 ? $"[[{topic}]]" : $"{body}\n\n[[{topic}]]";
        }
        return body;
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
        var dropOnReferenceLine = replacement is null
            || (!string.Equals(topic, replacement, StringComparison.OrdinalIgnoreCase)
                && Declares(content, replacement));
        Func<string, string> inProse = replacement is null
            ? inner => inner.Trim()
            : _ => $"[[{replacement}]]";

        var lines = content.Split('\n').Select(line => (string?)line).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i]!;
            if (!ReferenceRegex().Matches(line).Any(match => Is(match, topic))) continue;

            if (!IsReferenceLine(line))
            {
                lines[i] = ReplaceMatching(line, topic, inProse);
                continue;
            }

            var rewritten = dropOnReferenceLine
                ? RemoveFromReferenceLine(line, topic)
                : ReplaceMatching(line, topic, inProse);
            lines[i] = string.IsNullOrWhiteSpace(rewritten) ? null : rewritten;
        }

        return JoinWithoutRemovedLines(lines);
    }

    /// <summary>Whether a reference names <paramref name="topic"/> (trimmed, any case).</summary>
    private static bool Is(Match reference, string topic) =>
        string.Equals(reference.Groups[1].Value.Trim(), topic, StringComparison.OrdinalIgnoreCase);

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
        ReferenceRegex().Replace(line, match => Is(match, topic) ? replace(match.Groups[1].Value) : match.Value);

    /// <summary>
    /// Drops the matching references from a line of references, with the
    /// separator that followed each, keeping the others and their spacing.
    /// </summary>
    private static string RemoveFromReferenceLine(string line, string topic)
    {
        var matches = ReferenceRegex().Matches(line);
        var tokens = matches.Select((match, m) =>
        {
            var end = match.Index + match.Length;
            var next = m + 1 < matches.Count ? matches[m + 1].Index : line.Length;
            return (match.Value, Separator: line[end..next], Drop: Is(match, topic));
        }).ToList();

        var kept = tokens.Where(token => !token.Drop).ToList();
        if (kept.Count == 0) return string.Empty;

        var builder = new StringBuilder(line[..matches[0].Index]);
        var lastSeparator = tokens[^1].Separator;
        for (var k = 0; k < kept.Count; k++)
        {
            builder.Append(kept[k].Value);
            // The last kept reference ends the line the way the line ended.
            builder.Append(k == kept.Count - 1
                ? lastSeparator[lastSeparator.TrimEnd().Length..]
                : kept[k].Separator);
        }
        return builder.ToString();
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
            if (lines[i] is not { } kept)
            {
                // Collapse the blank line before the removed one when the line
                // after it is blank too (or the removed line was the last).
                var nextIsBlankOrEnd = NextKept(lines, i) is not { } next || string.IsNullOrWhiteSpace(next);
                if (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]) && nextIsBlankOrEnd)
                    result.RemoveAt(result.Count - 1);
                continue;
            }
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

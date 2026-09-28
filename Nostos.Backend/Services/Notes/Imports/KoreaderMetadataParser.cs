using System.Text;
using System.Text.RegularExpressions;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads the small, data-only subset of KOReader sidecar metadata that Nostos
/// needs for annotation import. It deliberately does not execute Lua.
/// </summary>
internal static partial class KoreaderMetadataParser
{
    public static KoreaderMetadataDocument Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new FormatException("KOReader metadata file is empty.");

        var title = default(string);
        var author = default(string);
        var isbn = default(string);
        var annotations = new List<KoreaderAnnotation>();
        var section = Section.None;
        Dictionary<string, string?>? current = null;

        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
                continue;

            if (section == Section.Annotations && AnnotationStart().IsMatch(trimmed))
            {
                current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                section = Section.Annotation;
                continue;
            }

            if (CloseTable().IsMatch(trimmed))
            {
                if (section == Section.Annotation && current is not null)
                {
                    annotations.Add(ToAnnotation(current));
                    current = null;
                    section = Section.Annotations;
                }
                else if (section is Section.DocProps or Section.Annotations)
                {
                    section = Section.None;
                }
                continue;
            }

            var match = Assignment().Match(line);
            if (!match.Success)
                continue;

            var key = Unescape(match.Groups["key"].Value);
            var rawValue = match.Groups["value"].Value.Trim();
            if (rawValue == "{")
            {
                section = key switch
                {
                    "doc_props" => Section.DocProps,
                    "annotations" => Section.Annotations,
                    _ => section,
                };
                continue;
            }

            var value = ParseScalar(rawValue);
            if (section == Section.DocProps)
            {
                switch (key)
                {
                    case "title":
                        title = value;
                        break;
                    case "authors":
                    case "author":
                        author ??= value;
                        break;
                    case "isbn":
                    case "isbn13":
                    case "isbn10":
                        isbn ??= value;
                        break;
                }
            }
            else if (section == Section.Annotation && current is not null)
            {
                current[key] = value;
            }
        }

        if (section == Section.Annotation && current is not null)
            annotations.Add(ToAnnotation(current));

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(isbn))
            throw new FormatException("KOReader metadata must contain doc_props.title or an ISBN.");

        return new KoreaderMetadataDocument(
            Clean(title),
            Clean(author),
            Clean(isbn),
            annotations);
    }

    private static KoreaderAnnotation ToAnnotation(IReadOnlyDictionary<string, string?> values) =>
        new(
            Get(values, "text"),
            Get(values, "note"),
            Get(values, "chapter"),
            Get(values, "datetime"),
            Get(values, "page"),
            Get(values, "pageno"),
            Get(values, "pos0"),
            Get(values, "pos1"));

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? Clean(value) : null;

    private static string? ParseScalar(string raw)
    {
        raw = raw.TrimEnd(',').Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return Unescape(raw[1..^1]);

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            return Unescape(raw[1..^1]);

        if (string.Equals(raw, "nil", StringComparison.Ordinal))
            return null;

        return raw;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '\\' || i + 1 >= value.Length)
            {
                result.Append(ch);
                continue;
            }

            var next = value[++i];
            result.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                _ => next,
            });
        }

        return result.ToString();
    }

    [GeneratedRegex("^\\s*\\[\\\"(?<key>(?:\\\\.|[^\\\"])*)\\\"\\]\\s*=\\s*(?<value>.+?)\\s*,?\\s*$")]
    private static partial Regex Assignment();

    [GeneratedRegex("^\\s*\\[\\d+\\]\\s*=\\s*\\{\\s*,?\\s*$")]
    private static partial Regex AnnotationStart();

    [GeneratedRegex("^\\s*}\\s*,?\\s*$")]
    private static partial Regex CloseTable();

    private enum Section
    {
        None,
        DocProps,
        Annotations,
        Annotation,
    }
}

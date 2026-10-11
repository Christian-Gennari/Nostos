using System.Globalization;
using System.Text;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads the small, data-only subset of KOReader sidecar metadata that Nostos
/// needs for annotation import. It deliberately does not execute Lua.
/// </summary>
internal static class KoreaderMetadataParser
{
    public static KoreaderMetadataDocument Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new FormatException("KOReader metadata file is empty.");

        if (new LuaDataReader(source).ReadDocument() is not LuaTable root)
            throw new FormatException("KOReader metadata must be a Lua table.");

        var title = default(string);
        var author = default(string);
        var isbn = default(string);

        if (root.Get("doc_props") is LuaTable props)
        {
            foreach (var (key, value) in props.Entries)
            {
                switch (key)
                {
                    case "title":
                        title = value as string;
                        break;
                    case "authors":
                    case "author":
                        author ??= value as string;
                        break;
                    case "isbn":
                    case "isbn13":
                    case "isbn10":
                        isbn ??= value as string;
                        break;
                }
            }
        }

        var annotations = new List<KoreaderAnnotation>();
        if (root.Get("annotations") is LuaTable entries)
        {
            foreach (var (_, value) in entries.Entries)
            {
                if (value is LuaTable annotation)
                    annotations.Add(ToAnnotation(annotation));
            }
        }

        // A PDF without embedded metadata has an empty doc_props. KOReader then
        // shows the file name as the title, and so does this: it is what the
        // owner sees on the device, and the import asks before filing under it.
        if (string.IsNullOrWhiteSpace(title))
        {
            var stats = root.Get("stats") as LuaTable;
            title = Known(stats?.Get("title") as string)
                ?? FileTitle(root.Get("doc_path") as string);
            author ??= Known(stats?.Get("authors") as string);
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(isbn))
            throw new FormatException("KOReader metadata must name its book: a title, an ISBN or a file path.");

        return new KoreaderMetadataDocument(
            Clean(title),
            Clean(author),
            Clean(isbn),
            annotations,
            Clean(root.Get("partial_md5_checksum") as string));
    }

    // A reflowable book stores pos0/pos1 as xpointer strings. A PDF stores
    // them as tables of page coordinates, which `Scalar` drops: the page
    // number is the only part of a PDF position Nostos can use.
    private static KoreaderAnnotation ToAnnotation(LuaTable values) =>
        new(
            Scalar(values, "text"),
            Scalar(values, "note"),
            Scalar(values, "chapter"),
            Scalar(values, "datetime"),
            Scalar(values, "page"),
            Scalar(values, "pageno"),
            Scalar(values, "pos0"),
            Scalar(values, "pos1"));

    private static string? Scalar(LuaTable values, string key) =>
        Clean(values.Get(key) as string);

    // KOReader's statistics write "N/A" for a property the document lacks.
    private static string? Known(string? value) =>
        Clean(value) is { } known && known != "N/A" ? known : null;

    private static string? FileTitle(string? path)
    {
        var name = Clean(path)?.Split('/', '\\')[^1];
        if (string.IsNullOrEmpty(name))
            return null;

        var extension = name.LastIndexOf('.');
        return Clean(extension > 0 ? name[..extension] : name);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A Lua table in file order; a value is a string, a table or null.</summary>
    private sealed class LuaTable
    {
        public List<(string Key, object? Value)> Entries { get; } = [];

        public object? Get(string key)
        {
            foreach (var (candidate, value) in Entries)
            {
                if (string.Equals(candidate, key, StringComparison.Ordinal))
                    return value;
            }

            return null;
        }
    }

    /// <summary>
    /// Reads what KOReader's <c>dump.lua</c> writes: nested tables, <c>%q</c>
    /// strings (a line break inside one is a backslash followed by a real
    /// line break), numbers, booleans and nil. Numbers and booleans are kept
    /// as their source text.
    /// </summary>
    private sealed class LuaDataReader(string source)
    {
        // Sidecars nest three or four levels; the cap keeps an upload of
        // nothing but braces from exhausting the stack.
        private const int MaxDepth = 32;
        private const string Return = "return";
        private int _position;

        public object? ReadDocument()
        {
            SkipTrivia();
            var afterReturn = _position + Return.Length;
            if (Matches(Return) && (afterReturn >= source.Length || !IsNameChar(source[afterReturn])))
                _position = afterReturn;

            return ReadValue(0);
        }

        private object? ReadValue(int depth)
        {
            SkipTrivia();
            if (_position >= source.Length)
                throw Malformed();

            var ch = source[_position];
            if (ch == '{')
                return ReadTable(depth + 1);
            if (ch is '"' or '\'')
                return ReadString();

            var start = _position;
            while (_position < source.Length && !IsValueEnd(source[_position]))
                _position++;
            if (_position == start)
                throw Malformed();

            var token = source[start.._position];
            return token == "nil" ? null : token;
        }

        private LuaTable ReadTable(int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException("KOReader metadata is nested too deeply.");

            _position++;
            var table = new LuaTable();
            var positional = 0;
            while (true)
            {
                SkipTrivia();
                if (_position >= source.Length)
                    throw Malformed();

                var ch = source[_position];
                if (ch == '}')
                {
                    _position++;
                    return table;
                }

                if (ch is ',' or ';')
                {
                    _position++;
                    continue;
                }

                string key;
                if (ch == '[')
                {
                    _position++;
                    key = ReadValue(depth) as string ?? throw Malformed();
                    Expect(']');
                    Expect('=');
                }
                else if (TryReadNameKey(out var name))
                {
                    key = name;
                }
                else
                {
                    key = (++positional).ToString(CultureInfo.InvariantCulture);
                }

                table.Entries.Add((key, ReadValue(depth)));
            }
        }

        private bool TryReadNameKey(out string name)
        {
            name = string.Empty;
            var cursor = _position;
            while (cursor < source.Length && IsNameChar(source[cursor]))
                cursor++;
            if (cursor == _position || char.IsAsciiDigit(source[_position]))
                return false;

            var end = cursor;
            while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
                cursor++;
            if (cursor >= source.Length || source[cursor] != '='
                || (cursor + 1 < source.Length && source[cursor + 1] == '='))
                return false;

            name = source[_position..end];
            _position = cursor + 1;
            return true;
        }

        private string ReadString()
        {
            var quote = source[_position++];
            var result = new StringBuilder();
            while (_position < source.Length)
            {
                var ch = source[_position++];
                if (ch == quote)
                    return result.ToString();

                if (ch != '\\' || _position >= source.Length)
                {
                    result.Append(ch);
                    continue;
                }

                var next = source[_position++];
                if (char.IsAsciiDigit(next))
                {
                    var code = next - '0';
                    for (var i = 0; i < 2 && _position < source.Length && char.IsAsciiDigit(source[_position]); i++)
                        code = (code * 10) + (source[_position++] - '0');
                    result.Append((char)code);
                    continue;
                }

                // A file that crossed a Windows machine carries CRLF breaks.
                if (next == '\r' && _position < source.Length && source[_position] == '\n')
                    _position++;

                result.Append(next switch
                {
                    'n' or '\r' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next,
                });
            }

            throw Malformed();
        }

        private void Expect(char expected)
        {
            SkipTrivia();
            if (_position >= source.Length || source[_position] != expected)
                throw Malformed();
            _position++;
        }

        private void SkipTrivia()
        {
            while (_position < source.Length)
            {
                if (char.IsWhiteSpace(source[_position]))
                {
                    _position++;
                }
                else if (Matches("--[["))
                {
                    var end = source.IndexOf("]]", _position + 4, StringComparison.Ordinal);
                    _position = end < 0 ? source.Length : end + 2;
                }
                else if (Matches("--"))
                {
                    var end = source.IndexOf('\n', _position);
                    _position = end < 0 ? source.Length : end + 1;
                }
                else
                {
                    return;
                }
            }
        }

        private bool Matches(string text) =>
            string.CompareOrdinal(source, _position, text, 0, text.Length) == 0;

        private static bool IsNameChar(char ch) => char.IsAsciiLetterOrDigit(ch) || ch == '_';

        private static bool IsValueEnd(char ch) =>
            char.IsWhiteSpace(ch) || ch is ',' or ';' or '}' or ']' or '{' or '"' or '\'';

        private static FormatException Malformed() =>
            new("KOReader metadata is not a readable Lua table.");
    }
}

using System.Text;
using Microsoft.Data.Sqlite;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// What an import file is made of, for the <c>HighlightImportShape</c> log
/// event (issue #1523): a database's tables and the columns of the tables the
/// readers use, a CSV's header cells, a text file's line and separator counts
/// with the masked format lines of the Kindle entries the parser does not
/// recognise, or a Lua sidecar's top-level keys. Structure only: never a
/// highlight, title, author, file name or path.
/// </summary>
internal static class ImportFileShape
{
    // Small enough for one log line, large enough for a changed schema.
    public const int MaxLength = 2 * 1024;

    private const string Unknown = "unknown";
    private const int MaxTables = 30;
    private const int MaxColumns = 60;
    private const int MaxCellLength = 40;
    private const int MaxPatternLength = 120;
    private const int MaxPatterns = 5;

    /// <summary>The source the file looks like, and what it is made of.</summary>
    public static (string Source, string Shape) Describe(string path)
    {
        if (KoboDatabaseReader.HasSqliteHeader(path))
            return DescribeDatabase(path);

        var length = new FileInfo(path).Length;
        if (length > HighlightImportSourceReader.MaxTextBytes)
            return (Unknown, $"file of {length} bytes; too large to read");

        return DescribeText(HighlightImportSourceReader.DecodeText(File.ReadAllBytes(path)));
    }

    /// <summary>
    /// A database's tables, and the columns of the tables the readers use:
    /// names only, in the order the file has them, so a firmware or export
    /// that added, removed or renamed one is visible at a glance.
    /// </summary>
    private static (string Source, string Shape) DescribeDatabase(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var tables = TableNames(connection);
        var source = tables.Contains(AppleBooksDatabaseReader.AnnotationTable, StringComparer.OrdinalIgnoreCase)
            || tables.Contains(AppleBooksDatabaseReader.LibraryTable, StringComparer.OrdinalIgnoreCase)
            ? HighlightImportSourceReader.AppleBooks
            : tables.Contains("Bookmark", StringComparer.OrdinalIgnoreCase)
                ? HighlightImportSourceReader.Kobo
                : Unknown;

        string[] read = source switch
        {
            HighlightImportSourceReader.AppleBooks =>
                [AppleBooksDatabaseReader.AnnotationTable, AppleBooksDatabaseReader.LibraryTable],
            HighlightImportSourceReader.Kobo => ["Bookmark", "content"],
            _ => [],
        };

        var parts = new List<string>(read.Length + 1);
        if (tables.Count > 0)
        {
            var listed = tables.Take(MaxTables).ToList();
            parts.Add("tables: " + string.Join(", ", listed)
                + (tables.Count > MaxTables ? ", …" : string.Empty));
        }

        foreach (var table in read)
        {
            var columns = ColumnNames(connection, table);
            if (columns.Count == 0)
                continue;
            var listed = columns.Take(MaxColumns).ToList();
            parts.Add($"{table}({string.Join(", ", listed)}"
                + (columns.Count > MaxColumns ? ", …" : string.Empty) + ")");
        }

        return (source, Cap(string.Join("; ", parts)));
    }

    /// <summary>The same description for text that arrived as a string, not a file.</summary>
    internal static (string Source, string Shape) DescribeText(string text)
    {
        if (ReadwiseCsvParser.Looks(text))
            return (HighlightImportSourceReader.Readwise, Cap($"header {Header(text)}"));

        // The source reader's order: a Lua sidecar is never a Kindle file.
        var lua = HighlightImportSourceReader.IsLuaSidecar(text);
        var kindle = !lua && KindleClippingsParser.Looks(text);
        var source = kindle ? HighlightImportSourceReader.Kindle
            : lua ? HighlightImportSourceReader.Koreader
            : Unknown;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var parts = new List<string>
        {
            $"lines {lines.Length}",
            $"separators {lines.Count(line => line.Trim() == KindleClippingsParser.Separator)}",
        };

        if (kindle)
        {
            var patterns = KindleClippingsParser.UnrecognisedFormatLines(text)
                .Select(Mask)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxPatterns)
                .ToList();
            parts.Add(patterns.Count == 0
                ? "unrecognised patterns: none"
                : "unrecognised patterns: " + string.Join(" | ", patterns));
        }

        if (lua)
        {
            var keys = KoreaderMetadataParser.TopLevelKeys(text);
            parts.Add(keys is null
                ? "lua table: keys unreadable"
                : "keys: " + string.Join(", ", keys.Take(MaxTables)));
        }

        return (source, Cap(string.Join("; ", parts)));
    }

    /// <summary>
    /// The first line is Readwise's own column names; the rows below it are the
    /// reader's highlights and are never read here.
    /// </summary>
    private static string Header(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = (end < 0 ? text : text[..end]).TrimStart('\uFEFF');
        var cells = ReadwiseCsvParser.Records(header).FirstOrDefault() ?? [];
        var shown = cells.Select(cell => cell.Length <= MaxCellLength ? cell : cell[..MaxCellLength] + "…");
        return $"({cells.Count} cells): {string.Join(", ", shown)}";
    }

    /// <summary>
    /// A line with every letter made <c>x</c> (or <c>X</c> where it was upper
    /// case) and digits, punctuation and spacing kept, so a language we do not
    /// parse shows its layout without showing its words:
    /// <c>- Xxxx xxx xx Xxxx 12 | Xxxxx 34</c>.
    /// </summary>
    internal static string Mask(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaxPatternLength));
        foreach (var ch in value)
        {
            if (builder.Length >= MaxPatternLength)
            {
                builder.Append('…');
                break;
            }

            builder.Append(char.IsLetter(ch) ? (char.IsUpper(ch) ? 'X' : 'x') : ch);
        }

        return builder.ToString();
    }

    private static List<string> TableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT 31";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static List<string> ColumnNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read() && columns.Count < MaxColumns + 1)
            columns.Add(reader.GetString(0));
        return columns;
    }

    internal static string Cap(string value) =>
        value.Length <= MaxLength ? value : value[..(MaxLength - 1)] + "…";
}

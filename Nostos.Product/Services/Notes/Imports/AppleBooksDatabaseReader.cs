using Microsoft.Data.Sqlite;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads Apple Books highlights from the two databases Books keeps on a Mac
/// (issue #1357). Highlights made on an iPhone or iPad reach the Mac through
/// iCloud, so this covers every Apple device:
/// <code>
/// ~/Library/Containers/com.apple.iBooksX/Data/Documents/AEAnnotation/AEAnnotation_v10312011_1727_local.sqlite
/// ~/Library/Containers/com.apple.iBooksX/Data/Documents/BKLibrary/BKLibrary-1-091020131601.sqlite
/// </code>
/// The annotation database holds the highlights (<c>ZAEANNOTATION</c>); the
/// library database holds each book's title and author (<c>ZBKLIBRARYASSET</c>),
/// joined by asset id. Without the library database the highlights still read,
/// one untitled book per asset, for the owner to place. Both are Core Data
/// stores whose recent writes can sit in a <c>-wal</c> file beside them; when
/// the owner includes it, SQLite reads it as part of the database.
/// </summary>
internal static class AppleBooksDatabaseReader
{
    public const string AnchorKind = "applebooks_cfi";
    internal const string AnnotationTable = "ZAEANNOTATION";
    internal const string LibraryTable = "ZBKLIBRARYASSET";

    internal const string NoLibraryNotice =
        "The Books library file was not included, so Nostos cannot see this book's title. Choose the book below, or import again with both files.";
    internal const string MissingAnnotationsMessage =
        "This is the Books library file. Choose the AEAnnotation file too: it holds the highlights.";

    public static bool HasTable(string path, string table)
    {
        try
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table";
            command.Parameters.AddWithValue("$table", table);
            return command.ExecuteScalar() is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>The annotation database, and the library database when one was chosen with it.</summary>
    public static IReadOnlyList<ImportSourceBook> Read(string annotationsPath, string? libraryPath)
    {
        var library = libraryPath is null ? new Dictionary<string, (string? Title, string? Author)>() : ReadLibrary(libraryPath);

        using var connection = Open(annotationsPath);
        var columns = Columns(connection, AnnotationTable);
        if (!columns.Contains("ZANNOTATIONASSETID"))
            throw new FormatException("This database has no Apple Books highlights. Choose the AEAnnotation file from the Books folder on your Mac.");

        using var command = connection.CreateCommand();
        // Deleted highlights stay in the table with a flag. Bookmarks and
        // reading positions are rows too, with neither text nor a note.
        command.CommandText =
            $"""
            SELECT ZANNOTATIONASSETID,
                   {Optional(columns, "ZANNOTATIONSELECTEDTEXT")},
                   {Optional(columns, "ZANNOTATIONNOTE")},
                   {Optional(columns, "ZANNOTATIONLOCATION")}
            FROM {AnnotationTable}
            WHERE {(columns.Contains("ZANNOTATIONDELETED") ? "COALESCE(ZANNOTATIONDELETED, 0) = 0" : "1 = 1")}
            ORDER BY ZANNOTATIONASSETID,
                     {(columns.Contains("ZPLLOCATIONRANGESTART") ? "ZPLLOCATIONRANGESTART," : string.Empty)}
                     {(columns.Contains("ZANNOTATIONCREATIONDATE") ? "ZANNOTATIONCREATIONDATE," : string.Empty)}
                     Z_PK
            """;

        var books = new Dictionary<string, List<ImportAnnotation>>(StringComparer.Ordinal);
        var order = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var assetId = Text(reader, 0);
                var text = Text(reader, 1);
                var note = Text(reader, 2) ?? string.Empty;
                if (assetId is null || (text is null && note.Length == 0))
                    continue;

                if (!books.TryGetValue(assetId, out var annotations))
                {
                    annotations = [];
                    books[assetId] = annotations;
                    order.Add(assetId);
                }

                var location = Text(reader, 3);
                annotations.Add(new ImportAnnotation(
                    AnchorKind,
                    location is not null && location.StartsWith("epubcfi(", StringComparison.Ordinal) ? location : null,
                    text,
                    note));
            }
        }

        return order
            .Select(assetId =>
            {
                library.TryGetValue(assetId, out var meta);
                var notice = libraryPath is null ? NoLibraryNotice : null;
                return new ImportSourceBook($"applebooks:{assetId}", meta.Title, meta.Author, null, books[assetId], notice);
            })
            .ToList();
    }

    private static Dictionary<string, (string? Title, string? Author)> ReadLibrary(string path)
    {
        var result = new Dictionary<string, (string? Title, string? Author)>(StringComparer.Ordinal);
        using var connection = Open(path);
        var columns = Columns(connection, LibraryTable);
        if (!columns.Contains("ZASSETID"))
            return result;

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ZASSETID, {Optional(columns, "ZTITLE")}, {Optional(columns, "ZAUTHOR")} FROM {LibraryTable}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Text(reader, 0) is { } assetId)
                result[assetId] = (Text(reader, 1), Text(reader, 2));
        }

        return result;
    }

    // The upload is a private copy, so it is opened read-write: SQLite then
    // folds a -wal file beside it into what it reads.
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static string Optional(HashSet<string> columns, string name) =>
        columns.Contains(name) ? name : $"NULL AS {name}";

    private static string? Text(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

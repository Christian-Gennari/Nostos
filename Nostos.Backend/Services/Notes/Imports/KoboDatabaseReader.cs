using Microsoft.Data.Sqlite;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads highlights and annotations out of an uploaded KoboReader.sqlite.
/// The file is user-supplied, so it is opened read-only and every column
/// beyond the bookmark's id and volume is treated as optional: the schema has
/// drifted across Kobo firmware versions.
/// </summary>
internal static class KoboDatabaseReader
{
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    public static IReadOnlyList<KoboVolume> Read(string path)
    {
        if (!HasSqliteHeader(path))
            throw new FormatException("This file is not a Kobo database. Choose KoboReader.sqlite from the .kobo folder on the device.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var bookmarkColumns = Columns(connection, "Bookmark");
            if (!bookmarkColumns.Contains("BookmarkID") || !bookmarkColumns.Contains("VolumeID"))
                throw new FormatException("This database has no Kobo bookmarks table. Choose KoboReader.sqlite from the .kobo folder on the device.");

            var metadata = ReadVolumeMetadata(connection);
            var grouped = new Dictionary<string, List<KoboBookmark>>(StringComparer.Ordinal);
            var order = new List<string>();

            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT BookmarkID, VolumeID,
                       {Optional(bookmarkColumns, "Text")},
                       {Optional(bookmarkColumns, "Annotation")},
                       {Optional(bookmarkColumns, "Hidden")}
                FROM Bookmark
                ORDER BY VolumeID, {(bookmarkColumns.Contains("DateCreated") ? "DateCreated," : string.Empty)} BookmarkID
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bookmarkId = Text(reader, 0);
                var volumeId = Text(reader, 1);
                if (bookmarkId is null || volumeId is null)
                    continue;

                // Kobo keeps deleted highlights as hidden rows.
                var hidden = Text(reader, 4);
                if (hidden is not null
                    && (hidden.Equals("true", StringComparison.OrdinalIgnoreCase) || hidden == "1"))
                    continue;

                if (!grouped.TryGetValue(volumeId, out var bookmarks))
                {
                    bookmarks = [];
                    grouped[volumeId] = bookmarks;
                    order.Add(volumeId);
                }

                bookmarks.Add(new KoboBookmark(bookmarkId, Text(reader, 2), Text(reader, 3)));
            }

            return order
                .Select(volumeId =>
                {
                    metadata.TryGetValue(volumeId, out var meta);
                    return new KoboVolume(
                        volumeId,
                        meta?.Title ?? TitleFromVolumeId(volumeId),
                        meta?.Author,
                        meta?.Isbn,
                        grouped[volumeId]);
                })
                .ToList();
        }
        catch (SqliteException ex)
        {
            throw new FormatException("This Kobo database could not be read. Copy KoboReader.sqlite from the device again and retry.", ex);
        }
    }

    private static Dictionary<string, VolumeMetadata> ReadVolumeMetadata(SqliteConnection connection)
    {
        var result = new Dictionary<string, VolumeMetadata>(StringComparer.Ordinal);
        var columns = Columns(connection, "content");
        if (!columns.Contains("ContentID"))
            return result;

        using var command = connection.CreateCommand();
        // ContentType 6 is the book row; chapters share the table.
        command.CommandText =
            $"""
            SELECT ContentID,
                   {Optional(columns, "Title")},
                   {Optional(columns, "Attribution")},
                   {Optional(columns, "ISBN")}
            FROM content
            WHERE ContentID IN (SELECT DISTINCT VolumeID FROM Bookmark)
            {(columns.Contains("ContentType") ? "AND ContentType = 6" : string.Empty)}
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var contentId = Text(reader, 0);
            if (contentId is null)
                continue;
            result[contentId] = new VolumeMetadata(Text(reader, 1), Text(reader, 2), Text(reader, 3));
        }

        return result;
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

    // Sideloaded books without a content row still carry their path:
    // file:///mnt/onboard/Author/Some Book.epub
    private static string? TitleFromVolumeId(string volumeId)
    {
        var name = volumeId[(volumeId.LastIndexOf('/') + 1)..];
        foreach (var extension in new[] { ".kepub.epub", ".epub", ".pdf", ".mobi", ".cbz", ".txt" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        name = Uri.UnescapeDataString(name).Trim();
        return name.Length == 0 || name.Length == volumeId.Length ? null : name;
    }

    private static bool HasSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header.SequenceEqual(SqliteMagic);
    }

    private sealed record VolumeMetadata(string? Title, string? Author, string? Isbn);
}

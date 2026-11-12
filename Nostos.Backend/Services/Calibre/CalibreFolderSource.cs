using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Services.Library.Imports;

namespace Nostos.Backend.Services.Calibre;

/// <summary>
/// The one Calibre library this server may read. The administrator makes the
/// folder visible (for a container, a read-only bind mount such as
/// <c>/mnt/calibre:/external/calibre:ro</c>) and names it here; the web
/// interface never sends a path.
///
/// Only <see cref="ReadAsync"/>, <see cref="OpenRead"/> and
/// <see cref="OpenCover"/> exist, and every open is read-only. The database is
/// opened with SQLite's readonly mode as well as <c>PRAGMA query_only</c>, and
/// the files are opened with read sharing, so the library can stay in use by
/// Calibre while an import reads it.
/// </summary>
public sealed class CalibreFolderSource : ICalibreLibrarySource, ICalibreLibraryAvailability
{
    public const string SourceKind = "calibre";

    /// <summary>Upper bound on books read in one scan, so a mistaken mount cannot run unbounded.</summary>
    public const int MaxBooks = 50_000;

    private const string CoverFile = "cover.jpg";

    private static readonly string[] SupportedFormats = ["EPUB", "PDF", "M4B", "M4A", "MP3"];

    private static readonly string[] ReadLabels = ["read", "isread", "readstatus"];
    private static readonly string[] DateReadLabels = ["dateread", "readdate", "datereading", "finished", "datefinished"];

    private readonly string? _root;
    private readonly string? _metadataPath;

    public CalibreFolderSource(IOptions<CalibreOptions> options)
    {
        var value = options.Value;
        var configured = value.LibraryRoot?.Trim();
        if (string.IsNullOrEmpty(configured))
            return;

        if (!Path.IsPathFullyQualified(configured))
        {
            throw new InvalidOperationException(
                $"'{CalibreOptions.SectionName}:LibraryRoot' must be an absolute path to the Calibre library folder.");
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        _metadataPath = Path.Combine(_root, "metadata.db");
    }

    public string Kind => SourceKind;

    public bool IsConfigured => _root is not null;

    public bool IsAvailable => _metadataPath is not null && File.Exists(_metadataPath);

    public string? CoverExtension => ".jpg";

    public async Task<CalibreSourceLibrary> ReadAsync(CancellationToken ct)
    {
        if (!IsAvailable)
            throw new CalibreLibraryException(
                "The Calibre library is not available. Check that the folder is mounted and holds metadata.db.");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _metadataPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 15,
        };

        await using var connection = new SqliteConnection(builder.ToString());

        try
        {
            await connection.OpenAsync(ct);

            // Belt and braces: the connection string already asks SQLite for a
            // read-only handle. This also makes a mistake a clear error rather
            // than a write into the user's library.
            await using var readOnly = connection.CreateCommand();
            readOnly.CommandText = "PRAGMA query_only = 1;";
            await readOnly.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException)
        {
            throw new CalibreLibraryException(
                "This folder does not hold a readable Calibre library. Check that metadata.db is Calibre's own database.");
        }

        var warnings = new List<string>();
        var rows = await ReadBooksAsync(connection, ct);
        if (rows.Count == 0)
        {
            return new CalibreSourceLibrary([], [], [], 0, false, warnings);
        }

        var authors = await ReadNamesAsync(connection, """
            SELECT bal.book, a.name
            FROM books_authors_link bal
            JOIN authors a ON a.id = bal.author
            ORDER BY bal.book, bal.id
            """, warnings, "authors", ct);
        var tags = await ReadNamesAsync(connection, """
            SELECT btl.book, t.name
            FROM books_tags_link btl
            JOIN tags t ON t.id = btl.tag
            ORDER BY btl.book, btl.id
            """, warnings, "tags", ct);
        var series = await ReadNamesAsync(connection, """
            SELECT bsl.book, s.name
            FROM books_series_link bsl
            JOIN series s ON s.id = bsl.series
            """, warnings, "series", ct);
        var publishers = await ReadNamesAsync(connection, """
            SELECT bpl.book, p.name
            FROM books_publishers_link bpl
            JOIN publishers p ON p.id = bpl.publisher
            """, warnings, "publishers", ct);
        var comments = await ReadNamesAsync(connection, "SELECT book, text FROM comments", warnings, "comments", ct);
        var ratings = await ReadRatingsAsync(connection, warnings, ct);
        var identifiers = await ReadIdentifiersAsync(connection, warnings, ct);
        var data = await ReadDataAsync(connection, warnings, ct);
        var custom = await ReadCustomColumnsAsync(connection, warnings, ct);

        var books = new List<CalibreSourceBook>(rows.Count);
        var skipped = new List<CalibreSkipped>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(row.Path))
            {
                skipped.Add(new CalibreSkipped(
                    row.Title, authors.TryGetValue(row.Id, out var missingAuthor) ? JoinAuthors(missingAuthor) : null,
                    1, "Calibre did not record a folder for this book."));
                continue;
            }

            var notices = new List<string>();
            var formats = ReadFormats(row, data, notices);
            if (formats.Count == 0)
            {
                skipped.Add(new CalibreSkipped(
                    row.Title,
                    authors.TryGetValue(row.Id, out var author) ? JoinAuthors(author) : null,
                    1,
                    "Calibre records no book file for this entry."));
                continue;
            }

            var coverRelative = row.HasCover ? row.Path + "/" + CoverFile : null;
            var coverPath = coverRelative is not null && Resolve(coverRelative) is not null
                ? coverRelative
                : null;
            var customRead = custom.Read.TryGetValue(row.Id, out var readValue) ? (bool?)readValue : null;
            var readAt = custom.DateRead.TryGetValue(row.Id, out var dateValue) ? (DateTime?)dateValue : null;

            books.Add(new CalibreSourceBook(
                (int)row.Id,
                string.IsNullOrWhiteSpace(row.Title) ? "Unknown" : row.Title,
                authors.TryGetValue(row.Id, out var names) ? JoinAuthors(names) : null,
                identifiers.TryGetValue(row.Id, out var isbn) ? isbn : null,
                series.TryGetValue(row.Id, out var seriesNames) ? seriesNames.FirstOrDefault() : null,
                row.SeriesIndex,
                publishers.TryGetValue(row.Id, out var publisherNames) ? publisherNames.FirstOrDefault() : null,
                row.PublishedDate,
                ratings.TryGetValue(row.Id, out var calibreRating) ? Rating(calibreRating) : 0,
                comments.TryGetValue(row.Id, out var comment) ? CalibreText.PlainText(comment.FirstOrDefault()) : null,
                tags.TryGetValue(row.Id, out var tagNames) ? tagNames : (IReadOnlyList<string>)[],
                customRead,
                readAt,
                formats,
                coverPath,
                notices));
        }

        var truncated = rows.Count > MaxBooks;
        if (truncated)
        {
            warnings.Add($"Only the first {MaxBooks} books were read; the library has more.");
            books = books.Take(MaxBooks).ToList();
        }

        return new CalibreSourceLibrary(
            books,
            skipped,
            custom.Columns,
            rows.Count,
            truncated,
            warnings);
    }

    public Stream? OpenRead(CalibreSourceFormat format)
    {
        var path = Resolve(format.Path);
        return path is null ? null : OpenReadOnly(path);
    }

    public Stream? OpenCover(CalibreSourceBook book)
    {
        var path = book.CoverPath is null ? null : Resolve(book.CoverPath);
        return path is null ? null : OpenReadOnly(path);
    }

    // ------------------------------------------------------------------
    // Database reading
    // ------------------------------------------------------------------

    private static async Task<List<BookRow>> ReadBooksAsync(SqliteConnection connection, CancellationToken ct)
    {
        var books = new List<BookRow>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, path, series_index, pubdate, has_cover
            FROM books
            ORDER BY id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", MaxBooks + 1);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var seriesIndex = reader.IsDBNull(3) ? null : FormatSeriesIndex(reader.GetDouble(3));
                books.Add(new BookRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? "Unknown" : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    seriesIndex,
                    reader.IsDBNull(4) ? null : FormatDate(reader.GetValue(4)),
                    !reader.IsDBNull(5) && reader.GetInt64(5) != 0));
            }
        }
        catch (SqliteException)
        {
            throw new CalibreLibraryException(
                "This folder does not hold a readable Calibre library. Check that metadata.db is Calibre's own database.");
        }

        return books;
    }

    private static async Task<Dictionary<long, List<string>>> ReadNamesAsync(
        SqliteConnection connection,
        string sql,
        List<string> warnings,
        string table,
        CancellationToken ct)
    {
        var values = new Dictionary<long, List<string>>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                var book = reader.GetInt64(0);
                if (!values.TryGetValue(book, out var list))
                    values[book] = list = [];
                list.Add(reader.GetString(1));
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // An older Calibre schema: the field is absent, not the import broken.
            warnings.Add($"The Calibre library has no {table} table; that field was left empty.");
        }

        return values;
    }

    private static async Task<Dictionary<long, int>> ReadRatingsAsync(
        SqliteConnection connection,
        List<string> warnings,
        CancellationToken ct)
    {
        var values = new Dictionary<long, int>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT brl.book, r.rating
                FROM books_ratings_link brl
                JOIN ratings r ON r.id = brl.rating
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                values[reader.GetInt64(0)] = (int)reader.GetInt64(1);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            warnings.Add("The Calibre library has no ratings table; ratings were left empty.");
        }

        return values;
    }

    private static async Task<Dictionary<long, string>> ReadIdentifiersAsync(
        SqliteConnection connection,
        List<string> warnings,
        CancellationToken ct)
    {
        var values = new Dictionary<long, string>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT book, type, val
                FROM identifiers
                WHERE lower(type) = 'isbn' OR lower(type) LIKE '%isbn%'
                ORDER BY book, id
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(2))
                    continue;
                var book = reader.GetInt64(0);
                var value = reader.GetString(2);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                // ISBN-13 is the canonical identity; an ISBN-10 is kept when it
                // is the only one Calibre records.
                var digits = new string(value.Where(char.IsDigit).ToArray());
                if (!values.TryGetValue(book, out var existing))
                {
                    values[book] = value;
                }
                else if (digits.Length == 13 && new string(existing.Where(char.IsDigit).ToArray()).Length != 13)
                {
                    values[book] = value;
                }
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            warnings.Add("The Calibre library has no identifiers table; ISBNs were left empty.");
        }

        return values;
    }

    private static async Task<List<DataRow>> ReadDataAsync(
        SqliteConnection connection,
        List<string> warnings,
        CancellationToken ct)
    {
        var values = new List<DataRow>();
        try
        {
            var hasName = await HasColumnAsync(connection, "data", "name", ct);
            await using var command = connection.CreateCommand();
            command.CommandText = hasName
                ? "SELECT book, format, uncompressed_size, name FROM data ORDER BY book, format"
                : "SELECT book, format, uncompressed_size, '' FROM data ORDER BY book, format";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                values.Add(new DataRow(
                    reader.GetInt64(0),
                    reader.GetString(1).ToUpperInvariant(),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            warnings.Add("The Calibre library has no data table; book folders were read directly.");
        }

        return values;
    }

    private static async Task<CustomColumnValues> ReadCustomColumnsAsync(
        SqliteConnection connection,
        List<string> warnings,
        CancellationToken ct)
    {
        var columns = new List<CalibreCustomColumn>();
        var readColumn = (long?)null;
        var dateColumn = (long?)null;
        var readLabelCount = 0;
        var dateLabelCount = 0;

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, label, name, datatype FROM custom_columns ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;

                var id = reader.GetInt64(0);
                var label = reader.GetString(1);
                var name = reader.IsDBNull(2) ? label : reader.GetString(2);
                var datatype = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var labelKey = ColumnKey(label);
                var nameKey = ColumnKey(name);

                var isRead = datatype.Equals("bool", StringComparison.OrdinalIgnoreCase)
                    && (ReadLabels.Contains(labelKey) || ReadLabels.Contains(nameKey));
                var isDateRead = datatype.Equals("datetime", StringComparison.OrdinalIgnoreCase)
                    && (DateReadLabels.Contains(labelKey) || DateReadLabels.Contains(nameKey));

                if (isRead)
                {
                    readLabelCount++;
                    if (readColumn is null)
                    {
                        readColumn = id;
                        columns.Add(new CalibreCustomColumn(
                            label, name, datatype, "used", "Sets whether the book was read."));
                        continue;
                    }
                }

                if (isDateRead)
                {
                    dateLabelCount++;
                    if (dateColumn is null)
                    {
                        dateColumn = id;
                        columns.Add(new CalibreCustomColumn(
                            label, name, datatype, "used", "Sets the book's read date."));
                        continue;
                    }
                }

                columns.Add(new CalibreCustomColumn(
                    label, name, datatype, "skipped", "Nostos has no field for this column."));
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            warnings.Add("The Calibre library has no custom columns; none were read.");
        }

        if (readLabelCount > 1)
            warnings.Add("More than one read column was found; only the first was used.");
        if (dateLabelCount > 1)
            warnings.Add("More than one read-date column was found; only the first was used.");

        var read = readColumn is { } readId
            ? await ReadValuesAsync(connection, readId, warnings, "read", ct)
            : new Dictionary<long, object>();
        var dates = dateColumn is { } dateId
            ? await ReadValuesAsync(connection, dateId, warnings, "date read", ct)
            : new Dictionary<long, object>();
        return new CustomColumnValues(columns, ToBooleans(read), ToDates(dates));
    }

    private static async Task<Dictionary<long, object>> ReadValuesAsync(
        SqliteConnection connection,
        long columnId,
        List<string> warnings,
        string label,
        CancellationToken ct)
    {
        var values = new Dictionary<long, object>();
        try
        {
            await using var command = connection.CreateCommand();
            // The id came from custom_columns, so the table name is a number.
            command.CommandText = $"SELECT book, value FROM custom_column_{columnId}";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                values[reader.GetInt64(0)] = reader.GetValue(1);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            warnings.Add($"The Calibre library's {label} column could not be read.");
        }

        return values;
    }

    private static Dictionary<long, bool> ToBooleans(Dictionary<long, object> values)
    {
        var result = new Dictionary<long, bool>();
        foreach (var (book, value) in values)
        {
            result[book] = value switch
            {
                bool flag => flag,
                long number => number != 0,
                string text => text is "1" or "true" or "True" or "yes" or "Yes",
                _ => false,
            };
        }

        return result;
    }

    private static Dictionary<long, DateTime> ToDates(Dictionary<long, object> values)
    {
        var result = new Dictionary<long, DateTime>();
        foreach (var (book, value) in values)
        {
            if (value is DateTime date)
                result[book] = date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
            else if (value is string text
                && DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                result[book] = parsed.UtcDateTime;
            }
        }

        return result;
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.FieldCount > 1 && reader.GetString(1) == column)
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Files
    // ------------------------------------------------------------------

    private List<CalibreSourceFormat> ReadFormats(
        BookRow book,
        IReadOnlyList<DataRow> data,
        List<string> notices)
    {
        var formats = new Dictionary<string, CalibreSourceFormat>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in data.Where(row => row.Book == book.Id))
        {
            if (formats.ContainsKey(row.Format))
                continue;

            var fileName = row.Name;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = FindFile(book.Path, row.Format);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                notices.Add($"Calibre records {row.Format} for this book, but its file is missing.");
                continue;
            }

            var relative = book.Path + "/" + fileName;
            var path = Resolve(relative);
            if (path is null || !File.Exists(path))
            {
                notices.Add($"{row.Format} is no longer in the Calibre library folder.");
                formats[row.Format] = new CalibreSourceFormat(
                    FormatKey(book.Id, row.Format),
                    row.Format,
                    fileName,
                    row.Bytes,
                    KindOf(row.Format),
                    relative,
                    Readable: false,
                    Problem: $"{row.Format} is no longer in the Calibre library folder.");
                continue;
            }

            var info = new FileInfo(path);
            var readable = true;
            string? problem = null;
            if (SupportedFormats.Contains(row.Format, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = OpenReadOnly(path);
                    problem = stream is null
                        ? "The file could not be opened."
                        : BookFileValidator.Validate(stream, fileName);
                    readable = problem is null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    readable = false;
                    problem = "The file could not be read.";
                }
            }

            formats[row.Format] = new CalibreSourceFormat(
                FormatKey(book.Id, row.Format),
                row.Format,
                fileName,
                info.Length,
                KindOf(row.Format),
                relative,
                readable,
                problem);
        }

        return formats.Values.ToList();
    }

    /// <summary>A legacy data row without a name: the folder's file for the format.</summary>
    private string? FindFile(string bookPath, string format)
    {
        var directory = ResolveDirectory(bookPath);
        if (directory is null)
            return null;

        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles()
                .Where(file => string.Equals(file.Extension, "." + format.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.Name)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? Resolve(string relativePath) => Walk(relativePath, directory: false);

    private string? ResolveDirectory(string relativePath) => Walk(relativePath, directory: true);

    /// <summary>
    /// Walks a reference from the root one segment at a time, refusing a
    /// symbolic link at any step. The last segment is a folder only when the
    /// caller says so; everything above it always is.
    /// </summary>
    private string? Walk(string relativePath, bool directory)
    {
        if (_root is null || !ExternalMediaRelativePath.TryGetSegments(relativePath, out var segments))
            return null;

        try
        {
            var current = _root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                FileSystemInfo entry = directory || i < segments.Length - 1
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                if (!entry.Exists || ExternalMediaRoot.IsLink(entry))
                    return null;
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static FileStream? OpenReadOnly(string path)
    {
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Small conversions
    // ------------------------------------------------------------------

    private static string JoinAuthors(IReadOnlyList<string> authors) =>
        string.Join(" & ", authors.Where(author => !string.IsNullOrWhiteSpace(author)));

    private static string FormatKey(long bookId, string format) => $"calibre:{bookId}:{format.ToUpperInvariant()}";

    private static string KindOf(string format) =>
        format.ToUpperInvariant() is "MP3" or "M4A" or "M4B" ? "audiobook" : "ebook";

    private static int Rating(long calibreRating) =>
        calibreRating <= 0 ? 0 : Math.Clamp((int)Math.Round(calibreRating / 2.0), 0, 5);

    private static string? FormatSeriesIndex(double index) =>
        index <= 0 ? null : index.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? FormatDate(object value)
    {
        if (value is DateTime date)
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (value is string text
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string ColumnKey(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private sealed record BookRow(
        long Id,
        string Title,
        string Path,
        string? SeriesIndex,
        string? PublishedDate,
        bool HasCover);

    private sealed record DataRow(long Book, string Format, long Bytes, string? Name);

    private sealed record CustomColumnValues(
        IReadOnlyList<CalibreCustomColumn> Columns,
        IReadOnlyDictionary<long, bool> Read,
        IReadOnlyDictionary<long, DateTime> DateRead);
}

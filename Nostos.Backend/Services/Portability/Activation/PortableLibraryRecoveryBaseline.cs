using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Raised when the portable state of a retained recovery copy is not provably
/// preserved by a forward schema upgrade. The message is safe to surface: it
/// never carries paths or row contents.
/// </summary>
public sealed class PortableRecoveryBaselineException(string message) : Exception(message);

/// <summary>One logical portable table's order-independent state evidence.</summary>
public sealed record PortableTableBaseline(
    string LogicalTable,
    bool Present,
    int RowCount,
    string Digest,
    IReadOnlyList<string> Columns);

/// <summary>
/// Schema-tolerant evidence of a retained recovery copy's portable relational
/// state, captured before a forward migration and required to be preserved
/// afterwards. The compared tables and columns are derived from the portability
/// completeness inventory (<see cref="PortableEntitySet"/>) and the EF model,
/// intersected with what the retained schema actually has; only explicitly
/// declared renames (see the alias maps) are translated. A migration that
/// deletes, adds or alters portable rows therefore fails the comparison instead
/// of silently redefining the expected library.
/// </summary>
public sealed class PortableRecoveryBaseline
{
    internal PortableRecoveryBaseline(IReadOnlyList<PortableTableBaseline> tables)
    {
        Tables = tables;
    }

    public IReadOnlyList<PortableTableBaseline> Tables { get; }
}

/// <summary>
/// Captures and verifies the portable-state baseline of a recovery database
/// across a forward schema migration. The retained original is never touched:
/// the caller captures from a working copy, migrates that copy, and verifies
/// the same shape against the migrated copy.
/// </summary>
public static class PortableLibraryRecoveryBaseline
{
    private static readonly Lazy<IReadOnlyList<(string Table, IReadOnlyList<string> Columns)>>
        PortableSchema = new(BuildPortableSchema, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Old table names that a known migration explicitly renames into the
    /// current logical table. Only these aliases are probed; any other rename
    /// or drop makes the logical table unresolvable after migration and fails.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OldTableAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Concepts"] = "Topics",
            ["NoteConcepts"] = "NoteTopics",
        };

    /// <summary>
    /// Old column names that known migrations explicitly rename, keyed by the
    /// logical table. Anything a migration changes without a declared mapping
    /// is a mismatch.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Table, string OldColumn), string> OldColumnAliases =
        BuildOldColumnAliases();

    /// <summary>
    /// The synthetic logical table that stands for what the library says about
    /// each book (rating, favourite, review). The columns that used to hold it
    /// per edition are rewritten by the migration that moved it onto the work,
    /// so row-by-row equality cannot hold; this evidence is what must survive.
    /// </summary>
    private const string BookValuesTable = "BookValues";

    private const string KeyedByWork = "keyed-by-work";
    private const string KeyedByBook = "keyed-by-book";

    public static async Task<PortableRecoveryBaseline> CaptureAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        await Task.CompletedTask;
        return Capture(databasePath);
    }

    /// <summary>
    /// Recomputes exactly the captured shape against the migrated database and
    /// requires row counts, column presence and order-independent row digests
    /// to match. Tables that were absent may only appear empty afterwards.
    /// </summary>
    public static async Task RequirePreservedAsync(
        string databasePath,
        PortableRecoveryBaseline before,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(before);
        await Task.CompletedTask;

        using var connection = Open(databasePath);
        var tables = ReadTableNames(connection);
        foreach (var table in before.Tables)
        {
            ct.ThrowIfCancellationRequested();
            if (table.LogicalTable == BookValuesTable)
            {
                var keyedByBook = table.Columns.Contains(KeyedByBook);
                var (valueCount, valueDigest) = ComputeBookValues(connection, tables, keyedByBook);
                if (valueCount != table.RowCount || !string.Equals(valueDigest, table.Digest, StringComparison.Ordinal))
                {
                    throw Mismatch();
                }

                continue;
            }

            var actual = ResolveActualTable(tables, table.LogicalTable);
            if (!table.Present)
            {
                if (actual is not null && CountRows(connection, actual) > 0)
                {
                    throw Mismatch();
                }

                continue;
            }

            if (actual is null)
            {
                throw Mismatch();
            }

            var actualColumns = ReadColumns(connection, actual);
            var resolved = ResolveActualColumns(table.LogicalTable, actualColumns, table.Columns);
            if (resolved.Length != table.Columns.Count)
            {
                throw Mismatch();
            }

            var (count, digest) = Compute(connection, actual, resolved);
            if (count != table.RowCount || !string.Equals(digest, table.Digest, StringComparison.Ordinal))
            {
                throw Mismatch();
            }
        }
    }

    /// <summary>
    /// Requires the extracted portable payload's per-kind counts to equal the
    /// counts the retention manifest recorded for the same generation.
    /// <see cref="MigrationExistingCounts.AssistantSettings"/> counts rows that
    /// carry a portable value while the payload counts the singleton's
    /// presence, so the latter may be one when the former is zero.
    /// </summary>
    public static void RequireCountsPreserved(
        MigrationExistingCounts expected,
        MigrationArchiveCounts actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        Equal(expected.Works, actual.Works);
        Equal(expected.Books, actual.Books);
        Equal(expected.Notes, actual.Notes);
        Equal(expected.Topics, actual.Topics);
        Equal(expected.NoteTopics, actual.NoteTopics);
        Equal(expected.Writings, actual.Writings);
        Equal(expected.WritingNotes, actual.WritingNotes);
        Equal(expected.Collections, actual.Collections);
        Equal(expected.BookCollections, actual.CollectionMemberships);
        Equal(expected.Acquisitions, actual.Acquisitions);
        Equal(expected.NoteImportBookLinks, actual.NoteImportBookLinks);
        Equal(expected.ReadingListEntries, actual.ReadingListEntries);
        if (expected.AssistantSettings > 0 && actual.AssistantSettings != 1)
        {
            throw Mismatch();
        }

        static void Equal(long expectedCount, long actualCount)
        {
            if (expectedCount != actualCount)
            {
                throw Mismatch();
            }
        }
    }

    private static PortableRecoveryBaseline Capture(string databasePath)
    {
        using var connection = Open(databasePath);
        var tables = ReadTableNames(connection);
        var result = new List<PortableTableBaseline>(PortableSchema.Value.Count);
        foreach (var (logicalTable, modelColumns) in PortableSchema.Value)
        {
            var actual = ResolveActualTable(tables, logicalTable);
            if (actual is null)
            {
                result.Add(new PortableTableBaseline(logicalTable, Present: false, 0, ZeroDigest, []));
                continue;
            }

            var actualColumns = ReadColumns(connection, actual);
            var resolved = ResolveActualColumns(logicalTable, actualColumns, modelColumns);
            if (resolved.Length == 0)
            {
                throw Mismatch();
            }

            if (logicalTable == "Books" && !HasBookValuesOnWork(connection, tables))
            {
                // The retained schema still keeps rating and review per edition and
                // the migration moves them onto the work. Those two columns are
                // checked as book-level evidence below instead of row by row.
                resolved = resolved
                    .Where(item => item.Logical is not ("Progress_Rating" or "Progress_PersonalReview"))
                    .ToArray();
            }

            var (count, digest) = Compute(connection, actual, resolved);
            result.Add(new PortableTableBaseline(
                logicalTable, Present: true, count, digest,
                resolved.Select(item => item.Logical).ToArray()));
        }

        if (tables.Contains("Books") && !HasBookValuesOnWork(connection, tables))
        {
            var keyedByBook = !ReadColumns(connection, "Books").Contains("WorkId");
            var (valueCount, valueDigest) = ComputeBookValues(connection, tables, keyedByBook);
            result.Add(new PortableTableBaseline(
                BookValuesTable, Present: true, valueCount, valueDigest,
                [keyedByBook ? KeyedByBook : KeyedByWork]));
        }

        return new PortableRecoveryBaseline(result);
    }

    private static bool HasBookValuesOnWork(SqliteConnection connection, IReadOnlySet<string> tables) =>
        tables.Contains("Works") && ReadColumns(connection, "Works").Contains("Rating");

    /// <summary>
    /// What the library says about each book, as an order-independent set of
    /// facts: "this book has rating N", "this book has review X", "this book is
    /// a favourite". Read from either schema. Before the values moved onto the
    /// work every edition carried them, so a book's facts are the union of its
    /// editions'; after, they are the work's plus any edition's own exception.
    /// The migration only moves facts (agreeing ones merge into one), so the set
    /// is identical before and after unless something was lost.
    /// </summary>
    private static (int Count, string Digest) ComputeBookValues(
        SqliteConnection connection,
        IReadOnlySet<string> tables,
        bool keyedByBook)
    {
        var bookColumns = ReadColumns(connection, "Books");
        string? Pick(params string[] names) => names.FirstOrDefault(bookColumns.Contains);
        var rating = Pick("Progress_Rating", "Rating");
        var review = Pick("Progress_PersonalReview", "PersonalReview");
        var favourite = Pick("Progress_IsFavorite", "IsFavorite");
        var facts = new HashSet<string>(StringComparer.Ordinal);

        void Add(string key, string kind, string? value) => facts.Add($"{key}|{kind}|{value}");

        var key = keyedByBook ? "\"Id\"" : "\"WorkId\"";
        var onWork = HasBookValuesOnWork(connection, tables);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = keyedByBook && onWork
                // One work per book before work grouping existed: a book's facts
                // are its edition's own value, else its work's.
                ? "SELECT b.\"Id\", COALESCE(NULLIF(b.\"Progress_Rating\", 0), w.\"Rating\"), "
                  + "COALESCE(NULLIF(b.\"Progress_PersonalReview\", ''), w.\"PersonalReview\"), w.\"IsFavorite\" "
                  + "FROM \"Books\" b LEFT JOIN \"Works\" w ON w.\"Id\" = b.\"WorkId\""
                : $"SELECT {key}, {(rating is null ? "0" : Quote(rating))}, {(review is null ? "NULL" : Quote(review))}, "
                  + $"{(favourite is null ? "0" : Quote(favourite))} FROM \"Books\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = Canonical(reader.GetValue(0));
                if (!reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) > 0)
                    Add(id, "R", Canonical(reader.GetValue(1)));
                if (!reader.IsDBNull(2) && reader.GetString(2).Length > 0)
                    Add(id, "V", reader.GetString(2));
                if (!reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) > 0)
                    Add(id, "F", null);
            }
        }

        if (onWork && !keyedByBook)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Id\", \"Rating\", \"PersonalReview\", \"IsFavorite\" FROM \"Works\";";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = Canonical(reader.GetValue(0));
                if (!reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) > 0)
                    Add(id, "R", Canonical(reader.GetValue(1)));
                if (!reader.IsDBNull(2) && reader.GetString(2).Length > 0)
                    Add(id, "V", reader.GetString(2));
                if (!reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) > 0)
                    Add(id, "F", null);
            }
        }

        var accumulator = new byte[32];
        foreach (var fact in facts)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(fact));
            for (var index = 0; index < accumulator.Length; index++)
            {
                accumulator[index] ^= digest[index];
            }
        }

        return (facts.Count, Convert.ToHexString(accumulator).ToLowerInvariant());
    }

    private static SqliteConnection Open(string databasePath)
    {
        // The working copy is host-owned; opening read-write lets SQLite
        // recover a WAL the migration may have left behind. Only SELECTs run.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static HashSet<string> ReadTableNames(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string? ResolveActualTable(IReadOnlySet<string> tables, string logicalTable)
    {
        if (tables.Contains(logicalTable)) return logicalTable;
        foreach (var (oldName, target) in OldTableAliases)
        {
            if (string.Equals(target, logicalTable, StringComparison.Ordinal) && tables.Contains(oldName))
            {
                return oldName;
            }
        }

        return null;
    }

    private static List<string> ReadColumns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>
    /// Maps the requested logical columns onto the columns actually present,
    /// translating only declared old names. A requested logical column with no
    /// actual counterpart is omitted; the caller treats that as a mismatch.
    /// </summary>
    private static (string Logical, string Actual)[] ResolveActualColumns(
        string logicalTable,
        IReadOnlyList<string> actualColumns,
        IReadOnlyList<string> logicalColumns)
    {
        var byLogical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var actual in actualColumns)
        {
            var logical = OldColumnAliases.TryGetValue((logicalTable, actual), out var current)
                ? current
                : actual;
            byLogical.TryAdd(logical, actual);
        }

        var resolved = new List<(string, string)>(logicalColumns.Count);
        foreach (var logical in logicalColumns)
        {
            if (byLogical.TryGetValue(logical, out var actual))
            {
                resolved.Add((logical, actual));
            }
        }

        return resolved.ToArray();
    }

    private static int CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static (int Count, string Digest) Compute(
        SqliteConnection connection,
        string table,
        IReadOnlyList<(string Logical, string Actual)> columns)
    {
        var accumulator = new byte[32];
        var count = 0;
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(item => Quote(item.Actual)))} FROM {Quote(table)};";
        using var reader = command.ExecuteReader();
        var row = new StringBuilder(256);
        while (reader.Read())
        {
            row.Clear();
            for (var index = 0; index < columns.Count; index++)
            {
                var logical = columns[index].Logical;
                row.Append(logical.Length).Append(':').Append(logical).Append('=');
                var value = Canonical(reader.IsDBNull(index) ? null : reader.GetValue(index));
                row.Append(value.Length).Append(':').Append(value).Append('|');
            }

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(row.ToString()));
            for (var index = 0; index < accumulator.Length; index++)
            {
                accumulator[index] ^= digest[index];
            }

            count++;
        }

        return (count, Convert.ToHexString(accumulator).ToLowerInvariant());
    }

    private static string Canonical(object? value) => value switch
    {
        null => "\u0000",
        DBNull => "\u0000",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static string Quote(string identifier) => $"\"{identifier}\"";

    private static PortableRecoveryBaselineException Mismatch() => new(
        "The retained library's portable state changed during the schema upgrade; "
        + "the recovery copy cannot be used safely.");

    private static string ZeroDigest { get; } = new string('0', 64);

    private static IReadOnlyList<(string Table, IReadOnlyList<string> Columns)> BuildPortableSchema()
    {
        using var model = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);
        var portableTables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in PortableEntitySet.EntityTypes)
        {
            if (model.Model.FindEntityType(type)?.GetTableName() is { } table)
            {
                portableTables.Add(table);
            }
        }

        var columnsByTable = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var entity in model.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } table || !portableTables.Contains(table)) continue;
            foreach (var property in entity.GetProperties())
            {
                if (property.IsShadowProperty()) continue;
                if (property.GetColumnName() is not { } column) continue;
                if (!columnsByTable.TryGetValue(table, out var set))
                {
                    columnsByTable[table] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                set.Add(column);
            }
        }

        return columnsByTable
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, (IReadOnlyList<string>)pair.Value.ToArray()))
            .ToArray();
    }

    private static IReadOnlyDictionary<(string Table, string OldColumn), string> BuildOldColumnAliases()
    {
        var aliases = new Dictionary<(string, string), string>
        {
            [("Topics", "Concept")] = "Topic",
            [("NoteTopics", "ConceptId")] = "TopicId",
        };
        // RefactorBookModel moved the flattened Books columns onto the owned
        // Metadata/Progress/FileDetails navigation prefixes.
        foreach (var (oldName, newName) in new (string Old, string New)[]
                 {
                     ("VolumeNumber", "Metadata_VolumeNumber"),
                     ("Translator", "Metadata_Translator"),
                     ("Subtitle", "Metadata_Subtitle"),
                     ("Series", "Metadata_Series"),
                     ("Rating", "Progress_Rating"),
                     ("Publisher", "Metadata_Publisher"),
                     ("PublishedDate", "Metadata_PublishedDate"),
                     ("ProgressPercent", "Progress_ProgressPercent"),
                     ("PlaceOfPublication", "Metadata_PlaceOfPublication"),
                     ("PersonalReview", "Progress_PersonalReview"),
                     ("LastReadAt", "Progress_LastReadAt"),
                     ("LastLocation", "Progress_LastLocation"),
                     ("Language", "Metadata_Language"),
                     ("IsFavorite", "Progress_IsFavorite"),
                     ("HasFile", "FileDetails_HasFile"),
                     ("FinishedAt", "Progress_FinishedAt"),
                     ("FileName", "FileDetails_FileName"),
                     ("Editor", "Metadata_Editor"),
                     ("Edition", "Metadata_Edition"),
                     ("Description", "Metadata_Description"),
                     ("CoverFileName", "FileDetails_CoverFileName"),
                     ("ChaptersJson", "FileDetails_ChaptersJson"),
                     ("Categories", "Metadata_Categories"),
                 })
        {
            aliases[("Books", oldName)] = newName;
        }

        return aliases;
    }
}

using Microsoft.Data.Sqlite;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// Apple Books' two Mac databases, with the tables and columns Books itself
/// uses (Core Data stores: Z-prefixed names, dates in seconds since 2001).
/// The column set follows what open-source readers of these files query, so a
/// fixture fails where a real file would.
/// </summary>
internal static class AppleBooksFixtures
{
    public const string AnnotationFileName = "AEAnnotation_v10312011_1727_local.sqlite";
    public const string LibraryFileName = "BKLibrary-1-091020131601.sqlite";

    public sealed record Annotation(
        string AssetId,
        string? Text,
        string? Note = null,
        string? Location = "epubcfi(/6/12[chapter1]!/4/2/32/2/1,:0,:48)",
        int Deleted = 0,
        double RangeStart = 0,
        string? Chapter = "Chapter One");

    public sealed record Asset(string AssetId, string? Title, string? Author);

    private const string AnnotationSchema = """
        CREATE TABLE ZAEANNOTATION (
            Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER,
            ZANNOTATIONDELETED INTEGER, ZANNOTATIONISUNDERLINE INTEGER, ZANNOTATIONSTYLE INTEGER,
            ZANNOTATIONTYPE INTEGER, ZPLLOCATIONRANGEEND INTEGER, ZPLLOCATIONRANGESTART INTEGER,
            ZANNOTATIONCREATIONDATE TIMESTAMP, ZANNOTATIONMODIFICATIONDATE TIMESTAMP,
            ZANNOTATIONASSETID VARCHAR, ZANNOTATIONLOCATION VARCHAR, ZANNOTATIONNOTE VARCHAR,
            ZANNOTATIONREPRESENTATIVETEXT VARCHAR, ZANNOTATIONSELECTEDTEXT VARCHAR,
            ZANNOTATIONUUID VARCHAR, ZFUTUREPROOFING5 VARCHAR);
        CREATE TABLE Z_METADATA (Z_VERSION INTEGER PRIMARY KEY, Z_UUID VARCHAR(255), Z_PLIST BLOB);
        """;

    private const string LibrarySchema = """
        CREATE TABLE ZBKLIBRARYASSET (
            Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER,
            ZASSETID VARCHAR, ZAUTHOR VARCHAR, ZGENRE VARCHAR, ZPATH VARCHAR,
            ZSORTAUTHOR VARCHAR, ZSORTTITLE VARCHAR, ZTITLE VARCHAR, ZPURCHASEDATE TIMESTAMP);
        CREATE TABLE Z_METADATA (Z_VERSION INTEGER PRIMARY KEY, Z_UUID VARCHAR(255), Z_PLIST BLOB);
        """;

    public static string CreateAnnotations(string directory, params Annotation[] rows)
    {
        var path = Path.Combine(directory, AnnotationFileName);
        using var connection = Open(path);
        Execute(connection, AnnotationSchema);
        InsertAnnotations(connection, rows);
        return path;
    }

    public static string CreateLibrary(string directory, params Asset[] assets)
    {
        var path = Path.Combine(directory, LibraryFileName);
        using var connection = Open(path);
        Execute(connection, LibrarySchema);
        foreach (var asset in assets)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO ZBKLIBRARYASSET (Z_ENT, Z_OPT, ZASSETID, ZTITLE, ZAUTHOR, ZSORTTITLE, ZGENRE) VALUES (3, 1, $id, $title, $author, $title, 'Fiction')";
            command.Parameters.AddWithValue("$id", asset.AssetId);
            command.Parameters.AddWithValue("$title", (object?)asset.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$author", (object?)asset.Author ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        return path;
    }

    /// <summary>
    /// An annotation database whose newest rows exist only in its -wal file,
    /// as when Books is still open. Returns the database and its -wal file,
    /// copied into <paramref name="directory"/>.
    /// </summary>
    public static (string Database, string Wal) CreateAnnotationsWithPendingWal(
        string directory, Annotation[] checkpointed, Annotation[] pending)
    {
        var work = Directory.CreateTempSubdirectory("nostos-applebooks-wal-").FullName;
        try
        {
            var path = Path.Combine(work, AnnotationFileName);
            using var connection = Open(path);
            Execute(connection, "PRAGMA journal_mode = WAL;");
            Execute(connection, AnnotationSchema);
            InsertAnnotations(connection, checkpointed);
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "PRAGMA wal_autocheckpoint = 0;");
            InsertAnnotations(connection, pending);

            // Copied while the connection is open, so nothing is checkpointed.
            var database = Path.Combine(directory, AnnotationFileName);
            var wal = database + "-wal";
            CopyShared(path, database);
            CopyShared(path + "-wal", wal);
            return (database, wal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void InsertAnnotations(SqliteConnection connection, IEnumerable<Annotation> rows)
    {
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ZAEANNOTATION (Z_ENT, Z_OPT, ZANNOTATIONDELETED, ZANNOTATIONSTYLE, ZANNOTATIONTYPE,
                    ZPLLOCATIONRANGESTART, ZANNOTATIONCREATIONDATE, ZANNOTATIONMODIFICATIONDATE,
                    ZANNOTATIONASSETID, ZANNOTATIONLOCATION, ZANNOTATIONNOTE, ZANNOTATIONSELECTEDTEXT,
                    ZANNOTATIONUUID, ZFUTUREPROOFING5)
                VALUES (2, 1, $deleted, 3, 2, $start, 685151385.91602, 685151385.91602,
                    $asset, $location, $note, $text, $uuid, $chapter)
                """;
            command.Parameters.AddWithValue("$deleted", row.Deleted);
            command.Parameters.AddWithValue("$start", row.RangeStart);
            command.Parameters.AddWithValue("$asset", row.AssetId);
            command.Parameters.AddWithValue("$location", (object?)row.Location ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)row.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("$text", (object?)row.Text ?? DBNull.Value);
            command.Parameters.AddWithValue("$uuid", Guid.NewGuid().ToString("D").ToUpperInvariant());
            command.Parameters.AddWithValue("$chapter", (object?)row.Chapter ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = File.Create(to);
        source.CopyTo(target);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

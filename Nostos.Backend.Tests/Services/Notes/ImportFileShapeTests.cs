using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

public sealed class ImportFileShapeTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nostos-import-shape-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_kobo_database_lists_its_tables_and_the_columns_the_reader_reads()
    {
        var path = CreateDatabase(
            """
            CREATE TABLE content (
                ContentID TEXT NOT NULL, ContentType TEXT NOT NULL, Title TEXT,
                Attribution TEXT, ISBN TEXT, PRIMARY KEY (ContentID));
            CREATE TABLE Bookmark (
                BookmarkID TEXT NOT NULL, VolumeID TEXT NOT NULL, Text TEXT,
                Annotation TEXT, Hidden BOOL, Type TEXT, DateCreated TEXT,
                PRIMARY KEY (BookmarkID));
            CREATE TABLE Shelf (Name TEXT);
            """);

        var (source, shape) = ImportFileShape.Describe(path);

        source.Should().Be(HighlightImportSourceReader.Kobo);
        shape.Should().Contain("tables: Bookmark, Shelf, content");
        shape.Should().Contain("Bookmark(BookmarkID, VolumeID, Text, Annotation, Hidden, Type, DateCreated)");
        shape.Should().Contain("content(ContentID, ContentType, Title, Attribution, ISBN)");
    }

    [Fact]
    public void A_readwise_export_shows_its_header_and_never_a_row()
    {
        var (source, shape) = ImportFileShape.Describe(ImportFixtures.ReadwiseExport);

        source.Should().Be(HighlightImportSourceReader.Readwise);
        shape.Should().StartWith("header (11 cells): Highlight, Book Title, Book Author");
        shape.Should().Contain("Amazon Book ID");

        // The rows hold the reader's highlights; only the header is structure.
        shape.Should().NotContain("Pride and Prejudice");
        shape.Should().NotContain("Jane Austen");
        shape.Should().NotContain("It is a truth");
        shape.Should().NotContain("A second synthetic highlight");
        shape.Should().NotContain("Tweets From Ada Reader");
    }

    [Fact]
    public void A_kindle_file_counts_lines_and_masks_the_format_of_unrecognised_entries()
    {
        var path = Write(
            "My Clippings.txt",
            """
            Qo'noS (Martok)
            - tlhIngan Hol 12 | 'ej 34

            nuqneH
            ==========
            """);

        var (source, shape) = ImportFileShape.Describe(path);

        source.Should().Be(HighlightImportSourceReader.Kindle);
        shape.Should().Contain("lines 5");
        shape.Should().Contain("separators 1");
        shape.Should().Contain("unrecognised patterns: - xxxXxxxx Xxx 12 | 'xx 34");

        // The title and the highlight never reach the shape.
        shape.Should().NotContain("Qo'noS");
        shape.Should().NotContain("Martok");
        shape.Should().NotContain("nuqneH");
    }

    [Fact]
    public void At_most_five_distinct_unrecognised_patterns_are_kept()
    {
        var blocks = Enumerable.Range(1, 8).Select(index =>
            $"Book {index} (Author, Someone)\n- Unparsed Thing {index} | Else {index}\n\nSecret {index}.");
        var path = Write("My Clippings.txt", string.Join("\n==========\n", blocks) + "\n==========\n");

        var (_, shape) = ImportFileShape.Describe(path);

        shape.Should().Contain("- Xxxxxxxx Xxxxx 1 | Xxxx 1");
        shape.Should().Contain("- Xxxxxxxx Xxxxx 5 | Xxxx 5");
        shape.Should().NotContain("- Xxxxxxxx Xxxxx 6 | Xxxx 6");
    }

    [Fact]
    public void A_koreader_sidecar_shows_only_its_top_level_keys()
    {
        var (source, shape) = ImportFileShape.Describe(ImportFixtures.KoreaderEpub);

        source.Should().Be(HighlightImportSourceReader.Koreader);
        shape.Should().Contain("keys: doc_props, annotations");
        shape.Should().NotContain("Synthetic Reader Book");
        shape.Should().NotContain("Ada Reader");
        shape.Should().NotContain("A synthetic highlighted sentence");
    }

    [Fact]
    public void An_unreadable_file_is_described_as_unknown_without_its_text()
    {
        var path = Write("notes.txt", "A line that is not any export format.");

        var (source, shape) = ImportFileShape.Describe(path);

        source.Should().Be("unknown");
        shape.Should().Contain("lines 1");
        shape.Should().NotContain("not any export format");
    }

    [Fact]
    public void The_shape_is_capped_so_one_line_stays_one_log_line()
    {
        var header = string.Join(",", new[] { "Highlight", "Book Title" }
            .Concat(Enumerable.Range(1, 100).Select(index => $"Extra Column Number {index:D3}")));
        var path = Write("export.csv", header + "\n");

        var (source, shape) = ImportFileShape.Describe(path);

        source.Should().Be(HighlightImportSourceReader.Readwise);
        shape.Should().StartWith("header (102 cells): Highlight, Book Title, Extra Column Number 001");
        shape.Should().HaveLength(ImportFileShape.MaxLength);
        shape.Should().EndWith("…");
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, text);
        return path;
    }

    private string CreateDatabase(string schema)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.sqlite");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.ExecuteNonQuery();
        return path;
    }
}

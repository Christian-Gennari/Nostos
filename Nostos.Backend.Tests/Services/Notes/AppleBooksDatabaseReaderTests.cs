using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;
using static Nostos.Backend.Tests.Support.AppleBooksFixtures;

namespace Nostos.Backend.Tests.Services.Notes;

public sealed class AppleBooksDatabaseReaderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nostos-applebooks-test-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Highlights_are_read_by_book_with_title_author_note_and_position()
    {
        var annotations = CreateAnnotations(_directory,
            new Annotation("ASSET-WALDEN", "I went to the woods because I wished to live deliberately.", "Why I keep reading it.", RangeStart: 20),
            new Annotation("ASSET-WALDEN", "Our life is frittered away by detail.", RangeStart: 10),
            new Annotation("ASSET-PP", "It is a truth universally acknowledged.", Location: "epubcfi(/6/4[c1]!/4/2/1,:0,:39)"));
        var library = CreateLibrary(_directory,
            new Asset("ASSET-WALDEN", "Walden", "Henry David Thoreau"),
            new Asset("ASSET-PP", "Pride and Prejudice", "Jane Austen"),
            new Asset("ASSET-NONE", "A Book Without Highlights", "Nobody"));

        var file = HighlightImportSourceReader.Read(annotations, [library]);

        file.Source.Should().Be(HighlightImportSourceReader.AppleBooks);
        file.Books.Select(book => (book.SourceKey, book.Title, book.Author, book.Notice)).Should().Equal(
            ("applebooks:ASSET-PP", "Pride and Prejudice", "Jane Austen", null),
            ("applebooks:ASSET-WALDEN", "Walden", "Henry David Thoreau", null));

        // In reading order, not in the order they were made.
        file.Books[1].Annotations.Should().Equal(
            new ImportAnnotation(AppleBooksDatabaseReader.AnchorKind, "epubcfi(/6/12[chapter1]!/4/2/32/2/1,:0,:48)", "Our life is frittered away by detail.", ""),
            new ImportAnnotation(AppleBooksDatabaseReader.AnchorKind, "epubcfi(/6/12[chapter1]!/4/2/32/2/1,:0,:48)", "I went to the woods because I wished to live deliberately.", "Why I keep reading it."));
        file.Books[0].Annotations.Single().AnchorValue.Should().Be("epubcfi(/6/4[c1]!/4/2/1,:0,:39)");
    }

    [Fact]
    public void Deleted_highlights_bookmarks_and_odd_locations_are_left_out_or_kept_without_a_position()
    {
        var annotations = CreateAnnotations(_directory,
            new Annotation("A", "Kept."),
            new Annotation("A", "Deleted in Books.", Deleted: 1),
            new Annotation("A", null),                       // a bookmark or reading position
            new Annotation("A", null, Note: "A note with no passage."),
            new Annotation("A", "No position.", Location: "not-a-cfi"));

        var book = HighlightImportSourceReader.Read(annotations, []).Books.Single();

        book.Annotations.Select(annotation => (annotation.Text, annotation.Note, annotation.AnchorValue is null)).Should().Equal(
            ("Kept.", "", false),
            (null, "A note with no passage.", false),
            ("No position.", "", true));
    }

    [Fact]
    public void Without_the_library_file_each_book_is_untitled_and_says_why()
    {
        var annotations = CreateAnnotations(_directory, new Annotation("ASSET-1", "A passage."));

        var book = HighlightImportSourceReader.Read(annotations, []).Books.Single();

        (book.Title, book.Author).Should().Be((null, null));
        book.Notice.Should().Be(AppleBooksDatabaseReader.NoLibraryNotice);
    }

    [Fact]
    public void The_two_files_can_come_in_either_order_but_the_library_alone_is_refused()
    {
        var annotations = CreateAnnotations(_directory, new Annotation("ASSET-1", "A passage."));
        var library = CreateLibrary(_directory, new Asset("ASSET-1", "Walden", "Henry David Thoreau"));

        HighlightImportSourceReader.Read(library, [annotations]).Books.Single().Title.Should().Be("Walden");

        var alone = () => HighlightImportSourceReader.Read(library, []);
        alone.Should().Throw<FormatException>().WithMessage(AppleBooksDatabaseReader.MissingAnnotationsMessage);
    }

    [Fact]
    public void A_kobo_database_is_still_read_as_kobo()
    {
        var path = Path.Combine(_directory, "KoboReader.sqlite");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE content (ContentID TEXT, ContentType TEXT, Title TEXT, Attribution TEXT);
                CREATE TABLE Bookmark (BookmarkID TEXT, VolumeID TEXT, Text TEXT, Annotation TEXT);
                INSERT INTO content VALUES ('vol', '6', 'A Kobo Book', 'Ada');
                INSERT INTO Bookmark VALUES ('bm', 'vol', 'Kobo text.', NULL);
                """;
            command.ExecuteNonQuery();
        }

        HighlightImportSourceReader.Read(path, []).Source.Should().Be(HighlightImportSourceReader.Kobo);
    }
}

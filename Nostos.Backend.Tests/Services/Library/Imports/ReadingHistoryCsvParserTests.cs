using FluentAssertions;
using Nostos.Backend.Services.Library.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library.Imports;

public sealed class ReadingHistoryCsvParserTests
{
    private static readonly string Goodreads = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "goodreads", "goodreads_library_export.csv");

    private static readonly string Storygraph = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "storygraph", "storygraph_export.csv");

    [Fact]
    public void Goodreads_is_detected_by_its_header_and_parsed_by_column_name()
    {
        var text = File.ReadAllText(Goodreads);
        GoodreadsCsvParser.Looks(text).Should().BeTrue();
        StorygraphCsvParser.Looks(text).Should().BeFalse();

        var (books, skipped) = GoodreadsCsvParser.Parse(text);

        books.Should().HaveCount(12);
        skipped.Should().ContainSingle().Which.Should().Match<ReadingHistorySkipped>(entry =>
            entry.Count == 1 && entry.Reason == ReadingHistoryCsv.EmptyReason);

        var cartographer = books.Single(book => book.Title == "The Cartographer's Daughter");
        cartographer.Isbn10.Should().Be("0000000019");
        cartographer.Isbn13.Should().Be("9780000000019");
        cartographer.Rating.Should().Be(5);
        cartographer.ReadStatus.Should().Be(ReadingHistoryStatus.Read);
        cartographer.FinishedAt.Should().Be(new DateTime(2021, 3, 14, 0, 0, 0, DateTimeKind.Utc));
        cartographer.Shelves.Should().Equal("fantasy", "favourites");
        // RFC 4180: commas, doubled quotes and the line break survive.
        cartographer.Review.Should().Contain("drawn from memory").And.Contain("\nand a mother");
        cartographer.PrivateNotes.Should().Be("Kept for the map on page 12.");

        // ISBN and ISBN13 are both `=""` on a Kindle edition.
        var winter = books.Single(book => book.Title == "A Winter in the Salt Marshes");
        winter.Isbn10.Should().BeNull();
        winter.Isbn13.Should().BeNull();
        winter.Rating.Should().Be(4);

        // An ISBN-10-only row still yields one identity.
        var kitchen = books.Single(book => book.Title == "Small Gods of the Kitchen");
        kitchen.Isbn10.Should().Be("0000000051");
        kitchen.Isbn13.Should().BeNull();

        books.Should().Contain(book => book.Title == "The Lantern Keepers" && book.ToRead);
        books.Should().Contain(book => book.Title == "Notes on a Quiet Sea"
            && book.ReadStatus == ReadingHistoryStatus.CurrentlyReading);
        books.Single(book => book.Title == "Nine Kinds of Silence").ReadCount.Should().Be(2);
        books.Should().Contain(book => book.Title == "The Unread Shelf" && book.ReadStatus == ReadingHistoryStatus.ToRead);

        // Quotes and a comma inside the title are not delimiters.
        books.Should().Contain(book => book.Title == "Dune, or: \"The Spice\" Must Flow");
    }

    [Fact]
    public void Storygraph_is_detected_by_its_header_and_keeps_quarter_stars_and_read_dates()
    {
        var text = File.ReadAllText(Storygraph);
        StorygraphCsvParser.Looks(text).Should().BeTrue();
        GoodreadsCsvParser.Looks(text).Should().BeFalse();

        var (books, skipped) = StorygraphCsvParser.Parse(text);

        books.Should().HaveCount(12);
        skipped.Should().ContainSingle().Which.Count.Should().Be(1);

        var cartographer = books.Single(book => book.Title == "The Cartographer's Daughter");
        cartographer.Isbn13.Should().Be("9780000000019");
        cartographer.Rating.Should().Be(5);
        cartographer.RatingNotice.Should().Be("4.75 stars is stored as 5.");
        cartographer.FinishedAt.Should().Be(new DateTime(2021, 3, 14, 0, 0, 0, DateTimeKind.Utc));
        cartographer.Shelves.Should().Equal("fantasy", "favourites");
        cartographer.Review.Should().Contain("\nand a mother");

        var silence = books.Single(book => book.Title == "Nine Kinds of Silence");
        silence.ReadCount.Should().Be(2);
        // Last Date Read names the last of two reads.
        silence.FinishedAt.Should().Be(new DateTime(2024, 2, 20, 0, 0, 0, DateTimeKind.Utc));

        var glass = books.Single(book => book.Title == "The Glass Orchard");
        (glass.Rating, glass.RatingNotice, glass.ReadStatus).Should().Be(
            (2, "2.25 stars is stored as 2.", ReadingHistoryStatus.DidNotFinish));

        // The identifier is StoryGraph's own, not an ISBN, so it cannot match one.
        var paper = books.Single(book => book.Title == "The Paper Meridian");
        paper.Isbn.Should().BeNull();
        paper.FinishedAt.Should().Be(new DateTime(2018, 9, 9, 0, 0, 0, DateTimeKind.Utc));

        var kitchen = books.Single(book => book.Title == "Small Gods of the Kitchen");
        kitchen.Isbn10.Should().Be("0000000051");
        kitchen.Isbn13.Should().BeNull();
        kitchen.Rating.Should().Be(4, "4.25 rounds to 4");

        // Last Date Read is empty; the end of Dates Read answers instead.
        var estuary = books.Single(book => book.Title == "The Long Estuary");
        estuary.FinishedAt.Should().Be(new DateTime(2020, 2, 10, 0, 0, 0, DateTimeKind.Utc));

        books.Should().Contain(book => book.Title == "A Winter in the Salt Marshes"
            && book.ReadStatus == ReadingHistoryStatus.Paused);
        books.Should().Contain(book => book.Title == "The Unread Shelf" && book.ToRead);
        books.Should().Contain(book => book.Title == "Dune, or: \"The Spice\" Must Flow");
    }

    [Fact]
    public void The_source_reader_decides_by_content_and_rejects_anything_else()
    {
        var goodreads = ReadingHistoryImportSourceReader.Read(Goodreads);
        goodreads.Source.Should().Be("goodreads");
        goodreads.Books.Should().HaveCount(12);

        var storygraph = ReadingHistoryImportSourceReader.Read(Storygraph);
        storygraph.Source.Should().Be("storygraph");
        storygraph.Books.Should().HaveCount(12);

        var other = Path.Combine(Path.GetTempPath(), $"reading-history-{Guid.NewGuid():N}.csv");
        File.WriteAllText(other, "Title,Author\nA Book,Someone\n");
        try
        {
            Action read = () => ReadingHistoryImportSourceReader.Read(other);
            read.Should().Throw<FormatException>()
                .WithMessage(ReadingHistoryImportSourceReader.Unrecognised);
        }
        finally
        {
            File.Delete(other);
        }
    }
}

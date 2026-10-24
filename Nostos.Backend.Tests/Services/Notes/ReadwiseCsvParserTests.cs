using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

public sealed class ReadwiseCsvParserTests
{
    [Fact]
    public void An_export_is_read_by_book_and_what_is_not_a_book_is_left_out_or_flagged()
    {
        var text = Fixture("readwise-export.csv");
        ReadwiseCsvParser.Looks(text).Should().BeTrue();

        var (books, skipped) = ReadwiseCsvParser.Parse(text);

        books.Select(book => (book.SourceKey, book.Title, book.Author)).Should().Equal(
            ("readwise:asin:B00SYNTH01", "Pride and Prejudice", "Jane Austen"),
            ("readwise:meta:WALDEN|HENRY DAVID THOREAU", "Walden", "Henry David Thoreau"),
            ("readwise:meta:HOW TO READ SLOWLY|EXAMPLE BLOG", "How to Read Slowly", "Example Blog"));

        // A quoted field keeps its commas, its doubled quotes and its line break.
        books[0].Annotations.Should().Equal(
            new ImportAnnotation("readwise_location", "5", "\"It is a truth\", she wrote,\nacross two lines.", "Opening line."),
            new ImportAnnotation("readwise_location", "120", "A second synthetic highlight.", ""),
            new ImportAnnotation("readwise_location", "130", null, "A note with no highlight."));
        books[0].Notice.Should().BeNull();

        books[1].Annotations.Single().Should().Be(
            new ImportAnnotation("readwise_page", "88", "I went to the woods because I wished to live deliberately", ""));

        // Positioned by order with no Amazon id: perhaps an article, so flagged.
        books[2].Notice.Should().Be(ReadwiseCsvParser.ArticleNotice);

        skipped.Should().BeEquivalentTo(new[]
        {
            new HighlightImportSkipped("Tweets From Ada Reader", "@adareader", 1, ReadwiseCsvParser.TweetReason),
            new HighlightImportSkipped("Episode 12: Reading", "The Reading Show", 1, ReadwiseCsvParser.PodcastReason),
            new HighlightImportSkipped("Walden", "Henry David Thoreau", 1, ReadwiseCsvParser.EmptyReason),
        });
    }

    [Fact]
    public void A_category_column_decides_what_is_a_book_and_columns_are_found_by_name()
    {
        const string csv =
            "Book Title,Category,Highlight,Location,Location Type,Book Author\n" +
            "A Novel,books,Line one.,12,location,Ada Reader\n" +
            "A Post,articles,Line two.,1,order,A Blog\n" +
            "A Show,podcasts,Line three.,30,time_offset,A Host\n" +
            "A Feed,rss,Line four.,1,order,A Site\n" +
            "A Scan,pdfs,Line five.,4,page,Grace Writer\n";

        var (books, skipped) = ReadwiseCsvParser.Parse(csv);

        books.Select(book => book.Title).Should().Equal("A Novel", "A Scan");
        books.Should().AllSatisfy(book => book.Notice.Should().BeNull());
        skipped.Select(entry => entry.Reason).Should().Equal(
            ReadwiseCsvParser.ArticleReason, ReadwiseCsvParser.PodcastReason, ReadwiseCsvParser.OtherReason);
    }

    [Fact]
    public void Records_handle_quotes_line_endings_and_an_unclosed_quote()
    {
        ReadwiseCsvParser.Records("a,\"b,c\",\"d\"\"e\"\r\n\"multi\nline\",x\n\"never closed")
            .Should().BeEquivalentTo(new[]
            {
                new List<string> { "a", "b,c", "d\"e" },
                new List<string> { "multi\nline", "x" },
                new List<string> { "never closed" },
            }, options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData("Highlight,Book Title,Book Author\n", true)]
    [InlineData("﻿\"Highlight\",\"Book Title\"\r\n", true)]
    [InlineData("Title,Author\n", false)]
    [InlineData("return {\n", false)]
    [InlineData("", false)]
    public void Only_a_readwise_header_is_taken_for_a_readwise_export(string text, bool expected) =>
        ReadwiseCsvParser.Looks(text).Should().Be(expected);

    [Fact]
    public void Short_rows_and_blank_lines_do_not_throw()
    {
        var (books, skipped) = ReadwiseCsvParser.Parse("Highlight,Book Title,Book Author\n\nOnly a highlight\n,,\n,Other,\nText,Book\n");

        books.Should().ContainSingle().Which.Title.Should().Be("Book");
        skipped.Select(entry => entry.Reason).Should().Equal(ReadwiseCsvParser.UntitledReason, ReadwiseCsvParser.EmptyReason);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "readwise", name));
}

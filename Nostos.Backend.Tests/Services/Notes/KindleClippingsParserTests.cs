using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

public sealed class KindleClippingsParserTests
{
    [Fact]
    public void English_clippings_fold_edits_and_notes_into_one_annotation_each()
    {
        var text = Fixture("My Clippings.txt");
        KindleClippingsParser.Looks(text).Should().BeTrue();

        var (books, skipped) = KindleClippingsParser.Parse(text);

        books.Select(book => (book.Title, book.Author)).Should().Equal(
            ("Pride and Prejudice", "Jane Austen"),
            ("Walden", "Henry David Thoreau"),
            ("Meditations (Penguin Classics)", "Marcus Aurelius"),
            ("Synthetic Field Guide", "Ada Reader, Grace Writer"));

        // The highlight was extended on the device: one annotation, the longer
        // text, with the note made at its last location attached.
        var austen = books[0].Annotations.Should().ContainSingle().Subject;
        austen.Text.Should().StartWith("It is a truth universally acknowledged, that a single man");
        austen.Note.Should().Be("The famous opening.");
        (austen.AnchorKind, austen.AnchorValue).Should().Be(("kindle_location", "5-7"));

        // A note with no highlight under it stands alone; its edit replaces it.
        books[1].Annotations.Should().HaveCount(2);
        books[1].Annotations[0].Should().Be(new ImportAnnotation("kindle_location", "120-121", "I went to the woods because I wished to live deliberately", ""));
        books[1].Annotations[1].Should().Be(new ImportAnnotation("kindle_location", "300", null, "A thought of my own, revised."));

        // A PDF has pages and no locations; line breaks inside a highlight stay.
        var meditations = books[2].Annotations.Should().ContainSingle().Subject;
        (meditations.AnchorKind, meditations.AnchorValue).Should().Be(("kindle_page", "12"));
        meditations.Text.Should().Be("Waste no more time arguing what a good man should be.\nBe one.");

        // Older firmware abbreviates the end of a range.
        books[3].Annotations.Single().AnchorValue.Should().Be("1180-1181");

        books.Select(book => book.SourceKey).Should().OnlyHaveUniqueItems()
            .And.AllSatisfy(key => key.Should().StartWith("kindle:"));

        skipped.Should().BeEquivalentTo(new[]
        {
            new HighlightImportSkipped("Pride and Prejudice", "Jane Austen", 1, KindleClippingsParser.BookmarkReason),
            new HighlightImportSkipped("Pride and Prejudice", "Jane Austen", 1, KindleClippingsParser.ClippingLimitReason),
            new HighlightImportSkipped("Walden", "Henry David Thoreau", 1, KindleClippingsParser.EmptyReason),
        });
    }

    [Fact]
    public void Other_ui_languages_are_read_and_an_unknown_one_is_left_out_with_a_reason()
    {
        var (books, skipped) = KindleClippingsParser.Parse(Fixture("My Clippings.languages.txt"));

        var byTitle = books.ToDictionary(book => book.Title!);
        byTitle.Keys.Should().BeEquivalentTo("Der Process", "Candide", "Don Quijote", "吾輩は猫である", "论语", "Truncated Book");

        var kafka = byTitle["Der Process"].Annotations.Should().ContainSingle().Subject;
        kafka.Should().Be(new ImportAnnotation("kindle_location", "95-96", "Jemand mußte Josef K. verleumdet haben", "Der Anfang."));
        byTitle["Der Process"].Author.Should().Be("Franz Kafka");

        byTitle["Candide"].Annotations.Single().AnchorValue.Should().Be("40-41");
        byTitle["Don Quijote"].Author.Should().Be("Miguel de Cervantes");
        byTitle["Don Quijote"].Annotations.Single().AnchorValue.Should().Be("10-11");
        byTitle["吾輩は猫である"].Annotations.Single().AnchorValue.Should().Be("12-13");
        // Page and location in one bracketed phrase: the location wins.
        byTitle["论语"].Annotations.Single().Should().Be(new ImportAnnotation("kindle_location", "30-31", "学而时习之", ""));
        // The last entry lost its separator; it is still an entry.
        byTitle["Truncated Book"].Annotations.Single().Text.Should().Be("A last entry the device never closed.");

        skipped.Should().BeEquivalentTo(new[]
        {
            new HighlightImportSkipped("Candide", "Voltaire", 1, KindleClippingsParser.BookmarkReason),
            new HighlightImportSkipped("Bilinmeyen Kitap", "Bir Yazar", 1, KindleClippingsParser.UnrecognisedReason),
            new HighlightImportSkipped("a stray line with no entry shape", null, 1, KindleClippingsParser.UnrecognisedReason),
        });
    }

    [Theory]
    [InlineData("Walden (Thoreau, Henry David)", "Walden", "Henry David Thoreau")]
    [InlineData("Walden", "Walden", null)]
    [InlineData("Meditations (Penguin Classics) (Marcus Aurelius)", "Meditations (Penguin Classics)", "Marcus Aurelius")]
    [InlineData("A Book (Reader, Ada; Writer, Grace)", "A Book", "Ada Reader, Grace Writer")]
    [InlineData("A Book (Ada Reader and Grace Writer)", "A Book", "Ada Reader and Grace Writer")]
    [InlineData("A Book (King, Jr.)", "A Book", "King, Jr.")]
    [InlineData("A Book (Organisation (Editor))", "A Book", "Organisation (Editor)")]
    [InlineData("﻿A Book ()", "A Book", null)]
    [InlineData("(Only Brackets)", "(Only Brackets)", null)]
    public void Title_lines_split_into_title_and_author(string line, string title, string? author) =>
        KindleClippingsParser.TitleAndAuthor(line).Should().Be((title, author));

    [Theory]
    [InlineData("180-181", 180, 181)]
    [InlineData("1180-81", 1180, 1181)]
    [InlineData("42", 42, 42)]
    [InlineData("1,204-1,206", 1204, 1206)]
    [InlineData("90-12", 90, 90)]
    public void Location_ranges_are_read_including_abbreviated_ends(string value, int start, int end) =>
        KindleClippingsParser.ParseRange(value).Should().Be((start, end));

    [Theory]
    [InlineData("")]
    [InlineData("==========")]
    [InlineData("\n\n==========\n==========\n")]
    [InlineData("Title only\n==========")]
    [InlineData("Title\n- Your Highlight on Location 99999999999999999999 | Added\n\ntext\n==========")]
    [InlineData("Title\nnot a meta line\n\ntext\n==========")]
    [InlineData("Title\n-\n\n\n==========")]
    [InlineData("Title\n- Your Note\n\nA note with no position\n==========\nTitle\n- Your Note\n\nAnother\n==========")]
    public void Malformed_input_never_throws(string text)
    {
        var act = () => KindleClippingsParser.Parse(text);
        act.Should().NotThrow();
    }

    [Fact]
    public void A_highlight_rewritten_at_the_same_range_keeps_the_latest_text()
    {
        const string text =
            "Book (Author)\n- Your Highlight on Location 10-12 | Added on Monday\n\nFirst wording.\n==========\n" +
            "Book (Author)\n- Your Highlight on Location 10-12 | Added on Tuesday\n\nSecond wording.\n==========\n" +
            "Book (Author)\n- Your Highlight on Location 50-52 | Added on Tuesday\n\nUnrelated passage.\n==========\n";

        var (books, _) = KindleClippingsParser.Parse(text);

        books.Single().Annotations.Select(annotation => annotation.Text)
            .Should().Equal("Second wording.", "Unrelated passage.");
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kindle", name));
}

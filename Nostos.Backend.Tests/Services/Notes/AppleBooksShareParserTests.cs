using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

public sealed class AppleBooksShareParserTests
{
    private const string Footer =
        "Excerpt From\nPride and Prejudice\nJane Austen\nThis material may be protected by copyright.\n";

    [Fact]
    public void A_shared_passage_is_read_with_the_book_its_footer_names()
    {
        var text = "“It is a truth universally acknowledged.”\n\n" + Footer;

        AppleBooksShareParser.Looks(text).Should().BeTrue();
        var (books, skipped) = AppleBooksShareParser.Parse(text);

        skipped.Should().BeEmpty();
        var book = books.Single();
        (book.Title, book.Author).Should().Be(("Pride and Prejudice", "Jane Austen"));
        book.SourceKey.Should().StartWith("applebooks:").And.NotBe(AppleBooksShareParser.UntitledKey);
        book.Annotations.Should().Equal(
            new ImportAnnotation(AppleBooksShareParser.PassageAnchor, null, "It is a truth universally acknowledged.", ""));
    }

    [Fact]
    public void Several_passages_share_one_footer_and_unquoted_text_is_the_note_before_it()
    {
        var text =
            "“First passage,\nacross two lines.”\n\n" +
            "My note on the first.\n\n" +
            "“Second passage.”\n" +
            "“Third passage.”\n\n" +
            Footer +
            "https://books.apple.com/us/book/pride-and-prejudice/id395534934\n";

        var (books, _) = AppleBooksShareParser.Parse(text);

        books.Single().Annotations.Select(annotation => (annotation.Text, annotation.Note)).Should().Equal(
            ("First passage,\nacross two lines.", "My note on the first."),
            ("Second passage.", ""),
            ("Third passage.", ""));
    }

    [Fact]
    public void Shares_from_two_books_pasted_together_stay_apart_and_a_repeat_is_one_highlight()
    {
        var text =
            "“Once.”\n\n" + Footer + "\n" +
            "“I went to the woods.”\n\nExcerpt From\nWalden\nHenry David Thoreau\nThis material may be protected by copyright.\n\n" +
            "“Once.”\n\n" + Footer;

        var (books, _) = AppleBooksShareParser.Parse(text);

        books.Select(book => book.Title).Should().Equal("Pride and Prejudice", "Walden");
        books[0].Annotations.Should().ContainSingle();
    }

    [Fact]
    public void The_older_one_line_footer_names_author_and_title()
    {
        var text = "“Nothing in life is to be feared.”\n\nExcerpt From: Marie Curie. “Pierre Curie.” iBooks.\n";

        var (books, _) = AppleBooksShareParser.Parse(text);

        (books.Single().Title, books.Single().Author).Should().Be(("Pierre Curie", "Marie Curie"));
    }

    [Fact]
    public void A_footer_in_another_language_is_read_and_other_quote_marks_count_as_quotes()
    {
        var text = "„Ein Satz.“\n\nAuszug aus\nDer Prozess\nFranz Kafka\nDieses Material ist möglicherweise urheberrechtlich geschützt.\n";

        var (books, _) = AppleBooksShareParser.Parse(text);

        (books.Single().Title, books.Single().Author).Should().Be(("Der Prozess", "Franz Kafka"));
        books.Single().Annotations.Single().Text.Should().Be("Ein Satz.");
    }

    [Fact]
    public void Passages_without_a_footer_come_in_as_one_untitled_book_for_the_preview_to_place()
    {
        var text = "“A passage whose footer was cut off.”\n\n“Another one.”\n";

        AppleBooksShareParser.Looks(text).Should().BeTrue();
        var (books, _) = AppleBooksShareParser.Parse(text);

        var book = books.Single();
        book.SourceKey.Should().Be(AppleBooksShareParser.UntitledKey);
        (book.Title, book.Author).Should().Be((null, null));
        book.Annotations.Should().HaveCount(2);
    }

    [Fact]
    public void Other_import_files_are_not_taken_for_a_share()
    {
        AppleBooksShareParser.Looks("return {\n  [\"doc_props\"] = {},\n}").Should().BeFalse();
        AppleBooksShareParser.Looks("Highlight,Book Title\nx,y\n").Should().BeFalse();
        AppleBooksShareParser.Looks("Some plain notes\nwith no quotes").Should().BeFalse();
    }

    [Fact]
    public void The_source_reader_sends_a_share_to_this_parser()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "“A passage.”\n\n" + Footer);
            var file = HighlightImportSourceReader.Read(path);
            file.Source.Should().Be(HighlightImportSourceReader.AppleBooks);
            file.Books.Single().Title.Should().Be("Pride and Prejudice");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

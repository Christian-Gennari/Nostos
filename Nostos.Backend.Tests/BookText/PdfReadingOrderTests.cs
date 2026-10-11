using FluentAssertions;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

public sealed class PdfReadingOrderTests
{
    private const double PageLeft = 0;
    private const double PageRight = 600;

    /// <summary>Lays a line of words out left to right from <paramref name="x"/>.</summary>
    private static IEnumerable<PlacedWord> Line(double x, double baseline, params string[] words)
    {
        foreach (var word in words)
        {
            var width = word.Length * 6.0;
            yield return new PlacedWord(word, x, x + width, baseline, 10);
            x += width + 4;
        }
    }

    [Fact]
    public void Words_drawn_out_of_order_are_read_line_by_line()
    {
        // What an OCR layer looks like: right words, wrong drawing order.
        var first = Line(50, 700, "implored", "him", "to", "bestow", "a", "trifle").ToList();
        var second = Line(50, 686, "no", "children", "asked", "him").ToList();
        var shuffled = new[] { second[2], first[1], first[3], second[0], first[0], first[5], second[1], first[2], first[4], second[3] };

        PdfReadingOrder.Compose(shuffled, PageLeft, PageRight)
            .Should().Be("implored him to bestow a trifle\nno children asked him");
    }

    [Fact]
    public void Lines_are_separated_so_words_do_not_run_together()
    {
        var words = Line(50, 700, "very", "well", "indeed.")
            .Concat(Line(50, 686, "There", "was", "nothing"))
            .ToList();

        PdfReadingOrder.Compose(words, PageLeft, PageRight)
            .Should().Be("very well indeed.\nThere was nothing");
    }

    [Fact]
    public void A_word_set_slightly_off_the_baseline_stays_on_its_line()
    {
        var words = new List<PlacedWord>
        {
            new("Scrooge", 50, 92, 700, 10),
            new("never", 96, 126, 702.5, 10),
            new("painted", 130, 172, 698.5, 10),
        };

        PdfReadingOrder.Compose(words, PageLeft, PageRight)
            .Should().Be("Scrooge never painted");
    }

    [Fact]
    public void Two_columns_are_read_one_after_the_other()
    {
        var words = new List<PlacedWord>();
        for (var line = 0; line < 20; line++)
        {
            var baseline = 700 - line * 14;
            words.AddRange(Line(40, baseline, $"left{line}a", $"left{line}b", $"left{line}c"));
            words.AddRange(Line(330, baseline, $"right{line}a", $"right{line}b", $"right{line}c"));
        }

        var lines = PdfReadingOrder.Compose(words, PageLeft, PageRight).Split('\n');

        lines.Should().HaveCount(40);
        lines[0].Should().Be("left0a left0b left0c");
        lines[19].Should().Be("left19a left19b left19c");
        lines[20].Should().Be("right0a right0b right0c");
        lines[39].Should().Be("right19a right19b right19c");
    }

    [Fact]
    public void A_heading_across_both_columns_does_not_hide_the_gutter()
    {
        var words = Line(40, 760, "A", "heading", "set", "across", "the", "whole", "width", "of", "the", "page", "above", "both", "columns").ToList();
        for (var line = 0; line < 20; line++)
        {
            var baseline = 700 - line * 14;
            words.AddRange(Line(40, baseline, $"left{line}a", $"left{line}b", $"left{line}c"));
            words.AddRange(Line(330, baseline, $"right{line}a", $"right{line}b", $"right{line}c"));
        }

        var text = PdfReadingOrder.Compose(words, PageLeft, PageRight);

        text.IndexOf("left19c", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("right0a", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_column_with_ragged_lines_is_not_split()
    {
        var words = new List<PlacedWord>();
        for (var line = 0; line < 20; line++)
        {
            words.AddRange(Line(
                40,
                700 - line * 14,
                $"w{line}a", "and", "some", "more", "words", "that", "run", "right", "across", "the", "middle", $"w{line}z"));
        }

        var lines = PdfReadingOrder.Compose(words, PageLeft, PageRight).Split('\n');

        lines.Should().HaveCount(20);
        lines[0].Should().StartWith("w0a and").And.EndWith("w0z");
    }

    [Fact]
    public void An_empty_page_is_empty_text()
    {
        PdfReadingOrder.Compose([], PageLeft, PageRight).Should().BeEmpty();
        PdfReadingOrder.Compose([new PlacedWord(" ", 1, 2, 3, 4)], PageLeft, PageRight).Should().BeEmpty();
    }
}

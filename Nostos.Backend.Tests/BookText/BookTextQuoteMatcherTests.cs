using FluentAssertions;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

/// <summary>Finding a passage by its first and last words (#1291).</summary>
public sealed class BookTextQuoteMatcherTests
{
    private const string Text =
        "It was late. “The time of the snow was long past,” she said, and the people of the valley waited. They never came back. Morning came.";

    private static IReadOnlyList<QuoteSpan> Find(string text, string start, string end, int max = 6000) =>
        QuoteMatcher.FindSpans(text, QuoteMatcher.Tokenize(start), QuoteMatcher.Tokenize(end), max);

    [Fact]
    public void Finds_the_passage_with_its_opening_quote_mark_and_closing_punctuation()
    {
        var span = Find(Text, "the time of the", "never came back").Should().ContainSingle().Subject;

        Text[span.Start..span.End].Should().Be(
            "“The time of the snow was long past,” she said, and the people of the valley waited. They never came back.");
        span.Score.Should().Be(1);
    }

    [Theory]
    [InlineData("The time, of the", "never came back")]
    [InlineData("the tyme of the", "never came back")]
    [InlineData("the time of the", "they never come back")]
    [InlineData("the time of the snow was long passed", "never came back")]
    public void Tolerates_what_dictation_gets_wrong(string start, string end)
    {
        Find(Text, start, end).Should().ContainSingle()
            .Which.Start.Should().Be(Text.IndexOf('“'));
    }

    [Fact]
    public void Needs_the_first_and_last_words_of_each_phrase()
    {
        Find(Text, "a time of the", "never came back").Should().BeEmpty();
        Find(Text, "the time of the", "never came home").Should().BeEmpty();
    }

    [Fact]
    public void A_short_phrase_must_match_exactly()
    {
        Find(Text, "it was", "came back").Should().ContainSingle();
        Find(Text, "it is", "came back").Should().BeEmpty();
    }

    [Fact]
    public void Folds_case_and_accents()
    {
        const string text = "Ich fühlte mich müde. Das Café war leer und kalt.";
        var span = Find(text, "das cafe", "leer und kalt").Should().ContainSingle().Subject;
        text[span.Start..span.End].Should().Be("Das Café war leer und kalt.");
    }

    [Fact]
    public void Stops_at_the_first_end_after_the_start_and_respects_the_length_cap()
    {
        const string text = "Start here now. One. End of it. Two. End of it.";
        var span = Find(text, "start here now", "end of it").Should().ContainSingle().Subject;
        text[span.Start..span.End].Should().Be("Start here now. One. End of it.");

        Find(text, "start here now", "end of it", max: 10).Should().BeEmpty();
    }

    [Fact]
    public void A_long_start_phrase_also_matches_a_word_late_but_scores_lower()
    {
        var spans = Find(Text, "the time of the snow", "never came back");
        spans.Should().HaveCount(2);
        spans.MaxBy(span => span.Score).Start.Should().Be(Text.IndexOf('“'));
    }

    [Fact]
    public void Each_start_gives_its_own_passage()
    {
        const string text = "The time of the snow. They never came back. The time of the rain. They never came back.";
        Find(text, "the time of the", "never came back").Should().HaveCount(2);
    }

    [Fact]
    public void Clean_collapses_whitespace_but_keeps_paragraphs()
    {
        QuoteMatcher.Clean("One  line\nwraps.\n\nNew   paragraph. ")
            .Should().Be("One line wraps.\n\nNew paragraph.");
    }

    [Theory]
    [InlineData("chapter 3", "Chapter III: The Snow", true)]
    [InlineData("3", "Chapter 3", true)]
    [InlineData("the snow", "Chapter III: The Snow", true)]
    [InlineData("chapter 4", "Chapter III: The Snow", false)]
    [InlineData("kapitel 2", "Kapitel II", true)]
    public void Chapter_hints_match_numbers_numerals_and_titles(string hint, string heading, bool expected)
    {
        QuoteMatcher.ChapterMatches([heading], hint).Should().Be(expected);
    }

    [Fact]
    public void Quote_refs_round_trip_and_reject_garbage()
    {
        var reference = new QuoteRef(Guid.NewGuid(), "abcdef012345", 4, 5, 10, 900);
        QuoteRef.TryParse(reference.ToString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(reference);

        QuoteRef.TryParse("q1-nope", out _).Should().BeFalse();
        QuoteRef.TryParse(reference.ToString().Replace("q1", "q2"), out _).Should().BeFalse();
        QuoteRef.TryParse(new QuoteRef(Guid.NewGuid(), "ab", 9, 3, 0, 1).ToString(), out _)
            .Should().BeFalse("the range runs backwards");
    }

    [Fact]
    public void Window_joins_the_overlap_of_a_split_block_once()
    {
        var tail = new string('x', 10) + " the overlapping sentence that both chunks hold.";
        var first = Chunk(0, "Opening words. " + tail);
        var second = Chunk(1, tail + " And then it continues.");
        var third = Chunk(2, "A separate block.");

        var window = QuoteWindow.Join([first, second, third]);

        window.Text.Should().Be("Opening words. " + tail + " And then it continues.\n\nA separate block.");
        window.Starts[1].Should().Be("Opening words. ".Length);
        window.ChunkAt(window.Text.IndexOf("continues", StringComparison.Ordinal)).Should().Be(1);
        window.ChunkAt(window.Text.IndexOf("separate", StringComparison.Ordinal)).Should().Be(2);
    }

    private static BookTextIndexedChunk Chunk(int ordinal, string text) =>
        new(Guid.NewGuid(), Guid.Empty, new string('a', 64), "v", BookTextSourceFormat.Epub, ordinal, text, [], []);
}

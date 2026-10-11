using Nostos.Product.BookText;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Alignment.Spike.Tests;

public sealed class PassageMatcherTests
{
    private const string Passage =
        "The patient traveller carried a golden lantern through the quiet orchard before sunrise.";

    private static PassageMatcher Matcher(params string[] blocks) =>
        new(new BookTextExtractedDocument(
            BookTextSourceFormat.Epub,
            blocks.Select((text, i) => new BookTextArtifactBlock(
                i,
                text,
                [],
                [
                    new BookTextSourceSegment(
                        0,
                        text.Length,
                        new EpubBookTextSourceLocator(
                            i,
                            $"chapter-{i}.xhtml",
                            StartTextOffset: 100,
                            EndTextOffset: 100 + text.Length))
                ])).ToArray()));

    private static PassageMatcher PdfMatcher(
        params string[] pages) =>
        new(new BookTextExtractedDocument(
            BookTextSourceFormat.Pdf,
            pages.Select((text, i) => new BookTextArtifactBlock(
                i,
                text,
                [],
                [
                    new BookTextSourceSegment(
                        0,
                        text.Length,
                        new PdfBookTextSourceLocator(
                            i + 3,
                            $"page-{i + 4}",
                            0,
                            text.Length))
                ])).ToArray()));

    [Fact]
    public void Preserves_source_offsets_despite_case_and_punctuation()
    {
        var match = Matcher(
            "Other prefatory material.",
            "First: " + Passage)
            .Locate(Passage.ToUpperInvariant(), new());

        Assert.True(match.Accepted);
        Assert.Equal(1, match.Start!.BlockOrder);

        var locator = Assert.IsType<EpubBookTextSourceLocator>(
            match.Start.Locator);

        Assert.Equal("chapter-1.xhtml", locator.ResourceHref);
        Assert.Equal(107, locator.StartTextOffset);

        Assert.Equal(
            "Other prefatory material.".Length + 1 + "First: ".Length,
            match.Start.GlobalTextOffset);
    }

    [Fact]
    public void Epub_regression_pins_both_global_and_resource_offsets()
    {
        const string prefix = "Prefatory material.";
        const string before = "Introduction: ";

        var match = Matcher(prefix, before + Passage)
            .Locate(Passage, new());

        Assert.True(match.Accepted);
        Assert.Equal(1, match.Score);

        var start = match.Start!;
        var end = match.End!;

        var startLocator =
            Assert.IsType<EpubBookTextSourceLocator>(start.Locator);

        var endLocator =
            Assert.IsType<EpubBookTextSourceLocator>(end.Locator);

        Assert.Equal(1, start.BlockOrder);
        Assert.Equal(1, end.BlockOrder);

        Assert.Equal(prefix.Length + 1 + before.Length,
            start.GlobalTextOffset);

        Assert.Equal(
            prefix.Length + 1 + before.Length + Passage.Length - 1,
            end.GlobalTextOffset);

        Assert.Equal(1, startLocator.SpineIndex);
        Assert.Equal("chapter-1.xhtml", startLocator.ResourceHref);
        Assert.Equal(100 + before.Length,
            startLocator.StartTextOffset);

        Assert.Equal(100 + before.Length + Passage.Length - 1,
            endLocator.EndTextOffset);

        Assert.Equal(Passage.ToLowerInvariant()
            .TrimEnd('.'), match.Passage);
    }

    [Fact]
    public void Tolerates_a_transcription_substitution() =>
        Assert.True(Matcher(Passage)
            .Locate(Passage.Replace("golden", "silver"), new())
            .Accepted);

    [Fact]
    public void Rejects_repeated_passages_without_picking_the_first() =>
        Assert.Equal("ambiguous",
            Matcher(Passage, Passage).Locate(Passage, new()).Reason);

    [Theory]
    [InlineData("")]
    [InlineData("a chapter title")]
    public void Rejects_insufficient_speech(string text) =>
        Assert.False(Matcher(Passage).Locate(text, new()).Accepted);

    [Fact]
    public void Rejects_an_unrelated_book() =>
        Assert.False(Matcher(Passage)
            .Locate(
                "Every sailor aboard the ship feared the storm approaching across the northern ocean.",
                new())
            .Accepted);

    [Fact]
    public void Finds_a_passage_across_extracted_block_boundaries()
    {
        var match = Matcher(
            "The patient traveller carried a golden lantern",
            "through the quiet orchard before sunrise.")
            .Locate(Passage, new());

        Assert.True(match.Accepted);
        Assert.Equal(0, match.Start!.BlockOrder);
        Assert.Equal(1, match.End!.BlockOrder);
    }

    [Fact]
    public void Rejects_near_identical_distant_alternatives() =>
        Assert.Equal("ambiguous",
            Matcher(Passage, Passage.Replace("golden", "silver"))
                .Locate(Passage, new()).Reason);

    [Fact]
    public void End_offset_uses_the_segment_containing_the_last_character()
    {
        var boundary =
            Passage.IndexOf("sunrise", StringComparison.Ordinal) + 3;

        var document = new BookTextExtractedDocument(
            BookTextSourceFormat.Epub,
            [
                new BookTextArtifactBlock(
                    0,
                    Passage,
                    [],
                    [
                        new BookTextSourceSegment(
                            0,
                            boundary,
                            new EpubBookTextSourceLocator(
                                0,
                                "first.xhtml",
                                StartTextOffset: 0,
                                EndTextOffset: boundary)),
                        new BookTextSourceSegment(
                            boundary,
                            Passage.Length - boundary,
                            new EpubBookTextSourceLocator(
                                1,
                                "second.xhtml",
                                StartTextOffset: 50,
                                EndTextOffset:
                                    50 + Passage.Length - boundary))
                    ])
            ]);

        var match = new PassageMatcher(document)
            .Locate(Passage, new());

        Assert.True(match.Accepted);

        var end = Assert.IsType<EpubBookTextSourceLocator>(
            match.End!.Locator);

        Assert.Equal("second.xhtml", end.ResourceHref);
        Assert.Equal(
            50 + Passage.Length - 1 - boundary,
            end.EndTextOffset);
    }

    [Fact]
    public void Pdf_match_returns_physical_page_and_page_relative_offsets()
    {
        const string prefix = "Other prefatory material.";
        const string before = "First: ";

        var match = PdfMatcher(prefix, before + Passage)
            .Locate(Passage, new());

        Assert.True(match.Accepted);

        var start =
            Assert.IsType<PdfBookTextSourceLocator>(
                match.Start!.Locator);

        var end =
            Assert.IsType<PdfBookTextSourceLocator>(
                match.End!.Locator);

        Assert.Equal(4, start.PageIndex);
        Assert.Equal("page-5", start.PageLabel);
        Assert.Equal(before.Length, start.StartTextOffset);

        Assert.Equal(4, end.PageIndex);
        Assert.Equal(
            before.Length + Passage.Length - 1,
            end.EndTextOffset);

        Assert.Equal(
            prefix.Length + 1 + before.Length,
            match.Start.GlobalTextOffset);

        Assert.Equal(
            prefix.Length + 1 + before.Length + Passage.Length - 1,
            match.End.GlobalTextOffset);
    }

    [Theory]
    [InlineData("remark-\nable")]
    [InlineData("remark- able")]
    public void Pdf_hyphenated_word_matches_spoken_word(
        string brokenWord)
    {
        var text =
            $"The patient traveller made a {brokenWord} discovery " +
            "while walking through the quiet orchard before sunrise.";

        const string spoken =
            "The patient traveller made a remarkable discovery " +
            "while walking through the quiet orchard before sunrise.";

        var match = PdfMatcher(text).Locate(spoken, new());

        Assert.True(match.Accepted);
        Assert.Equal(1, match.Score);
        Assert.Equal(0, match.Start!.GlobalTextOffset);

        var end = Assert.IsType<PdfBookTextSourceLocator>(
            match.End!.Locator);

        Assert.Equal(
            text.Length - 1,
            end.EndTextOffset);
    }

    [Fact]
    public void Pdf_hyphenation_preserves_end_of_joined_word()
    {
        const string text =
            "The remarkable- able patient travelled through the " +
            "quiet orchard before the golden sunrise.";

        const string spoken =
            "The remarkableable patient travelled through the " +
            "quiet orchard before the golden sunrise.";

        var match = PdfMatcher(text).Locate(spoken, new());

        Assert.True(match.Accepted);

        var end = Assert.IsType<PdfBookTextSourceLocator>(
            match.End!.Locator);

        Assert.Equal(text.Length - 1, end.EndTextOffset);
    }

    [Fact]
    public void Pdf_does_not_join_hyphen_followed_by_capital()
    {
        const string text =
            "The patient traveller met Remark- Able before he " +
            "walked through the quiet orchard at sunrise.";

        const string spoken =
            "The patient traveller met Remark Able before he " +
            "walked through the quiet orchard at sunrise.";

        var match = PdfMatcher(text).Locate(spoken, new());

        Assert.True(match.Accepted);
        Assert.Contains("remark able", match.Passage);
    }

    [Fact]
    public void Pdf_match_can_cross_pages_without_losing_provenance()
    {
        const string first =
            "The patient traveller carried a golden lantern";

        const string second =
            "through the quiet orchard before sunrise.";

        var match = PdfMatcher(first, second)
            .Locate(Passage, new());

        Assert.True(match.Accepted);

        var start =
            Assert.IsType<PdfBookTextSourceLocator>(
                match.Start!.Locator);

        var end =
            Assert.IsType<PdfBookTextSourceLocator>(
                match.End!.Locator);

        Assert.Equal(3, start.PageIndex);
        Assert.Equal(4, end.PageIndex);
        Assert.Equal(second.Length - 1, end.EndTextOffset);

        Assert.Equal(0, match.Start.GlobalTextOffset);
        Assert.Equal(
            first.Length + 1 + second.Length - 1,
            match.End.GlobalTextOffset);
    }

    [Fact]
    public void Pdf_end_locator_uses_the_containing_segment()
    {
        var boundary =
            Passage.IndexOf("sunrise", StringComparison.Ordinal) + 3;

        var document = new BookTextExtractedDocument(
            BookTextSourceFormat.Pdf,
            [
                new BookTextArtifactBlock(
                    0,
                    Passage,
                    [],
                    [
                        new BookTextSourceSegment(
                            0,
                            boundary,
                            new PdfBookTextSourceLocator(
                                5, "vi", 0, boundary)),
                        new BookTextSourceSegment(
                            boundary,
                            Passage.Length - boundary,
                            new PdfBookTextSourceLocator(
                                6, "vii", 50,
                                50 + Passage.Length - boundary))
                    ])
            ]);

        var match = new PassageMatcher(document)
            .Locate(Passage, new());

        Assert.True(match.Accepted);

        var end = Assert.IsType<PdfBookTextSourceLocator>(
            match.End!.Locator);

        Assert.Equal(6, end.PageIndex);
        Assert.Equal("vii", end.PageLabel);
        Assert.Equal(
            50 + Passage.Length - 1 - boundary,
            end.EndTextOffset);
    }
}

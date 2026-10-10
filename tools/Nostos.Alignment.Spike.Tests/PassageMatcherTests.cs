using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Alignment.Spike.Tests;

public sealed class PassageMatcherTests
{
    private const string Passage = "The patient traveller carried a golden lantern through the quiet orchard before sunrise.";
    private static PassageMatcher Matcher(params string[] blocks) => new(new BookTextExtractedDocument(BookTextSourceFormat.Epub,
        blocks.Select((text, i) => new BookTextArtifactBlock(i, text, [],
            [new BookTextSourceSegment(0, text.Length, new EpubBookTextSourceLocator(i, $"chapter-{i}.xhtml", StartTextOffset: 100, EndTextOffset: 100 + text.Length))])).ToArray()));

    [Fact]
    public void Preserves_source_offsets_despite_case_and_punctuation()
    {
        var match = Matcher("Other prefatory material.", "First: " + Passage).Locate(Passage.ToUpperInvariant(), new());
        Assert.True(match.Accepted);
        Assert.Equal(1, match.Start!.BlockOrder);
        var locator = Assert.IsType<EpubBookTextSourceLocator>(match.Start.Locator);
        Assert.Equal("chapter-1.xhtml", locator.ResourceHref);
        Assert.Equal(107, locator.StartTextOffset);
        Assert.Equal("Other prefatory material.".Length + 1 + "First: ".Length, match.Start.GlobalTextOffset);
    }

    [Fact]
    public void Tolerates_a_transcription_substitution() => Assert.True(Matcher(Passage).Locate(Passage.Replace("golden", "silver"), new()).Accepted);

    [Fact]
    public void Rejects_repeated_passages_without_picking_the_first() => Assert.Equal("ambiguous", Matcher(Passage, Passage).Locate(Passage, new()).Reason);

    [Theory]
    [InlineData("")]
    [InlineData("a chapter title")]
    public void Rejects_insufficient_speech(string text) => Assert.False(Matcher(Passage).Locate(text, new()).Accepted);

    [Fact]
    public void Rejects_an_unrelated_book() => Assert.False(Matcher(Passage).Locate("Every sailor aboard the ship feared the storm approaching across the northern ocean.", new()).Accepted);

    [Fact]
    public void Finds_a_passage_across_extracted_block_boundaries()
    {
        var match = Matcher("The patient traveller carried a golden lantern", "through the quiet orchard before sunrise.").Locate(Passage, new());
        Assert.True(match.Accepted);
        Assert.Equal(0, match.Start!.BlockOrder);
        Assert.Equal(1, match.End!.BlockOrder);
    }

    [Fact]
    public void Rejects_near_identical_distant_alternatives() => Assert.Equal("ambiguous", Matcher(Passage, Passage.Replace("golden", "silver")).Locate(Passage, new()).Reason);

    [Fact]
    public void End_offset_uses_the_segment_containing_the_last_character()
    {
        var boundary = Passage.IndexOf("sunrise", StringComparison.Ordinal) + 3;
        var document = new BookTextExtractedDocument(BookTextSourceFormat.Epub,
            [new BookTextArtifactBlock(0, Passage, [],
                [new BookTextSourceSegment(0, boundary, new EpubBookTextSourceLocator(0, "first.xhtml", StartTextOffset: 0, EndTextOffset: boundary)),
                 new BookTextSourceSegment(boundary, Passage.Length-boundary, new EpubBookTextSourceLocator(1, "second.xhtml", StartTextOffset: 50, EndTextOffset: 50+Passage.Length-boundary))])]);
        var match = new PassageMatcher(document).Locate(Passage, new());
        Assert.True(match.Accepted);
        var end = Assert.IsType<EpubBookTextSourceLocator>(match.End!.Locator);
        Assert.Equal("second.xhtml", end.ResourceHref);
        Assert.Equal(50 + Passage.Length - 1 - boundary, end.EndTextOffset);
    }
}

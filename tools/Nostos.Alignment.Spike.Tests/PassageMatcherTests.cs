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
    public void A_misread_word_costs_half_of_a_different_word()
    {
        // Three words as a scan's text layer gives them: l for i, m for rn.
        var scanned = Matcher(
            "The patlent traveller carrled a golden lantem through the quiet orchard before sunrise.");

        var match = scanned.Locate(Passage, new());

        Assert.True(match.Accepted);
        Assert.Equal(1 - 1.5 / 13, match.Score, 3);

        // Three different words in the same places are three whole errors.
        var different = Matcher(
            "The patient sailor dropped a golden anchor through the quiet orchard before sunrise.")
            .Locate(Passage, new());

        Assert.Equal(1 - 3d / 13, different.Score, 3);
    }

    [Fact]
    public void Short_words_are_never_near_misses()
    {
        // "then" and "than", "form" and "from" are different words.
        var match = Matcher(
            "He said then that the form was more than he could bear to sign that day.")
            .Locate(
                "He said than that the from was more then he could bear to sign that day.",
                new());

        Assert.Equal(1 - 3d / 16, match.Score, 3);
    }

    [Fact]
    public void A_unique_passage_is_accepted_a_little_below_the_usual_score()
    {
        // As measured: a British spelling, an interjection heard differently
        // and a dropped title leave the only place it can be at 0.84.
        var match = Matcher(
            "Mr. Utterson the lawyer was a man of a rugged countenance that was never lighted by a smile.",
            "Not a bit of it. The cheque was genuine. Tut-tut, said Mr. Utterson. " +
            "I see you feel as I do, said Mr. Enfield.",
            "It was a fine dry night; frost in the air; the streets as clean as a ballroom floor.")
            .Locate(
                "not a bit of it the check was genuine tot tot said utterson " +
                "i see you feel as i do said mr",
                new());

        Assert.True(match.Accepted);
        Assert.InRange(match.Score, 0.80, 0.85);
        Assert.Equal(1, match.Start!.BlockOrder);
    }

    [Fact]
    public void A_passage_below_the_usual_score_with_a_rival_is_declined()
    {
        var spoken = Passage
            .Replace("golden", "silver")
            .Replace("quiet", "noisy");

        // Alone, the passage is the only candidate.
        Assert.True(Matcher(Passage).Locate(spoken, new()).Accepted);

        // A second place that is nearly as close takes the certainty away.
        var rival = Passage
            .Replace("golden", "silver")
            .Replace("patient", "weary")
            .Replace("traveller", "pilgrim");

        var match = Matcher(Passage, rival).Locate(spoken, new());

        Assert.False(match.Accepted);
        Assert.Equal("weak_match", match.Reason);
    }

    [Fact]
    public void Pdf_running_heads_and_page_numbers_are_not_part_of_the_text()
    {
        var pages = new[]
        {
            "A CHRISTMAS CAROL 10 Scrooge never painted out old Marley's name above the warehouse door and there",
            "11 MARLEY'S GHOST it stood years afterwards the firm was known as Scrooge and Marley sometimes people",
            "A CHRISTMAS CAROL 12 new to the business called Scrooge Scrooge and sometimes Marley but he answered",
            "13 MARLEY'S GHOST to both names it was all the same to him oh but he was a tight fisted hand",
            "A CHRISTMAS CAROL 14 at the grindstone Scrooge a squeezing wrenching grasping scraping clutching",
            "15 MARLEY'S GHOST covetous old sinner hard and sharp as flint from which no steel had ever struck",
            "A CHRISTMAS CAROL 16 out generous fire secret and self contained and solitary as an oyster",
            "17 MARLEY'S GHOST the cold within him froze his old features nipped his pointed nose",
        };

        var matcher = PdfMatcher(pages);

        // A sentence that runs over a page turn is unbroken.
        var across = matcher.Locate(
            "old marley's name above the warehouse door and there it stood years afterwards " +
            "the firm was known as scrooge and marley",
            new());

        Assert.True(across.Accepted);
        Assert.Equal(1, across.Score);
        Assert.Equal(0, across.Start!.BlockOrder);
        Assert.Equal(1, across.End!.BlockOrder);

        // The reader sends the top of a page, head and number included.
        var opening = matcher.LocateOpening(pages[2]);

        Assert.True(opening.Accepted);
        Assert.Equal(1, opening.Score);
        Assert.Equal(2, opening.Start!.BlockOrder);
        Assert.Equal(
            pages[2].IndexOf("new to", StringComparison.Ordinal),
            Assert.IsType<PdfBookTextSourceLocator>(opening.Start.Locator).StartTextOffset);
    }

    [Fact]
    public void Pdf_pages_that_open_with_the_same_ordinary_word_keep_it()
    {
        var openings = new[]
        {
            "The clerk in the tank involuntarily applauded and poked the fire at once",
            "The owner of one scant young nose gnawed and mumbled by the hungry cold",
            "The fog and darkness thickened so that people ran about with flaring links",
            "The ancient tower of a church whose gruff old bell was always peeping slily",
            "The water plug being left in solitude its overflowings sullenly congealed",
            "The brightness of the shops where holly sprigs and berries crackled in the lamp heat",
            "The Lord Mayor in the stronghold of the mighty Mansion House gave orders",
            "The cold became intense in the main street at the corner of the court",
        };

        var match = PdfMatcher(openings).LocateOpening(openings[2]);

        Assert.True(match.Accepted);
        Assert.Equal(1, match.Score);
        Assert.Equal(2, match.Start!.BlockOrder);
        Assert.Equal(0, Assert.IsType<PdfBookTextSourceLocator>(match.Start.Locator).StartTextOffset);
    }

    [Fact]
    public void Chinese_is_matched_by_character_across_character_sets()
    {
        const string page =
            "我要給阿Q做正傳，已經不止一兩年了。但一面要做，一面又往回想，這足見我不是一個“立言”的人，" +
            "因為從來不朽之筆，須傳不朽之人，於是人以文傳，文以人傳——究竟誰靠誰傳，漸漸的不甚了然起來。";

        var matcher = Matcher("第一章 序", page, "第二章 優勝記略");

        // Simplified, unpunctuated, with two characters heard as homophones.
        var match = matcher.Locate(
            "但一面要做一面又往回想这足见我不是一个利言的人因为从来不朽之笔虚传不朽之人于是人以文传文以人传");

        Assert.True(match.Accepted);
        Assert.Equal(1, match.Start!.BlockOrder);
        Assert.Equal(page.IndexOf("但一面", StringComparison.Ordinal), match.Start.GlobalTextOffset - "第一章 序".Length - 1);

        Assert.Equal("insufficient_speech", matcher.Locate("我要给阿Q做正传").Reason);
    }

    [Fact]
    public void Ruby_glosses_are_left_out_and_offsets_stay_true()
    {
        const string text =
            "親譲（おやゆず）りの無鉄砲（むてっぽう）で小供の時から損ばかりしている。" +
            "小学校に居る時分学校の二階から飛び降りて一週間ほど腰（こし）を抜（ぬ）かした事がある。";

        var aids = new List<BookTextRange>();

        foreach (var gloss in new[] { "（", "おやゆず", "）", "（", "むてっぽう", "）", "（", "こし", "）", "（", "ぬ", "）" })
        {
            var from = aids.Count == 0 ? 0 : aids[^1].Start + aids[^1].Length;
            aids.Add(new BookTextRange(text.IndexOf(gloss, from, StringComparison.Ordinal), gloss.Length));
        }

        var document = new BookTextExtractedDocument(
            BookTextSourceFormat.Epub,
            [
                new BookTextArtifactBlock(
                    0,
                    text,
                    [],
                    [
                        new BookTextSourceSegment(
                            0,
                            text.Length,
                            new EpubBookTextSourceLocator(0, "c.xhtml", StartTextOffset: 0, EndTextOffset: text.Length)),
                    ]),
            ]);

        const string spoken =
            "親譲りの無鉄砲で小供の時から損ばかりしている小学校に居る時分学校の二階から飛び降りて一週間ほど腰を抜かした事がある";

        // With the glosses in the way the narration does not fit the page exactly.
        Assert.True(new PassageMatcher(document).Locate(spoken).Score < 1);

        var match = new PassageMatcher(
            document,
            new Dictionary<int, IReadOnlyList<BookTextRange>> { [0] = aids })
            .Locate(spoken);

        Assert.True(match.Accepted);
        Assert.Equal(1, match.Score);

        // The end is where the last spoken character really is in the page text.
        Assert.Equal(text.Length - 1, match.End!.GlobalTextOffset);
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

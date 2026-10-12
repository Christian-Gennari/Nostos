using FluentAssertions;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AudioAlignmentSearchTests
{
    private static readonly AlignmentOptions Options = new()
    {
        ProbeSeconds = 20,
        MaxProbes = 5,
        MatchToleranceCharacters = 40,
        ConsistencyToleranceCharacters = 40,
        LeadInSeconds = 1,
        MaximumLocalRateRatio = 5,
    };

    // Ten characters a second: one clip of twenty seconds is 200 characters.
    private static AudioAlignmentSearch Anchored(
        int target,
        AudioAlignmentSearch.Point? before,
        AudioAlignmentSearch.Point? after) =>
        new(target, totalCharacters: 10_000, durationSeconds: 1000, Options, before, after);

    [Fact]
    public void A_target_between_two_anchors_a_clip_apart_is_placed_between_them()
    {
        var search = Anchored(4050, new(400, 4000), new(412, 4100));

        search.AnchoredPositionSeconds.Should().BeApproximately(
            406 - Options.LeadInSeconds, 0.001);
    }

    [Fact]
    public void A_target_at_a_stored_anchor_is_that_anchors_second()
    {
        var search = Anchored(4000, new(400, 4000), new(400, 4000));

        search.AnchoredPositionSeconds.Should().BeApproximately(
            400 - Options.LeadInSeconds, 0.001);
    }

    [Fact]
    public void A_target_just_after_one_anchor_is_counted_on_from_it_to_land_early()
    {
        var search = Anchored(4100, new(400, 4000), after: null);

        // At the book's pace it is ten seconds on. Counted as fast narration
        // it is eight, so playback starts before the sentence, not after it.
        search.AnchoredPositionSeconds.Should().BeApproximately(
            408 - Options.LeadInSeconds, 0.001);
    }

    [Fact]
    public void A_target_just_before_one_anchor_is_counted_back_from_it_to_land_early()
    {
        var search = Anchored(3900, before: null, new(400, 4000));

        search.AnchoredPositionSeconds.Should().BeApproximately(
            387.5 - Options.LeadInSeconds, 0.001);
    }

    [Theory]
    [InlineData(3000, 4700)] // both more than a clip away
    [InlineData(3799, 4201)] // one character past a clip on either side
    public void Anchors_further_than_a_clip_from_the_target_do_not_answer(int beforeText, int afterText)
    {
        var search = Anchored(
            4000,
            new(beforeText / 10d, beforeText),
            new(afterText / 10d, afterText));

        search.AnchoredPositionSeconds.Should().BeNull();
        search.HasNextProbe.Should().BeTrue();
    }

    [Fact]
    public void No_anchors_do_not_answer()
    {
        Anchored(4000, null, null).AnchoredPositionSeconds.Should().BeNull();
    }

    [Fact]
    public void Anchors_that_contradict_each_other_do_not_answer()
    {
        // The earlier text is stored at the later second.
        var search = Anchored(4050, new(500, 4000), new(412, 4100));

        search.AnchoredPositionSeconds.Should().BeNull();
    }

    [Fact]
    public void A_count_that_would_pass_the_anchor_on_the_other_side_does_not_answer()
    {
        // 150 characters on from 400 s is 412 s at fast narration, but the next
        // stored anchor says the text 5000 characters later is spoken at 405 s.
        var search = Anchored(4150, new(400, 4000), new(405, 9000));

        search.AnchoredPositionSeconds.Should().BeNull();
    }

    [Fact]
    public void Constant_narration_rate_locates_target_in_one_probe()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 400,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        var result = Probe(search, t => 10 * t);

        result.Located.Should().BeTrue();
        result.Inconsistent.Should().BeFalse();
        result.PositionSeconds.Should().BeApproximately(
            39, 0.001);
    }

    [Fact]
    public void A_span_of_closing_words_is_timed_from_when_they_begin()
    {
        // The last eight seconds of a twenty-second clip hold the located text.
        var search = new AudioAlignmentSearch(
            targetOffset: 950,
            totalCharacters: 10_000,
            durationSeconds: 1000,
            Options);

        var result = search.Observe(new AudioAlignmentSearch.Observation(
            ClipStartSeconds: 80,
            ClipEndSeconds: 100,
            TextStartOffset: 900,
            TextEndOffset: 1000,
            Score: 1,
            Knots: [new(92, 900), new(100, 1000)]));

        // Halfway through the closing words is 96 s; spread over the whole
        // clip it would have been 90 s, six seconds before the sentence.
        result.Located.Should().BeTrue();
        result.PositionSeconds.Should().BeApproximately(
            96 - Options.LeadInSeconds, 0.001);
    }

    [Fact]
    public void A_target_between_two_placed_windows_is_timed_on_its_own_stretch()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 820,
            totalCharacters: 10_000,
            durationSeconds: 1000,
            Options);

        var result = search.Observe(new AudioAlignmentSearch.Observation(
            80,
            100,
            800,
            1000,
            1,
            [new(80, 800), new(88, 840), new(92, 900), new(100, 1000)]));

        result.Located.Should().BeTrue();
        result.PositionSeconds.Should().BeApproximately(
            84 - Options.LeadInSeconds, 0.001);
    }

    [Theory]
    [InlineData(92, 900, 91, 1000)]   // time runs backwards
    [InlineData(92, 950, 100, 940)]   // text runs backwards
    [InlineData(70, 900, 100, 1000)]  // before the clip
    [InlineData(92, 905, 100, 1000)]  // does not start at the span
    public void Pairs_that_do_not_fit_the_clip_fall_back_to_its_two_ends(
        double firstSeconds,
        double firstOffset,
        double secondSeconds,
        double secondOffset)
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 950,
            totalCharacters: 10_000,
            durationSeconds: 1000,
            Options);

        var result = search.Observe(new AudioAlignmentSearch.Observation(
            80,
            100,
            900,
            1000,
            1,
            [new(firstSeconds, firstOffset), new(secondSeconds, secondOffset)]));

        result.Located.Should().BeTrue();
        result.PositionSeconds.Should().BeApproximately(
            90 - Options.LeadInSeconds, 0.001);
    }

    [Fact]
    public void Nearby_anchor_initializes_search_at_correct_position()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 600,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options,
            before: new(50, 500),
            after: new(70, 700));

        search.EstimateSeconds.Should().BeApproximately(
            60, 0.001);

        var result = Probe(search, t => 10 * t);

        result.Located.Should().BeTrue();
        result.PositionSeconds.Should().BeApproximately(
            59, 0.001);
    }

    [Fact]
    public void Single_sided_anchor_extrapolates_at_average_rate()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 600,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options,
            before: new(50, 500));

        search.EstimateSeconds.Should().BeApproximately(
            60, 0.001);
    }

    [Fact]
    public void No_anchors_use_proportional_position()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 750,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        search.EstimateSeconds.Should().BeApproximately(
            75, 0.001);
    }

    [Fact]
    public void Changing_narration_rate_remains_searchable()
    {
        static double Narration(double time) =>
            time <= 50
                ? time * 10
                : 500 + (time - 50) * 5;

        var search = new AudioAlignmentSearch(
            targetOffset: 650,
            totalCharacters: 750,
            durationSeconds: 100,
            Options);

        var located = false;

        for (var i = 0; i < Options.MaxProbes; i++)
        {
            var result = Probe(search, Narration);

            result.Inconsistent.Should().BeFalse();

            if (result.Located)
            {
                result.PositionSeconds.Should().BeApproximately(
                    79, 1);
                located = true;
                break;
            }
        }

        located.Should().BeTrue();
    }

    [Fact]
    public void Long_non_text_intro_is_handled_by_declined_probes()
    {
        // First 100 seconds contain no book narration.
        // The book then progresses at 10 characters per second.
        static double Narration(double time) =>
            Math.Max(0, time - 100) * 10;

        var search = new AudioAlignmentSearch(
            targetOffset: 400,
            totalCharacters: 1000,
            durationSeconds: 200,
            Options);

        var declines = 0;
        var located = false;

        for (var i = 0; i < Options.MaxProbes; i++)
        {
            if (search.ClipEndSeconds <= 100)
            {
                search.Decline();
                declines++;
                continue;
            }

            var result = Probe(search, Narration);

            if (result.Located)
            {
                located = true;
                break;
            }
        }

        declines.Should().BeGreaterThan(0);
        located.Should().BeTrue();
    }

    [Fact]
    public void Declined_probe_nudges_forward_by_one_and_half_clips()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 400,
            totalCharacters: 1000,
            durationSeconds: 200,
            Options);

        search.EstimateSeconds.Should().Be(80);

        search.Decline().Should().Be(110);
        search.Decline().Should().Be(140);
    }

    [Fact]
    public void Bracket_prevents_estimate_leaving_known_interval()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 500,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options,
            before: new(40, 400),
            after: new(60, 600));

        search.EstimateSeconds.Should().Be(50);

        search.Decline().Should().Be(50);
    }

    [Fact]
    public void Contradictory_probes_are_rejected()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 900,
            totalCharacters: 1200,
            durationSeconds: 120,
            Options);

        var first = search.Observe(new(
            ClipStartSeconds: 30,
            ClipEndSeconds: 50,
            TextStartOffset: 400,
            TextEndOffset: 600,
            Score: 1));

        first.Inconsistent.Should().BeFalse();

        var second = search.Observe(new(
            ClipStartSeconds: 70,
            ClipEndSeconds: 90,
            TextStartOffset: 100,
            TextEndOffset: 300,
            Score: 1));

        second.Located.Should().BeFalse();
        second.Inconsistent.Should().BeTrue();
    }

    [Theory]
    [InlineData(90, 100, 300)]
    [InlineData(310, 100, 300)]
    public void Target_outside_passage_is_not_located_even_within_tolerance(
        int target,
        int start,
        int end)
    {
        var search = new AudioAlignmentSearch(
            targetOffset: target,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        var result = search.Observe(new(
            ClipStartSeconds: 0,
            ClipEndSeconds: 20,
            TextStartOffset: start,
            TextEndOffset: end,
            Score: 0.95));

        result.Located.Should().BeFalse();
        result.PositionSeconds.Should().BeNull();
        result.Inconsistent.Should().BeFalse();
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(300, 19)]
    public void Target_exactly_on_passage_boundary_is_located(
        int target,
        double expected)
    {
        var search = new AudioAlignmentSearch(
            targetOffset: target,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        var result = search.Observe(new(
            ClipStartSeconds: 0,
            ClipEndSeconds: 20,
            TextStartOffset: 100,
            TextEndOffset: 300,
            Score: 0.95));

        result.Located.Should().BeTrue();
        result.PositionSeconds.Should().Be(expected);
    }

    [Fact]
    public void Target_inside_passage_near_end_is_clamped_to_duration()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 990,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        var result = search.Observe(new(
            ClipStartSeconds: 90,
            ClipEndSeconds: 100,
            TextStartOffset: 900,
            TextEndOffset: 1000,
            Score: 1));

        result.Located.Should().BeTrue();
        result.PositionSeconds.Should().Be(98);
    }

    [Fact]
    public void Approximation_uses_bracket_midpoint()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 500,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options,
            before: new(40, 400),
            after: new(70, 700));

        search.ApproximatePositionSeconds.Should().Be(55);
    }

    [Fact]
    public void Probe_clip_is_clamped_at_audio_boundaries()
    {
        var beginning = new AudioAlignmentSearch(
            targetOffset: 0,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        beginning.ClipStartSeconds.Should().Be(0);
        beginning.ClipEndSeconds.Should().Be(10);

        var ending = new AudioAlignmentSearch(
            targetOffset: 1000,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        ending.ClipEndSeconds.Should().Be(100);
        ending.ClipStartSeconds.Should().Be(80);
    }

    [Fact]
    public void Repeated_estimate_inside_bracket_moves_half_a_probe()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 500,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options,
            before: new(20, 200),
            after: new(80, 800));

        search.EstimateSeconds.Should().Be(50);

        var first = search.Observe(new(
            ClipStartSeconds: 40,
            ClipEndSeconds: 60,
            TextStartOffset: 475,
            TextEndOffset: 495,
            Score: 1));

        first.Located.Should().BeFalse();
        first.Inconsistent.Should().BeFalse();
        search.HasNextProbe.Should().BeTrue();

        var firstCenter = 50d;
        var nextCenter =
            (search.ClipStartSeconds + search.ClipEndSeconds) / 2;

        Math.Abs(nextCenter - firstCenter)
            .Should().BeGreaterThan(2);

        Math.Abs(search.EstimateSeconds - 50)
            .Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void Exhausted_narrow_bracket_does_not_request_same_clip()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 500,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options,
            before: new(48, 480),
            after: new(52, 520));

        var result = search.Observe(new(
            ClipStartSeconds: 40,
            ClipEndSeconds: 60,
            TextStartOffset: 450,
            TextEndOffset: 490,
            Score: 1));

        result.Located.Should().BeFalse();
        result.Inconsistent.Should().BeFalse();

        search.HasNextProbe.Should().BeFalse();
        result.NextEstimateSeconds.Should().BeNull();

        search.ApproximatePositionSeconds.Should()
            .BeInRange(0, 100);
    }

    [Fact]
    public void Declined_probe_at_audio_boundary_stops_repeating()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 1000,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        search.ClipEndSeconds.Should().Be(100);

        search.Decline().Should().Be(100);
        search.HasNextProbe.Should().BeFalse();

        search.ApproximatePositionSeconds.Should().Be(100);
    }

    [Fact]
    public void Extreme_local_rate_falls_back_to_book_average()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 950,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options);

        var first = search.Observe(new(
            ClipStartSeconds: 0,
            ClipEndSeconds: 20,
            TextStartOffset: 100,
            TextEndOffset: 200,
            Score: 1));

        first.Located.Should().BeFalse();

        var second = search.Observe(new(
            ClipStartSeconds: 40,
            ClipEndSeconds: 60,
            TextStartOffset: 200,
            TextEndOffset: 300,
            Score: 1));

        second.Located.Should().BeFalse();
        second.Inconsistent.Should().BeFalse();

        double.IsFinite(search.EstimateSeconds)
            .Should().BeTrue();

        search.EstimateSeconds.Should().BeInRange(0, 100);
        search.ApproximatePositionSeconds.Should().NotBeNull();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_duration_is_rejected_before_any_probe(
        double duration)
    {
        Action construct = () => new AudioAlignmentSearch(
            targetOffset: 500,
            totalCharacters: 1000,
            durationSeconds: duration,
            Options);

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Very_large_finite_duration_never_produces_infinite_time()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 500,
            totalCharacters: 1000,
            durationSeconds: double.MaxValue,
            Options);

        double.IsFinite(search.EstimateSeconds).Should().BeTrue();

        var result = search.Observe(new(
            ClipStartSeconds: 0,
            ClipEndSeconds: 20,
            TextStartOffset: 100,
            TextEndOffset: 200,
            Score: 1));

        result.Located.Should().BeFalse();

        double.IsFinite(search.EstimateSeconds).Should().BeTrue();
        search.EstimateSeconds.Should().BeInRange(
            0, double.MaxValue);

        search.ApproximatePositionSeconds.Should().NotBeNull();
        double.IsFinite(
            search.ApproximatePositionSeconds!.Value)
            .Should().BeTrue();
    }

    [Fact]
    public void Invalid_anchor_coordinates_are_rejected()
    {
        Action construct = () => new AudioAlignmentSearch(
            targetOffset: 500,
            totalCharacters: 1000,
            durationSeconds: 100,
            Options,
            before: new(double.NaN, 300));

        construct.Should().Throw<ArgumentException>();
    }

    private static AudioAlignmentSearch.Decision Probe(
        AudioAlignmentSearch search,
        Func<double, double> narration)
    {
        return search.Observe(new AudioAlignmentSearch.Observation(
            search.ClipStartSeconds,
            search.ClipEndSeconds,
            (int)Math.Round(narration(search.ClipStartSeconds)),
            (int)Math.Round(narration(search.ClipEndSeconds)),
            1));
    }
}

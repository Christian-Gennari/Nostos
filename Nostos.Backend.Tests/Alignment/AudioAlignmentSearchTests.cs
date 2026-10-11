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

    [Fact]
    public void Target_inside_tolerance_is_located_and_clamped()
    {
        var search = new AudioAlignmentSearch(
            targetOffset: 90,
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
        result.PositionSeconds.Should().Be(0);
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

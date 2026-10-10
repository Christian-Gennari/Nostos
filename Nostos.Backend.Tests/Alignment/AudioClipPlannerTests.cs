using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AudioClipPlannerTests
{
    private static BookTrack Track(int number, long durationMs) =>
        new(
            Number: number,
            FileName: $"track-{number:D4}.mp3",
            ContentType: "audio/mpeg",
            DurationMs: durationMs,
            Bytes: 1024,
            Crc32: 0,
            Title: null);

    [Fact]
    public void Clip_inside_one_track_uses_only_that_track()
    {
        var tracks = new[]
        {
            Track(1, 60000),
            Track(2, 60000),
        };

        var result = AudioClipPlanner.Plan(tracks, 40, 20);

        result.Should().Equal(
            new AudioClipSegment(1, 20, 20));
    }

    [Fact]
    public void Clip_crossing_track_boundary_reads_both_tracks()
    {
        var tracks = new[]
        {
            Track(1, 60000),
            Track(2, 60000),
        };

        var result = AudioClipPlanner.Plan(tracks, 65, 20);

        result.Should().Equal(
            new AudioClipSegment(1, 45, 15),
            new AudioClipSegment(2, 0, 5));
    }

    [Fact]
    public void Ending_exactly_on_boundary_does_not_read_next_track()
    {
        var tracks = new[]
        {
            Track(1, 60000),
            Track(2, 60000),
        };

        var result = AudioClipPlanner.Plan(tracks, 60, 20);

        result.Should().Equal(
            new AudioClipSegment(1, 40, 20));
    }

    [Fact]
    public void Starting_exactly_on_boundary_does_not_read_previous_track()
    {
        var tracks = new[]
        {
            Track(1, 60000),
            Track(2, 60000),
        };

        var result = AudioClipPlanner.Plan(tracks, 80, 20);

        result.Should().Equal(
            new AudioClipSegment(2, 0, 20));
    }

    [Fact]
    public void Early_position_produces_shorter_clip()
    {
        var result = AudioClipPlanner.Plan(
            [Track(1, 60000)], 7, 20);

        result.Should().Equal(
            new AudioClipSegment(1, 0, 7));
    }

    [Fact]
    public void Clip_can_cross_multiple_tracks()
    {
        var tracks = new[]
        {
            Track(1, 5000),
            Track(2, 5000),
            Track(3, 5000),
        };

        var result = AudioClipPlanner.Plan(tracks, 14, 12);

        result.Should().Equal(
            new AudioClipSegment(1, 2, 3),
            new AudioClipSegment(2, 0, 5),
            new AudioClipSegment(3, 0, 4));
    }

    [Fact]
    public void Millisecond_durations_remain_precise()
    {
        var tracks = new[]
        {
            Track(1, 10500),
            Track(2, 10500),
        };

        var result = AudioClipPlanner.Plan(tracks, 12, 5);

        result.Should().Equal(
            new AudioClipSegment(1, 7, 3.5),
            new AudioClipSegment(2, 0, 1.5));
    }

    [Fact]
    public void Position_beyond_book_duration_is_rejected()
    {
        var tracks = new[]
        {
            Track(1, 10000),
            Track(2, 10000),
        };

        Action action = () =>
            AudioClipPlanner.Plan(tracks, 21, 20);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Invalid_positions_are_rejected(double position)
    {
        Action action = () =>
            AudioClipPlanner.Plan([Track(1, 60000)], position, 20);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}

using FluentAssertions;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AlignmentAudioDurationTests
{
    [Theory]
    [InlineData("1:20", 80)]
    [InlineData("01:02:03", 3723)]
    [InlineData("25:00:00", 90000)]
    [InlineData("100:15:30", 360930)]
    public void Parses_stored_duration(
        string duration,
        double expected)
    {
        AlignmentAudioDuration.ParseSeconds(duration)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("00:60:00")]
    [InlineData("1:90")]
    [InlineData("-1:00")]
    [InlineData("0:00")]
    [InlineData("NaN:00")]
    [InlineData("Infinity:00")]
    [InlineData("999999999999999999999999999999999:00:00")]
    public void Invalid_duration_is_unknown(string? duration)
    {
        AlignmentAudioDuration.ParseSeconds(duration)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0, 1, 5)]
    [InlineData(-10, -20, 1, 5)]
    [InlineData(1, 5, 1, 5)]
    [InlineData(5, 20, 5, 20)]
    [InlineData(8, 30, 8, 30)]
    [InlineData(20, 100, 8, 30)]
    [InlineData(int.MaxValue, double.PositiveInfinity, 8, 30)]
    public void Reverse_probe_options_are_bounded(
        int maxProbes,
        double probeSeconds,
        int expectedProbes,
        double expectedSeconds)
    {
        var options = new AlignmentOptions
        {
            MaxProbes = maxProbes,
            ProbeSeconds = probeSeconds,
        };

        options.NormalizeProbeSettings();

        options.MaxProbes.Should().Be(expectedProbes);
        options.ProbeSeconds.Should().Be(expectedSeconds);
    }

    [Fact]
    public void Non_finite_probe_setting_uses_safe_default()
    {
        var options = new AlignmentOptions
        {
            ProbeSeconds = double.NaN,
        };

        options.NormalizeProbeSettings();

        options.ProbeSeconds.Should().Be(20);
    }
}

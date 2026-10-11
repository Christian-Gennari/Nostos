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
    public void Invalid_duration_is_unknown(string? duration)
    {
        AlignmentAudioDuration.ParseSeconds(duration)
            .Should().BeNull();
    }
}

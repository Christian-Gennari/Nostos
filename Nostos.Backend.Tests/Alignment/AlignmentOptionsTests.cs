using System.Reflection;
using FluentAssertions;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AlignmentOptionsTests
{
    [Fact]
    public void CloneNormalizedForSearch_preserves_every_property_including_hedge_after_seconds()
    {
        var source = new AlignmentOptions
        {
            ClipSeconds = 25,
            ProbeSeconds = 18,
            MaxProbes = 7,
            MatchToleranceCharacters = 65,
            ConsistencyToleranceCharacters = 70,
            LeadInSeconds = 4.5,
            MaximumLocalRateRatio = 8.5,
            MaxEpubBytes = 123_456_789,
            MaxCachedCharacters = 9_876_543,
            HedgeAfterSeconds = 2.75,
        };

        var copy = source.CloneNormalizedForSearch();

        copy.Should().NotBeSameAs(source);
        copy.Should().BeEquivalentTo(source);

        // Guard against future properties being omitted if the clone implementation ever changes.
        foreach (var property in typeof(AlignmentOptions)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(p => p.CanRead && p.CanWrite))
        {
            property.GetValue(copy).Should().Be(
                property.GetValue(source),
                $"property {property.Name} should be preserved by CloneNormalizedForSearch");
        }
    }

    [Theory]
    [InlineData(0, 0, -1, 1, 5, 0)]
    [InlineData(20, 100, 50, 8, 30, 30)]
    [InlineData(5, double.NaN, double.NaN, 5, 20, 0)]
    [InlineData(3, 12, double.PositiveInfinity, 3, 12, 0)]
    [InlineData(99, double.PositiveInfinity, 100, 8, 30, 30)]
    public void CloneNormalizedForSearch_bounds_probe_and_lead_in_settings_without_mutating_source(
        int maxProbes,
        double probeSeconds,
        double leadInSeconds,
        int expectedMaxProbes,
        double expectedProbeSeconds,
        double expectedLeadInSeconds)
    {
        var source = new AlignmentOptions
        {
            MaxProbes = maxProbes,
            ProbeSeconds = probeSeconds,
            LeadInSeconds = leadInSeconds,
            HedgeAfterSeconds = 1.5,
        };

        var copy = source.CloneNormalizedForSearch();

        copy.MaxProbes.Should().Be(expectedMaxProbes);
        copy.ProbeSeconds.Should().Be(expectedProbeSeconds);
        copy.LeadInSeconds.Should().Be(expectedLeadInSeconds);
        copy.HedgeAfterSeconds.Should().Be(1.5);

        source.MaxProbes.Should().Be(maxProbes);
        source.ProbeSeconds.Should().Be(probeSeconds);
        source.LeadInSeconds.Should().Be(leadInSeconds);
    }
}

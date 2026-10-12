using FluentAssertions;
using Nostos.Backend.Data;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class UtcDateTimeTests
{
    private static readonly DateTime Noon = new(2026, 3, 14, 12, 0, 0);

    [Fact]
    public void Leaves_a_utc_time_alone()
    {
        var utc = DateTime.SpecifyKind(Noon, DateTimeKind.Utc);

        UtcDateTime.Normalize(utc).Should().Be(utc).And.BeIn(DateTimeKind.Utc);
    }

    [Fact]
    public void Takes_a_time_with_no_kind_to_already_be_utc()
    {
        var normalized = UtcDateTime.Normalize(DateTime.SpecifyKind(Noon, DateTimeKind.Unspecified));

        normalized.Kind.Should().Be(DateTimeKind.Utc);
        normalized.Ticks.Should().Be(Noon.Ticks);
    }

    [Fact]
    public void Converts_a_local_time()
    {
        var local = DateTime.SpecifyKind(Noon, DateTimeKind.Local);

        var normalized = UtcDateTime.Normalize(local);

        normalized.Kind.Should().Be(DateTimeKind.Utc);
        normalized.Should().Be(local.ToUniversalTime());
    }
}

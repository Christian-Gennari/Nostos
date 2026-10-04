using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Opt-in gate for the greater-than-4-GiB portable-archive measurement. The
/// ordinary suite reports the test as skipped; a local or release verification
/// run enables it with:
/// <code>NOSTOS_RUN_LARGE_PORTABILITY_TESTS=1</code>.
/// </summary>
public sealed class LargePortabilityFactAttribute : FactAttribute
{
    public LargePortabilityFactAttribute()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("NOSTOS_RUN_LARGE_PORTABILITY_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            Skip = "Set NOSTOS_RUN_LARGE_PORTABILITY_TESTS=1 to run >4 GiB portability measurements.";
        }
    }
}

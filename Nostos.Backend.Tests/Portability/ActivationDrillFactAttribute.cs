using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Opt-in gate for the real-process activation failure drills. Each drill
/// starts and hard-kills one or more actual <c>Nostos.Backend</c> processes on a
/// disposable data root, so the suite is deliberately not part of the default
/// fast backend run. Enable it with:
/// <code>NOSTOS_RUN_ACTIVATION_DRILLS=1 dotnet test --filter ActivationRealHostDrillTests</code>
/// </summary>
public sealed class ActivationDrillFactAttribute : FactAttribute
{
    public ActivationDrillFactAttribute()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("NOSTOS_RUN_ACTIVATION_DRILLS"),
            "1",
            StringComparison.Ordinal))
        {
            Skip = "Set NOSTOS_RUN_ACTIVATION_DRILLS=1 to run the real-host activation failure drills.";
        }
    }
}

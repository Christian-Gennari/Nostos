using Nostos.Backend.Services.Portability.Activation;

namespace Nostos.Backend.Composition;

/// <summary>
/// Activation failure drill gate (issue #681, Slice 11).
/// Off unless an operator running the real-host drill suite sets
/// <c>NOSTOS_ACTIVATION_DRILL_STOP_AT</c> to an activation/restore boundary name.
/// The named boundary publishes a marker file and parks the run until the release
/// file appears, so the harness can terminate the process at an exact boundary.
/// Without the environment variable the test seams stay null and behavior is unchanged.
/// </summary>
internal static class SelfHostedActivationDrillHook
{
    internal const string StopAtEnvironmentVariable = "NOSTOS_ACTIVATION_DRILL_STOP_AT";
    internal const string MarkerFileEnvironmentVariable = "NOSTOS_ACTIVATION_DRILL_MARKER_FILE";
    internal const string ReleaseFileEnvironmentVariable = "NOSTOS_ACTIVATION_DRILL_RELEASE_FILE";

    internal static void ConfigureIfEnabled(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ConfigureIfEnabled(app.Services, Environment.GetEnvironmentVariable);
    }

    internal static void ConfigureIfEnabled(
        IServiceProvider services,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var drillStopAt = getEnvironmentVariable(StopAtEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(drillStopAt))
            return;

        var drillMarkerPath = getEnvironmentVariable(MarkerFileEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(drillMarkerPath))
        {
            throw new InvalidOperationException(
                $"{StopAtEnvironmentVariable} requires {MarkerFileEnvironmentVariable}.");
        }

        var drillReleasePath = getEnvironmentVariable(ReleaseFileEnvironmentVariable);
        void DrillGate(string step)
        {
            if (!string.Equals(step, drillStopAt, StringComparison.Ordinal)) return;
            File.WriteAllText(drillMarkerPath, step);
            if (string.IsNullOrWhiteSpace(drillReleasePath))
            {
                Thread.Sleep(Timeout.Infinite);
                return;
            }

            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (!File.Exists(drillReleasePath))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The activation drill gate was not released.");
                }

                Thread.Sleep(20);
            }
        }

        var drillDispatcher = services.GetRequiredService<SelfHostedActivationDispatcher>();
        drillDispatcher.CoordinatorCreatedForTesting = coordinator => coordinator.StepObserverForTesting = DrillGate;
        drillDispatcher.RestoreStepObserverForTesting = DrillGate;
    }
}

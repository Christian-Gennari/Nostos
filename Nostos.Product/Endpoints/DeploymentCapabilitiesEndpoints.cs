using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Server-authoritative runtime capabilities used by the shared frontend to
/// adapt to SelfHosted vs Cloud without separate Angular builds or hostname
/// guesses.
///
/// This endpoint intentionally returns Nostos product capabilities rather than
/// database/provider/vendor details.
/// </summary>
public static class DeploymentCapabilitiesEndpoints
{
    public const string Route = "/api/runtime/capabilities";

    /// <summary>
    /// Single switch for the frontend-facing library-migration capability. It is
    /// deliberately decoupled from <see cref="IMigrationPhaseAvailability"/>:
    /// the advertisement is an explicit product decision, not inference from the
    /// currently wired phase handlers, and flipping this to <c>true</c> is the
    /// one change that advertises the feature to the UI. The SelfHosted frontend
    /// transport and activation (#680, #681) passed their real-browser
    /// acceptance gate, so the switch is on for this host; a hosted (Cloud) host
    /// flips it when its private adapter ships (orchestrator-owned).
    /// </summary>
    public const bool AdvertiseLibraryMigration = true;

    public static IEndpointRouteBuilder MapDeploymentCapabilitiesEndpoints(
        this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, (DeploymentDescriptor deployment, IServiceProvider services) =>
            Results.Ok(ToResponse(deployment, services.GetService<IMigrationPhaseAvailability>())))
            .AllowAnonymous();
        return routes;
    }

    public static DeploymentCapabilitiesResponse ToResponse(
        DeploymentDescriptor deployment,
        IMigrationPhaseAvailability? migrationAvailability = null) =>
        new(
            DeploymentMode: deployment.Mode.ToString(),
            RequiresAuthentication: deployment.Capabilities.RequiresAuthentication,
            CanConfigureAiProvider: deployment.Capabilities.CanConfigureAiProvider,
            ManagedAi: deployment.Capabilities.ManagedAi,
            ManagedVoiceTranscription: deployment.Capabilities.ManagedVoiceTranscription,
            UsesCloudStorage: deployment.Capabilities.UsesCloudStorage,
            SupportsLocalBackupConfiguration: deployment.Capabilities.SupportsLocalBackupConfiguration,
            SupportsPrivateNetworkAccess: deployment.Capabilities.SupportsPrivateNetworkAccess,
            SupportsEreaderAccess: deployment.Capabilities.SupportsEreaderAccess,
            UsageMeteringAvailable: deployment.Capabilities.UsageMeteringAvailable,
            AccountManagementUrl: deployment.Capabilities.AccountManagementUrl,
            FeedbackUrl: deployment.Capabilities.FeedbackUrl,
            // Library migration is advertised only when the operator/frontend
            // transport is ready (see AdvertiseLibraryMigration). Phase
            // availability still gates preflight and job creation server-side;
            // it is intentionally not what the UI reads. Safe activation (#681)
            // exists only in the SelfHosted host; the destructive confirmation
            // stays gated by the frontend capability below.
            SupportsLibraryMigration: AdvertiseLibraryMigration,
            SupportsSafeActivation: deployment.Mode == DeploymentMode.SelfHosted);
}

public sealed record DeploymentCapabilitiesResponse(
    string DeploymentMode,
    bool RequiresAuthentication,
    bool CanConfigureAiProvider,
    bool ManagedAi,
    bool ManagedVoiceTranscription,
    bool UsesCloudStorage,
    bool SupportsLocalBackupConfiguration,
    bool SupportsPrivateNetworkAccess,
    bool SupportsEreaderAccess,
    bool UsageMeteringAvailable,
    string? AccountManagementUrl,
    string? FeedbackUrl,
    bool SupportsLibraryMigration = false,
    bool SupportsSafeActivation = false);

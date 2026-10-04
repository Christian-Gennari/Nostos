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
            // Library migration is advertised only while this host can actually
            // finish a job for at least one direction; a missing phase handler
            // also makes job creation refuse up front. Safe activation (#681)
            // is not available yet on any host.
            SupportsLibraryMigration: migrationAvailability is not null
                && (migrationAvailability.IsAvailable(MigrationDirection.Import)
                    || migrationAvailability.IsAvailable(MigrationDirection.Export)),
            SupportsSafeActivation: false);
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

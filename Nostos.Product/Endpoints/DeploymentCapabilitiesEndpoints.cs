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
            // Library migration is advertised from the SAME availability the
            // migration routes consult, so "the UI should offer this feature"
            // can never be true while the host would refuse to execute it. The
            // availability already folds in the operator's
            // LibraryMigration:Enabled switch and the registered phase
            // handlers; a host that does not map the migration endpoints
            // registers no availability and stays dark.
            SupportsLibraryMigration: deployment.Mode == DeploymentMode.SelfHosted
                && migrationAvailability is not null
                && migrationAvailability.IsAvailable(MigrationDirection.Import)
                && migrationAvailability.IsAvailable(MigrationDirection.Export),
            SupportsSafeActivation: deployment.Mode == DeploymentMode.SelfHosted,
            HostedBrowserIntegrationEnabled: deployment.Mode == DeploymentMode.Cloud
                && deployment.Capabilities.HostedBrowserIntegrationEnabled,
            SupportsManagedBackups: deployment.Mode == DeploymentMode.Cloud
                && deployment.Capabilities.SupportsManagedBackups);
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
    bool SupportsSafeActivation = false,
    bool HostedBrowserIntegrationEnabled = false,
    bool SupportsManagedBackups = false);

using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Services.Library.Imports;
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
            Results.Ok(ToResponse(
                deployment,
                services.GetService<IMigrationPhaseAvailability>(),
                services.GetService<IExternalMediaAvailability>(),
                services.GetService<IBookFileUploadStore>(),
                services.GetService<IServiceProviderIsService>()?.IsService(typeof(IMcpConnectedApps)) == true,
                services.GetService<ICalibreLibraryAvailability>(),
                services.GetService<ICalibreUploadAvailability>())))
            .AllowAnonymous();
        return routes;
    }

    public static DeploymentCapabilitiesResponse ToResponse(
        DeploymentDescriptor deployment,
        IMigrationPhaseAvailability? migrationAvailability = null,
        IExternalMediaAvailability? externalMedia = null,
        IBookFileUploadStore? bookFileUploadStore = null,
        bool mcpConnectedApps = false,
        ICalibreLibraryAvailability? calibre = null,
        ICalibreUploadAvailability? calibreUpload = null) =>
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
                && deployment.Capabilities.SupportsManagedBackups,
            SupportsAccountDeletion: deployment.Mode == DeploymentMode.Cloud
                && deployment.Capabilities.SupportsAccountDeletion,
            // Restore is offered only by a host that also lists backups and
            // supplies a restore adapter; listing alone never shows the button.
            SupportsManagedBackupRestore: deployment.Mode == DeploymentMode.Cloud
                && deployment.Capabilities.SupportsManagedBackups
                && deployment.Capabilities.SupportsManagedBackupRestore,
            // Fails closed twice over: only a SelfHosted host, and only one
            // that registers the feature. Whether a folder is configured yet
            // is the feature's own status, so Settings can show how to set
            // one up instead of hiding that the feature exists.
            SupportsExternalMediaLibraries: deployment.Mode == DeploymentMode.SelfHosted
                && externalMedia is not null,
            // A Calibre library the host can read (#1527). Same two locks: a
            // SelfHosted host, and one that registered a source. Cloud stays
            // dark until a source that accepts an uploaded library exists.
            SupportsCalibreImport: deployment.Mode == DeploymentMode.SelfHosted
                && calibre is not null,
            // The uploaded-zip path (#1572) is advertised by the same
            // registration the upload routes require. It is not tied to the
            // deployment mode: SelfHosted offers it beside its folder, and a
            // hosted deployment offers it instead of one.
            SupportsCalibreUpload: calibreUpload is not null,
            // Same registration the upload routes require: a host that maps the
            // resumable contract registers this store, and a host that does not
            // stays on the legacy whole-request upload.
            SupportsResumableBookUploads: bookFileUploadStore is not null,
            // Settings → Connected apps (#1469): any host that keeps a record
            // of the apps connected over MCP (SelfHosted tokens, Cloud OAuth
            // clients). The same registration maps the list/revoke routes.
            SupportsMcpConnectedApps: mcpConnectedApps);
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
    bool SupportsManagedBackups = false,
    bool SupportsAccountDeletion = false,
    bool SupportsManagedBackupRestore = false,
    bool SupportsExternalMediaLibraries = false,
    bool SupportsResumableBookUploads = false,
    bool SupportsMcpConnectedApps = false,
    /// <summary>
    /// A SelfHosted server that can read a Calibre library folder (#1527),
    /// whether or not one is set up yet. Absent/false keeps the folder path
    /// hidden; never inferred from <see cref="DeploymentMode"/>.
    /// </summary>
    bool SupportsCalibreImport = false,
    /// <summary>
    /// A server that accepts a zipped Calibre library through the resumable
    /// upload (#1572). SelfHosted offers it beside the folder; Cloud offers it
    /// instead of one. Absent/false keeps the upload path hidden.
    /// </summary>
    bool SupportsCalibreUpload = false);

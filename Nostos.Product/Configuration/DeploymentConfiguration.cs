namespace Nostos.Backend.Configuration;

/// <summary>
/// The deployment shape of the running Nostos server.
///
/// This is deliberately a small closed set. Product/domain code should depend
/// on explicit capabilities or infrastructure abstractions rather than parsing
/// configuration strings or checking host names.
/// </summary>
public enum DeploymentMode
{
    SelfHosted,
    Cloud,
}

/// <summary>
/// Product-visible differences between deployment modes.
///
/// These are stable Nostos capabilities, not infrastructure vendor names.
/// </summary>
public sealed record DeploymentCapabilities(
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
    bool HostedBrowserIntegrationEnabled = false,
    bool SupportsManagedBackups = false,
    bool SupportsAccountDeletion = false);

/// <summary>
/// Server-authoritative deployment descriptor resolved once during startup.
/// </summary>
public sealed record DeploymentDescriptor(
    DeploymentMode Mode,
    DeploymentCapabilities Capabilities)
{
    public const string ConfigurationKey = "Nostos:DeploymentMode";
    public const string AccountManagementUrlConfigurationKey = "Nostos:AccountManagementUrl";
    public const string HostedBrowserIntegrationConfigurationKey = "Nostos:HostedBrowserIntegrationEnabled";
    public const string DefaultCloudAccountManagementUrl = "https://nostos.page/account";
    public const string DefaultCloudFeedbackUrl = "https://nostos.page/feedback?from=settings";

    public static DeploymentDescriptor FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(configured))
            return For(DeploymentMode.SelfHosted);

        if (!Enum.TryParse<DeploymentMode>(configured.Trim(), ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode))
        {
            throw new InvalidOperationException(
                $"Invalid '{ConfigurationKey}' value '{configured}'. " +
                $"Supported values are '{DeploymentMode.SelfHosted}' and '{DeploymentMode.Cloud}'.");
        }

        var deployment = For(mode);
        if (mode != DeploymentMode.Cloud)
            return deployment;

        deployment = deployment with
        {
            Capabilities = deployment.Capabilities with
            {
                HostedBrowserIntegrationEnabled =
                    configuration.GetValue<bool>(HostedBrowserIntegrationConfigurationKey),
            },
        };

        var configuredAccountManagementUrl = configuration[AccountManagementUrlConfigurationKey];
        if (string.IsNullOrWhiteSpace(configuredAccountManagementUrl))
            return deployment;

        var trimmedAccountManagementUrl = configuredAccountManagementUrl.Trim();
        if (!Uri.TryCreate(trimmedAccountManagementUrl, UriKind.Absolute, out var accountManagementUri)
            || (accountManagementUri.Scheme != Uri.UriSchemeHttps
                && accountManagementUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"Invalid '{AccountManagementUrlConfigurationKey}' value '{configuredAccountManagementUrl}'. " +
                "Expected an absolute http(s) URL.");
        }

        return deployment with
        {
            Capabilities = deployment.Capabilities with
            {
                AccountManagementUrl = trimmedAccountManagementUrl,
            },
        };
    }

    public static DeploymentDescriptor For(DeploymentMode mode) =>
        mode switch
        {
            DeploymentMode.SelfHosted => new(
                mode,
                new DeploymentCapabilities(
                    RequiresAuthentication: false,
                    CanConfigureAiProvider: true,
                    ManagedAi: false,
                    ManagedVoiceTranscription: false,
                    UsesCloudStorage: false,
                    SupportsLocalBackupConfiguration: true,
                    SupportsPrivateNetworkAccess: true,
                    SupportsEreaderAccess: true,
                    UsageMeteringAvailable: false,
                    AccountManagementUrl: null,
                    FeedbackUrl: null)),

            DeploymentMode.Cloud => new(
                mode,
                new DeploymentCapabilities(
                    RequiresAuthentication: true,
                    CanConfigureAiProvider: false,
                    ManagedAi: true,
                    ManagedVoiceTranscription: true,
                    UsesCloudStorage: true,
                    SupportsLocalBackupConfiguration: false,
                    SupportsPrivateNetworkAccess: false,
                    SupportsEreaderAccess: true,
                    UsageMeteringAvailable: true,
                    AccountManagementUrl: DefaultCloudAccountManagementUrl,
                    FeedbackUrl: DefaultCloudFeedbackUrl)),

            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Nostos deployment mode."),
        };
}

public static class DeploymentServiceRegistration
{
    public static DeploymentDescriptor AddNostosDeployment(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var deployment = DeploymentDescriptor.FromConfiguration(configuration);
        services.AddSingleton(deployment);
        return deployment;
    }
}

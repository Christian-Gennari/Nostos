using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Endpoints;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

public sealed class DeploymentConfigurationTests
{
    [Fact]
    public void Missing_configuration_defaults_to_self_hosted()
    {
        var configuration = new ConfigurationBuilder().Build();

        var deployment = DeploymentDescriptor.FromConfiguration(configuration);

        deployment.Mode.Should().Be(DeploymentMode.SelfHosted);
        deployment.Capabilities.Should().Be(new DeploymentCapabilities(
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
            FeedbackUrl: null));
    }

    [Fact]
    public void Cloud_configuration_is_case_insensitive_and_has_cloud_contract()
    {
        var configuration = BuildConfiguration("cloud");

        var deployment = DeploymentDescriptor.FromConfiguration(configuration);

        deployment.Mode.Should().Be(DeploymentMode.Cloud);
        deployment.Capabilities.Should().Be(new DeploymentCapabilities(
            RequiresAuthentication: true,
            CanConfigureAiProvider: false,
            ManagedAi: true,
            ManagedVoiceTranscription: true,
            UsesCloudStorage: true,
            SupportsLocalBackupConfiguration: false,
            SupportsPrivateNetworkAccess: false,
            SupportsEreaderAccess: true,
            UsageMeteringAvailable: true,
            AccountManagementUrl: DeploymentDescriptor.DefaultCloudAccountManagementUrl,
            FeedbackUrl: DeploymentDescriptor.DefaultCloudFeedbackUrl));
    }

    [Fact]
    public void Invalid_mode_fails_with_actionable_configuration_error()
    {
        var configuration = BuildConfiguration("Hosted");

        var act = () => DeploymentDescriptor.FromConfiguration(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Nostos:DeploymentMode*Hosted*SelfHosted*Cloud*");
    }

    [Fact]
    public void Public_host_persistence_registration_uses_local_sqlite()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var act = () => services.AddNostosPersistence(
            configuration,
            Path.Combine(Path.GetTempPath(), "nostos-deployment-test"));

        act.Should().NotThrow();
        services.Should().Contain(service =>
            service.ServiceType.Name == "IDbContextFactory`1");
    }

    [Fact]
    public void Capability_response_contains_only_product_runtime_contract()
    {
        var deployment = DeploymentDescriptor.For(DeploymentMode.Cloud);

        var response = DeploymentCapabilitiesEndpoints.ToResponse(deployment);

        response.DeploymentMode.Should().Be("Cloud");
        response.RequiresAuthentication.Should().BeTrue();
        response.CanConfigureAiProvider.Should().BeFalse();
        response.ManagedAi.Should().BeTrue();
        response.ManagedVoiceTranscription.Should().BeTrue();
        response.UsesCloudStorage.Should().BeTrue();
        response.SupportsLocalBackupConfiguration.Should().BeFalse();
        response.SupportsPrivateNetworkAccess.Should().BeFalse();
        response.SupportsEreaderAccess.Should().BeTrue();
        response.UsageMeteringAvailable.Should().BeTrue();
        response.AccountManagementUrl.Should().Be(DeploymentDescriptor.DefaultCloudAccountManagementUrl);
        response.FeedbackUrl.Should().Be(DeploymentDescriptor.DefaultCloudFeedbackUrl);
        response.SupportsManagedBackups.Should().BeFalse();
    }

    [Fact]
    public void Managed_backups_require_cloud_host_opt_in_and_cannot_be_advertised_by_selfhosted()
    {
        var cloud = DeploymentDescriptor.For(DeploymentMode.Cloud) with
        {
            Capabilities = DeploymentDescriptor.For(DeploymentMode.Cloud).Capabilities with
            {
                SupportsManagedBackups = true,
            },
        };
        var selfHosted = DeploymentDescriptor.For(DeploymentMode.SelfHosted) with
        {
            Capabilities = DeploymentDescriptor.For(DeploymentMode.SelfHosted).Capabilities with
            {
                SupportsManagedBackups = true,
            },
        };

        DeploymentCapabilitiesEndpoints.ToResponse(cloud).SupportsManagedBackups.Should().BeTrue();
        DeploymentCapabilitiesEndpoints.ToResponse(selfHosted).SupportsManagedBackups.Should().BeFalse();
    }

    [Fact]
    public void Cloud_account_management_url_can_be_overridden_without_affecting_self_hosted()
    {
        const string configuredUrl = "https://accounts.example.test/manage";

        var cloud = DeploymentDescriptor.FromConfiguration(
            BuildConfiguration("Cloud", configuredUrl));
        var selfHosted = DeploymentDescriptor.FromConfiguration(
            BuildConfiguration("SelfHosted", configuredUrl));

        cloud.Capabilities.AccountManagementUrl.Should().Be(configuredUrl);
        selfHosted.Capabilities.AccountManagementUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("Cloud", true, true)]
    [InlineData("Cloud", false, false)]
    [InlineData("SelfHosted", true, false)]
    [InlineData("SelfHosted", false, false)]
    [InlineData(null, true, false)]
    public void Hosted_browser_integration_requires_explicit_cloud_opt_in(
        string? mode, bool enabled, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [DeploymentDescriptor.ConfigurationKey] = mode,
                [DeploymentDescriptor.HostedBrowserIntegrationConfigurationKey] = enabled.ToString(),
            }).Build();

        var deployment = DeploymentDescriptor.FromConfiguration(configuration);
        deployment.Capabilities.HostedBrowserIntegrationEnabled.Should().Be(expected);
        DeploymentCapabilitiesEndpoints.ToResponse(deployment)
            .HostedBrowserIntegrationEnabled.Should().Be(expected);
    }

    [Fact]
    public void Self_hosted_response_cannot_advertise_host_integration_from_a_custom_descriptor()
    {
        var deployment = DeploymentDescriptor.For(DeploymentMode.SelfHosted);
        deployment = deployment with
        {
            Capabilities = deployment.Capabilities with { HostedBrowserIntegrationEnabled = true },
        };

        DeploymentCapabilitiesEndpoints.ToResponse(deployment)
            .HostedBrowserIntegrationEnabled.Should().BeFalse();
    }

    private static IConfiguration BuildConfiguration(string mode, string? accountManagementUrl = null)
    {
        var values = new Dictionary<string, string?>
        {
            [DeploymentDescriptor.ConfigurationKey] = mode,
        };

        if (accountManagementUrl is not null)
            values[DeploymentDescriptor.AccountManagementUrlConfigurationKey] = accountManagementUrl;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}

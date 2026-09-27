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
            AccountManagementUrl: null));
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
            AccountManagementUrl: DeploymentDescriptor.DefaultCloudAccountManagementUrl));
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

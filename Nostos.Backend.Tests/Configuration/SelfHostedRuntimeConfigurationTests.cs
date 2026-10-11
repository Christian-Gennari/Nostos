using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Health;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

public sealed class SelfHostedRuntimeConfigurationTests
{
    [Fact]
    public void SelfHosted_Data_Protection_starts_without_hosted_secrets()
    {
        var services = new ServiceCollection();

        var act = () => services.AddNostosSelfHostedDataProtection(
            new ConfigurationBuilder().Build(), AppContext.BaseDirectory);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configured_key_ring_decrypts_saved_secrets_after_host_recreation(bool relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nostos-key-ring-{Guid.NewGuid():N}");
        var keysPath = Path.Combine(root, "data", "keys");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DataProtection:KeysPath"] = relativePath ? Path.Combine("data", "keys") : keysPath,
            }).Build();

        ServiceProvider CreateHost()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNostosSelfHostedDataProtection(configuration, root);
            return services.BuildServiceProvider();
        }

        try
        {
            string encrypted;
            using (var firstHost = CreateHost())
            {
                encrypted = firstHost.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("Nostos.Tests.SavedProviderKey").Protect("saved-provider-key");
            }

            Directory.GetFiles(keysPath, "key-*.xml").Should().NotBeEmpty();
            using var recreatedHost = CreateHost();
            recreatedHost.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Nostos.Tests.SavedProviderKey").Unprotect(encrypted)
                .Should().Be("saved-provider-key");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unconfigured_key_ring_keeps_the_platform_default_repository()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosSelfHostedDataProtection(
            new ConfigurationBuilder().Build(), AppContext.BaseDirectory);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository
            .Should().BeNull();
    }

    [Fact]
    public void Readiness_contains_local_sqlite_and_no_hosted_services()
    {
        var services = new ServiceCollection();
        services.AddNostosSelfHostedHealthChecks();

        using var provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        registrations.Should().Contain(registration =>
            registration.Name == "sqlite"
            && registration.Tags.Contains(NostosHealthCheckTags.Readiness));
        registrations.Should().NotContain(registration =>
            registration.Name == "control-plane" || registration.Name == "object-storage");
    }
}

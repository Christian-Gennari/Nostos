using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Product.Composition;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class ProviderDiscoveryCompositionTests
{
    [Fact]
    public void DefaultComposition_ResolvesDiscoveryToTheLiveFanOut()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());

        services.Count(descriptor => descriptor.ServiceType == typeof(IProviderDiscovery))
            .Should().Be(1, "the product registers exactly one discovery default, with no catalog mirror");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProviderDiscovery>()
            .Should().BeOfType<ProviderDiscoveryService>();
    }

    [Fact]
    public void HostRegisteredDiscovery_WinsOverTheLiveDefault()
    {
        var hostDiscovery = new StubDiscovery();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderDiscovery>(hostDiscovery);

        services.AddNostosProduct(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProviderDiscovery>().Should().BeSameAs(hostDiscovery);
    }

    private sealed class StubDiscovery : IProviderDiscovery
    {
        public Task<ProviderDiscoveryResult> SearchAsync(
            ProviderDiscoveryRequest request,
            CancellationToken ct) =>
            Task.FromResult(new ProviderDiscoveryResult([], false, []));
    }
}

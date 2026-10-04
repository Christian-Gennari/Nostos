using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

// Slice 3 of issue #679: the SelfHosted host must register the validated
// transfer root, its volume measurement, and the durable capacity service.
public sealed class TransferStorageRegistrationTests
{
    [Fact]
    public void SelfHosted_host_registers_and_creates_the_transfer_root_and_capacity_service()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        var resolver = factory.Services.GetRequiredService<TransferPathResolver>();
        resolver.RootPath.Should().Be(
            Path.GetFullPath(Path.Combine(factory.StorageTempRoot, "transfers")));
        Directory.Exists(resolver.RootPath).Should().BeTrue(
            "host startup creates the transfer root when it is missing");

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>()
            .Should().BeOfType<TransferStorageCapacity>();
        scope.ServiceProvider.GetRequiredService<ITransferVolume>()
            .Should().BeOfType<DriveInfoTransferVolume>();
    }
}

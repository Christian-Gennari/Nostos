using System.Net;
using FluentAssertions;
using Nostos.Backend.Providers.Acquisition;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class PublicAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")] // loopback
    [InlineData("127.42.0.9")] // the whole 127.0.0.0/8
    [InlineData("::1")] // IPv6 loopback
    [InlineData("10.0.0.5")] // RFC 1918
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")] // link-local, and the cloud metadata service
    [InlineData("100.64.0.1")] // carrier-grade NAT
    [InlineData("100.127.255.254")]
    [InlineData("fe80::1")] // IPv6 link-local
    [InlineData("fc00::1")] // IPv6 unique local
    [InlineData("fd12:3456:789a::1")]
    [InlineData("0.0.0.0")] // unspecified / "this network"
    [InlineData("::")]
    [InlineData("224.0.0.1")] // multicast
    [InlineData("239.255.255.250")]
    [InlineData("ff02::1")]
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped forms of the above
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("::ffff:192.168.1.10")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:100.64.0.1")]
    [InlineData("::ffff:0.0.0.0")]
    public void IsPublic_NonPublicAddress_ReturnsFalse(string address)
    {
        PublicAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void IsPublic_PublicAddress_ReturnsTrue(string address)
    {
        PublicAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().BeTrue();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("localhost.")]
    [InlineData("covers.localhost")]
    [InlineData("a.b.localhost")]
    public void IsLocalHostName_LocalName_ReturnsTrue(string host)
    {
        PublicAddressPolicy.IsLocalHostName(host).Should().BeTrue();
    }

    [Theory]
    [InlineData("covers.example.com")]
    [InlineData("notlocalhost.example.com")]
    [InlineData("localhost.example.com")]
    public void IsLocalHostName_OrdinaryName_ReturnsFalse(string host)
    {
        PublicAddressPolicy.IsLocalHostName(host).Should().BeFalse();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("covers.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    public async Task IsSafeToFetchAsync_LocalNameOrNonPublicLiteral_ReturnsFalse(string host)
    {
        (await PublicAddressPolicy.IsSafeToFetchAsync(host, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    public async Task ConnectToPublicAsync_NonPublicHost_ThrowsBeforeOpeningASocket(string host)
    {
        var connect = () => PublicAddressPolicy.ConnectToPublicAsync(host, 443, CancellationToken.None)
            .AsTask();

        await connect.Should().ThrowAsync<HttpRequestException>()
            .WithMessage($"*{PublicAddressPolicy.RefusalReason}*");
    }
}

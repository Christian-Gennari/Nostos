using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.Services;

public sealed class CoverDownloadTests
{
    [Theory]
    [InlineData("https://localhost/cover.png")]
    [InlineData("https://127.0.0.1/cover.png")]
    [InlineData("https://10.0.0.5/cover.png")]
    [InlineData("https://[::1]/cover.png")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    public async Task TryUserUrlAsync_PrivateOrLocalAddress_FailsBeforeAnyRequest(string url)
    {
        var result = await CoverDownload.TryUserUrlAsync(
            new ForbiddenHttpClientFactory(),
            new Uri(url),
            NullLogger.Instance,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(PublicAddressPolicy.RefusalReason);
    }

    /// <summary>
    /// Fails the test if the refusal did not happen first: fetching a cover on
    /// this factory would be a request the address check should have prevented.
    /// </summary>
    private sealed class ForbiddenHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException($"No request may be made for a refused address (client '{name}').");
    }
}

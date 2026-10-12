using System.Net;
using Nostos.Backend.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

public sealed class ProviderErrorDetailTests
{
    [Fact]
    public async Task Returns_the_gateway_error_message()
    {
        var detail = await ReadAsync("{\"error\":{\"message\":\"model overloaded\"}}");

        Assert.Equal("model overloaded", detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Returns_null_for_an_empty_body(string body)
    {
        Assert.Null(await ReadAsync(body));
    }

    [Theory]
    [InlineData("{\"detail\":\"no error object\"}")]
    [InlineData("{\"error\":\"a string, not an object\"}")]
    [InlineData("{\"error\":{\"message\":42}}")]
    public async Task Returns_the_body_when_json_has_no_error_message(string body)
    {
        Assert.Equal(body, await ReadAsync(body));
    }

    [Fact]
    public async Task Truncates_a_long_json_body_to_300_characters()
    {
        var body = "{\"detail\":\"" + new string('x', 400) + "\"}";

        var detail = await ReadAsync(body);

        Assert.Equal(body[..300], detail);
    }

    [Fact]
    public async Task Returns_null_for_a_body_that_is_not_json()
    {
        Assert.Null(await ReadAsync("<html>Bad Gateway</html>"));
    }

    [Fact]
    public async Task Rethrows_when_the_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new ThrowingContent(new OperationCanceledException(cts.Token)),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProviderErrorDetail.ReadAsync(response, cts.Token));
    }

    private static async Task<string?> ReadAsync(string body)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(body),
        };

        return await ProviderErrorDetail.ReadAsync(response, CancellationToken.None);
    }

    private sealed class ThrowingContent(Exception exception) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(exception);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

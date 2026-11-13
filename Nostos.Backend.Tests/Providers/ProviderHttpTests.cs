using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The one shared place provider HTTP behaviour lives: identity headers,
/// status classification, and parse failures.
/// </summary>
public sealed class ProviderHttpTests
{
    [Fact]
    public void Client_applies_the_product_identity_without_duplicating_configured_headers()
    {
        var factory = new StubHttpClientFactory(configureClient: client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttp.ProductUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd(ProviderHttp.JsonMediaType);
        });

        var client = ProviderHttp.Client(factory, "provider", accept: ProviderHttp.JsonMediaType);

        client.DefaultRequestHeaders.UserAgent.ToString().Should().Be(ProviderHttp.ProductUserAgent);
        client.DefaultRequestHeaders.Accept.Should().ContainSingle()
            .Which.MediaType.Should().Be(ProviderHttp.JsonMediaType);
    }

    [Fact]
    public void Client_keeps_a_configured_accept_when_the_call_does_not_name_one()
    {
        var factory = new StubHttpClientFactory(configureClient: client =>
            client.DefaultRequestHeaders.Accept.ParseAdd(StandardEbooksAccept));

        var client = ProviderHttp.Client(factory, "provider");

        client.DefaultRequestHeaders.UserAgent.ToString().Should().Be(ProviderHttp.ProductUserAgent);
        client.DefaultRequestHeaders.Accept.Should().ContainSingle()
            .Which.MediaType.Should().Be(StandardEbooksAccept);
    }

    [Fact]
    public void Client_uses_a_provider_specific_identity_when_given_one()
    {
        var factory = new StubHttpClientFactory(configureClient: client =>
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttp.ProductUserAgent));

        var client = ProviderHttp.Client(factory, "provider", "Approved/1.0");

        client.DefaultRequestHeaders.UserAgent.ToString().Should().Be("Approved/1.0");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Gone, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void IsMissing_matches_only_404_and_410(HttpStatusCode statusCode, bool expected)
    {
        using var response = new HttpResponseMessage(statusCode);

        ProviderHttp.IsMissing(response).Should().Be(expected);
    }

    [Fact]
    public void EnsureSuccess_accepts_a_success_status()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NoContent);

        var act = () => ProviderHttp.EnsureSuccess(response, "gutenberg", "the catalogue");

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureSuccess_reports_the_status_with_the_callers_wording()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var act = () => ProviderHttp.EnsureSuccess(response, "wikisource", "WS Export");

        var error = act.Should().Throw<ProviderException>().Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Be(
            "The wikisource source is unavailable: WS Export answered HTTP 503");
    }

    [Fact]
    public async Task ReadXmlAsync_maps_a_malformed_body_to_invalid_response()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<feed><unclosed>", Encoding.UTF8, "application/atom+xml"),
        };

        var act = () => ProviderHttp.ReadXmlAsync(response, "gutenberg", CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task ReadJsonAsync_maps_a_malformed_body_to_invalid_response()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>maintenance</html>", Encoding.UTF8, "application/json"),
        };

        var act = () => ProviderHttp.ReadJsonAsync(response, "librivox", CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task ReadJsonAsync_of_a_model_maps_a_malformed_body_to_invalid_response()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json"),
        };

        var act = () => ProviderHttp.ReadJsonAsync<Dictionary<string, string>>(
            response,
            "litteraturbanken",
            new JsonSerializerOptions(),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    private const string StandardEbooksAccept = "application/atom+xml";
}

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The extracted Gutenberg HTTP collaborator: which named client it uses, the
/// "no such item" answer, and the retry policy the provider hands it.
/// </summary>
public sealed class GutenbergCatalogClientTests
{
    private static (GutenbergCatalogClient Client, StubHttpMessageHandler Handler) CreateClient()
    {
        var handler = new StubHttpMessageHandler();
        return (new GutenbergCatalogClient(new StubHttpClientFactory(handler), NullLogger.Instance), handler);
    }

    [Fact]
    public async Task LoadAsync_OnNotFound_IsANullDocument()
    {
        var (client, handler) = CreateClient();
        handler.Register("/ebooks/999999.opds", new HttpResponseMessage(HttpStatusCode.NotFound));

        var document = await client.LoadAsync("/ebooks/999999.opds", retryDelays: null, CancellationToken.None);

        document.Should().BeNull();
        handler.RecordedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task LoadAsync_WithoutRetryDelays_DoesNotRetryATransientGatewayError()
    {
        var (client, handler) = CreateClient();
        handler.Register("/ebooks/1342.opds", new HttpResponseMessage(HttpStatusCode.GatewayTimeout));

        var act = () => client.LoadAsync("/ebooks/1342.opds", retryDelays: null, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("the catalogue answered HTTP 504");
        handler.RecordedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task LoadAsync_WithRetryDelays_RetriesGatewayErrorsUpToThePolicy()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler().Register("/ebooks/1342.opds", _ =>
            Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        GutenbergFixtures.Load("book-1342.opds"),
                        System.Text.Encoding.UTF8,
                        "application/atom+xml"),
                });
        var client = new GutenbergCatalogClient(
            new StubHttpClientFactory(handler),
            NullLogger.Instance);

        var document = await client.LoadAsync(
            "/ebooks/1342.opds",
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero],
            CancellationToken.None);

        document.Should().NotBeNull();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_OnANonGatewayError_IsNotRetried()
    {
        var (client, handler) = CreateClient();
        handler.Register("/ebooks/1342.opds", new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => client.LoadAsync(
            "/ebooks/1342.opds",
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero],
            CancellationToken.None);

        await act.Should().ThrowAsync<ProviderException>();
        handler.RecordedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task LoadAsync_OnANonXmlBody_IsAResponseInvalid()
    {
        var (client, handler) = CreateClient();
        handler.Register("/ebooks/search.opds/?query=test", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                GutenbergFixtures.Load("not-a-feed.html") + "<unclosed tag",
                System.Text.Encoding.UTF8,
                "application/atom+xml"),
        });

        var act = () => client.LoadAsync("/ebooks/search.opds/?query=test", retryDelays: null, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task ProbeSnapshotAsync_SendsTheValidatorsOnTheSnapshotClient()
    {
        var handler = new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler);
        var client = new GutenbergCatalogClient(factory, NullLogger.Instance);
        var lastModified = new DateTimeOffset(2026, 10, 6, 14, 41, 27, TimeSpan.Zero);
        handler.Register(
            GutenbergCatalog.SnapshotPath,
            request => request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : new HttpResponseMessage(HttpStatusCode.OK));
        using var response = await client.ProbeSnapshotAsync(
            new ProviderSnapshotRequest(ETag: "\"snapshot-1\"", IfModifiedSince: lastModified),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        factory.RequestedClientNames.Should().Contain(GutenbergProvider.SnapshotHttpClientName);
        var probe = handler.RecordedRequests.Should().ContainSingle().Which;
        probe.Method.Should().Be(HttpMethod.Head);
        probe.Headers.IfNoneMatch.Should().ContainSingle()
            .Which.Tag.Should().Be("\"snapshot-1\"");
        probe.Headers.IfModifiedSince.Should().Be(lastModified);
    }

    [Fact]
    public async Task ProbeSnapshotAsync_OnATransportFailure_IsUnavailable()
    {
        var (client, handler) = CreateClient();
        handler.Register(GutenbergCatalog.SnapshotPath, _ => throw new HttpRequestException("connection reset"));

        var act = async () => await client.ProbeSnapshotAsync(
            new ProviderSnapshotRequest(),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("connection reset");
    }

    [Fact]
    public async Task ProbeSnapshotAsync_UsesTheProviderUserAgent()
    {
        var handler = new StubHttpMessageHandler();
        var client = new GutenbergCatalogClient(new StubHttpClientFactory(handler), NullLogger.Instance);
        handler.Register(GutenbergCatalog.SnapshotPath, request =>
        {
            request.Headers.UserAgent.ToString().Should()
                .Be("Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos)");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        using var response = await client.ProbeSnapshotAsync(
            new ProviderSnapshotRequest(),
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }
}

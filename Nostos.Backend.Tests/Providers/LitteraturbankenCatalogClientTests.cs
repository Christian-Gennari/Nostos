using System.Net;
using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Litteraturbanken;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The extracted Litteraturbanken HTTP collaborator: the request it builds, the
/// page it parses, and the one-shot cache in front of the rights documents.
/// </summary>
public sealed class LitteraturbankenCatalogClientTests
{
    private const string CatalogPath = "/api/query_string/etext,faksimil";
    private const string LicensePath = "/red/etc/license/license.json";
    private const string ProvenancePath = "/red/etc/provenance/provenance.json";

    private static (LitteraturbankenCatalogClient Client, StubHttpMessageHandler Handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var handler = new StubHttpMessageHandler();
        if (route is not null)
            handler.SetFallback(route);

        var factory = new StubHttpClientFactory(handler, new Uri(LitteraturbankenProvider.BaseUrl));
        return (new LitteraturbankenCatalogClient(factory, LitteraturbankenProvider.ProviderIdentifier), handler);
    }

    [Fact]
    public async Task LoadPageAsync_SendsTheFormAsAQueryStringAndParsesThePage()
    {
        var (client, handler) = CreateClient(_ =>
            LitteraturbankenFixtures.Json(LitteraturbankenFixtures.TwoFormatCatalogJson));

        var page = await client.LoadPageAsync(
            "searchable:true",
            from: 0,
            to: 100,
            CancellationToken.None,
            sortField: "lbworkid|asc");

        page.Hits.Should().Be(2);
        page.Data.Should().HaveCount(2);
        page.Data[0].ExternalId.Should().Be("lb100");

        var request = handler.RecordedRequests.Should().ContainSingle().Which;
        request.RequestUri!.AbsolutePath.Should().Be(CatalogPath);
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "from").Should().Be("0");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "to").Should().Be("100");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "q").Should().Be("searchable:true");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "sort_field").Should().Be("lbworkid|asc");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "include").Should().Contain("lbworkid");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "exclude").Should().Be("text,parts,sourcedesc,pages,errata");
        request.Headers.Accept.Should().ContainSingle()
            .Which.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task LoadPageAsync_UsesTheDefaultSortForALiveSearch()
    {
        var (client, handler) = CreateClient(_ =>
            LitteraturbankenFixtures.Json(LitteraturbankenFixtures.TwoFormatCatalogJson));

        await client.LoadPageAsync("searchable:true", from: 0, to: 4, CancellationToken.None);

        LitteraturbankenFixtures.ReadQueryValue(
                handler.RecordedRequests.Should().ContainSingle().Which.RequestUri!,
                "sort_field")
            .Should().Be("sortkey|asc");
    }

    [Fact]
    public async Task LoadPageAsync_WithoutADataList_IsAResponseInvalid()
    {
        var (client, _) = CreateClient(_ => LitteraturbankenFixtures.Json("""{"hits":1,"data":null}"""));

        var act = () => client.LoadPageAsync("searchable:true", 0, 4, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Message.Should().Contain("the catalog omitted its data list");
    }

    [Fact]
    public async Task LoadPageAsync_OnANonSuccessStatus_NamesTheCatalog()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => client.LoadPageAsync("searchable:true", 0, 4, CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("the public catalog answered HTTP 503");
    }

    [Fact]
    public async Task GetRightsCatalogAsync_FetchesEachDocumentOnlyOnce()
    {
        var (client, handler) = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            LicensePath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.RightsLicenseJson),
            ProvenancePath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.ProvenanceJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var first = await client.GetRightsCatalogAsync(CancellationToken.None);
        var second = await client.GetRightsCatalogAsync(CancellationToken.None);

        second.Should().BeSameAs(first);
        handler.RecordedRequests.Count(request => request.RequestUri!.AbsolutePath == LicensePath)
            .Should().Be(1);
        handler.RecordedRequests.Count(request => request.RequestUri!.AbsolutePath == ProvenancePath)
            .Should().Be(1);
    }

    [Fact]
    public async Task GetRightsCatalogAsync_WhenTheLicenseDocumentFails_NamesRightsMetadata()
    {
        var (client, _) = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            LicensePath => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var act = () => client.GetRightsCatalogAsync(CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("rights metadata answered HTTP 500");
    }

    [Fact]
    public async Task GetRightsCatalogAsync_WhenTheProvenanceDocumentFails_NamesProvenanceMetadata()
    {
        var (client, _) = CreateClient(request => request.RequestUri!.AbsolutePath switch
        {
            LicensePath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.RightsLicenseJson),
            ProvenancePath => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var act = () => client.GetRightsCatalogAsync(CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("provenance metadata answered HTTP 500");
    }
}

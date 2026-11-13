using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Litteraturbanken;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Composition;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class LitteraturbankenProviderTests
{
    private const string RightsLicenseJson = LitteraturbankenFixtures.RightsLicenseJson;
    private const string ProvenanceJson = LitteraturbankenFixtures.ProvenanceJson;
    private const string TwoFormatCatalogJson = LitteraturbankenFixtures.TwoFormatCatalogJson;

    private static (LitteraturbankenProvider Provider, StubHttpMessageHandler Handler) CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var handler = new StubHttpMessageHandler();
        if (route is not null)
            handler.SetFallback(route);

        var factory = new StubHttpClientFactory(
            handler,
            new Uri(LitteraturbankenProvider.BaseUrl));
        return (new LitteraturbankenProvider(factory), handler);
    }

    [Fact]
    public void Identity_IsWithdrawnAndUsesOnlyTheSourceHost()
    {
        var (provider, _) = CreateProvider();

        provider.Id.Should().Be("litteraturbanken");
        provider.DisplayName.Should().Be("Litteraturbanken");
        provider.EnabledByDefault.Should().BeFalse();
        provider.IsWithdrawn.Should().BeTrue();
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.Search);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.ItemRetrieval);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.EbookAcquisition);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.RightsInformation);
        provider.AllowedHosts.Should().Equal("litteraturbanken.se");
        provider.MaxBytesPerPart.Should().Be(128L * 1024 * 1024);
        provider.MaxTotalBytes.Should().Be(provider.MaxBytesPerPart);
        provider.MaxParts.Should().Be(1);

        new ProviderRegistry([provider]).Find("litteraturbanken")!.Provider.EnabledByDefault
            .Should().BeFalse();
        ProviderHostPolicy.IsAllowed("litteraturbanken.se", provider.AllowedHosts).Should().BeTrue();
        ProviderHostPolicy.IsAllowed("outside.example", provider.AllowedHosts).Should().BeFalse();
    }

    [Fact]
    public void ProductComposition_RegistersLitteraturbankenButWithdrawsIt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());

        using var serviceProvider = services.BuildServiceProvider();
        var registration = serviceProvider
            .GetRequiredService<IProviderRegistry>()
            .Find(LitteraturbankenProvider.ProviderIdentifier);

        // Registered, so provenance for existing books still resolves...
        registration.Should().NotBeNull();
        registration!.Provider.EnabledByDefault.Should().BeFalse();

        // ...but withheld from every install until permission is confirmed.
        registration.Provider.IsWithdrawn.Should().BeTrue();
    }

    [Fact]
    public async Task Search_CombinesListedFormatsAndDropsRestrictedOrOversizedFiles()
    {
        var (provider, handler) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/query_string/etext,faksimil")
                return LitteraturbankenFixtures.Json(TwoFormatCatalogJson);
            if (path == "/red/etc/license/license.json")
                return LitteraturbankenFixtures.Json(RightsLicenseJson);
            if (path == "/red/etc/provenance/provenance.json")
                return LitteraturbankenFixtures.Json(ProvenanceJson);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.HasMore.Should().BeFalse();
        var item = result.Items.Single();
        item.ExternalId.Should().Be("lb100");
        item.Metadata.Title.Should().Be("Kåtornas folk");
        item.Metadata.Author.Should().Be("Ester Blenda Nordström");
        item.Metadata.Language.Should().Be("Swedish");
        item.Metadata.Publisher.Should().Be("Litteraturbanken");
        item.Metadata.PublishedDate.Should().Be("1916");
        item.Assets.Should().BeEmpty("search results stay thin");
        item.Source!.RightsStatement.Should().Contain("EPUB:").And.Contain("CC0.");
        item.Source.RightsStatement.Should().Contain("PDF:");
        item.Source.RightsStatement.Should().Contain("Göteborgs universitetsbibliotek");
        item.Source.RightsStatement.Should().Contain("Public Domain");
        item.Source.ItemUrl.Should().Contain("/författare/NordstromEB/titlar/KatornasFolk/info");

        var searchRequest = handler.RecordedRequests.Single(request =>
            request.RequestUri!.AbsolutePath == "/api/query_string/etext,faksimil");
        var query = LitteraturbankenFixtures.ReadQueryValue(searchRequest.RequestUri!, "q");
        query.Should().Contain("searchable:true");
        query.Should().Contain("show:true");
        query.Should().Contain("license:cc-0 OR license:pd OR license:cc-by");
        query.Should().NotContain("lb-2");
    }

    [Fact]
    public async Task PlanAcquisition_UsesTheChosenFormatAndItsOwnRightsAndProvenance()
    {
        var (provider, handler) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/query_string/etext,faksimil")
                return LitteraturbankenFixtures.Json(TwoFormatCatalogJson);
            if (path == "/red/etc/license/license.json")
                return LitteraturbankenFixtures.Json(RightsLicenseJson);
            if (path == "/red/etc/provenance/provenance.json")
                return LitteraturbankenFixtures.Json(ProvenanceJson);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("lb100", "pdf"),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.ProviderId.Should().Be("litteraturbanken");
        plan.ExternalId.Should().Be("lb100");
        plan.Asset.Id.Should().Be("pdf");
        plan.Asset.SizeBytes.Should().Be(8000000);
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(new Uri("https://litteraturbanken.se/export/faksimil/lb100.pdf"));
        plan.Parts[0].FileExtension.Should().Be(".pdf");
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
        plan.Source!.RightsStatement.Should().NotContain("CC0.");
        plan.Source.RightsStatement.Should().Contain(
            "Det avbildade exemplaret tillhör Göteborgs universitetsbibliotek (Shelf 42).");
        plan.Source.RightsStatement.Should().NotContain("licensen CC BY");
        plan.Source.RightsUrl.Should().Be(
            "https://creativecommons.org/publicdomain/mark/1.0/deed.sv");
        plan.Source.ItemUrl.Should().Contain("/författare/NordstromEB/titlar/KatornasFolk/info");
        handler.RecordedRequests.Should().Contain(request =>
            request.RequestUri!.AbsolutePath == "/red/etc/provenance/provenance.json");
    }

    [Fact]
    public async Task GetItem_RejectsUnknownRightsAndInvalidIdsBeforeAcquisition()
    {
        var restrictedJson = LitteraturbankenFixtures.RestrictedCatalogJson;
        var (provider, handler) = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/query_string/etext,faksimil")
                return LitteraturbankenFixtures.Json(restrictedJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        (await provider.GetItemAsync("lb200", CancellationToken.None)).Should().BeNull();
        (await provider.GetItemAsync("lb200/other", CancellationToken.None)).Should().BeNull();
        handler.RecordedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_SendsTheProductUserAgentAndASingleJsonAccept()
    {
        // The client already carries the product headers, as product composition
        // configures it; the provider must not append a second Accept.
        const string userAgentText = "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos)";
        string? userAgent = null;
        string[] accept = [];
        var handler = new StubHttpMessageHandler();
        handler.SetFallback(request =>
        {
            userAgent = request.Headers.UserAgent.ToString();
            accept = request.Headers.Accept
                .Select(value => value.MediaType ?? string.Empty)
                .ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"hits":0,"data":[]}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        var factory = new StubHttpClientFactory(
            handler,
            new Uri(LitteraturbankenProvider.BaseUrl),
            client =>
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgentText);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            });
        var provider = new LitteraturbankenProvider(factory);

        await provider.SearchAsync(new ProviderSearchQuery("Kåtornas folk"), CancellationToken.None);

        userAgent.Should().Be(userAgentText);
        accept.Should().ContainSingle().Which.Should().Be("application/json");
    }

    [Fact]
    public async Task Search_SourceFailure_NamesTheStatus()
    {
        var (provider, _) = CreateProvider(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("the public catalog answered HTTP 503");
    }

    [Fact]
    public async Task Search_MalformedCatalogJson_IsReportedAsResponseInvalid()
    {
        var (provider, _) = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not the catalog</html>", System.Text.Encoding.UTF8, "application/json")
        });

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task Search_ReportsSourceFailureAsProviderUnavailable()
    {
        var (provider, _) = CreateProvider(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public async Task Snapshot_UsesRightsFilteredCatalogAndPublishesThinItems()
    {
        var (provider, handler) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/query_string/etext,faksimil")
                return LitteraturbankenFixtures.Json(TwoFormatCatalogJson);
            if (path == "/red/etc/license/license.json")
                return LitteraturbankenFixtures.Json(RightsLicenseJson);
            if (path == "/red/etc/provenance/provenance.json")
                return LitteraturbankenFixtures.Json(ProvenanceJson);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var snapshot = await provider.ReadAsync(
            new ProviderSnapshotRequest(ETag: "ignored", IfModifiedSince: DateTimeOffset.UtcNow),
            CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        snapshot.ETag.Should().BeNull("the source has no catalog-wide validator");
        snapshot.LastModified.Should().BeNull("the source has no catalog update feed");
        snapshot.NextCursor.Should().BeNull();
        var items = await ReadItemsAsync(snapshot.Items);
        items.Should().ContainSingle();
        items[0].ExternalId.Should().Be("lb100");
        items[0].Assets.Should().BeEmpty("catalog sync publishes metadata only");
        items[0].Source!.RightsStatement.Should().Contain("EPUB:").And.Contain("PDF:");

        var request = handler.RecordedRequests.Single(message =>
            message.RequestUri!.AbsolutePath == "/api/query_string/etext,faksimil");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "from").Should().Be("0");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "to").Should().Be("100");
        LitteraturbankenFixtures.ReadQueryValue(request.RequestUri!, "q")
            .Should().Contain("searchable:true")
            .And.Contain("show:true")
            .And.Contain("license:cc-0 OR license:pd OR license:cc-by");
    }

    [Fact]
    public async Task Snapshot_OverlapsPageBoundaryToKeepAllFormatsOfOneWorkTogether()
    {
        var catalog = Enumerable.Range(1, 99)
            .Select(index => ("lb" + (1000 + index), LitteraturbankenFixtures.CatalogRow("lb" + (1000 + index), "Work " + index)))
            .Concat(
            [
                ("lb1100", LitteraturbankenFixtures.CatalogRow("lb1100", "Kåtornas folk")),
                ("lb1100", LitteraturbankenFixtures.CatalogRow("lb1100", "Kåtornas folk", mediaType: "faksimil", format: "pdf", size: 8_000_000)),
                ("lb9999", LitteraturbankenFixtures.CatalogRow("lb9999", "Last work")),
            ])
            .ToArray();
        var (provider, handler) = CreateProvider(request => LitteraturbankenFixtures.ServeCatalog(request, catalog));

        var firstPage = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        var firstItems = await ReadItemsAsync(firstPage.Items);
        firstItems.Should().HaveCount(99);
        firstItems.Should().NotContain(item => item.ExternalId == "lb1100");
        firstPage.NextCursor.Should().NotBeNull();

        var secondPage = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: firstPage.NextCursor),
            CancellationToken.None);
        var secondItems = await ReadItemsAsync(secondPage.Items);
        secondPage.NextCursor.Should().BeNull();
        secondItems.Should().HaveCount(2);
        var combinedWork = secondItems.Single(item => item.ExternalId == "lb1100");
        combinedWork.Assets.Should().BeEmpty();
        combinedWork.Source!.RightsStatement.Should().Contain("EPUB:").And.Contain("PDF:");

        var catalogRequests = handler.RecordedRequests
            .Where(message => message.RequestUri!.AbsolutePath == "/api/query_string/etext,faksimil")
            .ToArray();
        catalogRequests.Should().HaveCount(2);
        catalogRequests.Should().OnlyContain(message =>
            LitteraturbankenFixtures.ReadQueryValue(message.RequestUri!, "from") == "0"
            && LitteraturbankenFixtures.ReadQueryValue(message.RequestUri!, "to") == "100"
            && LitteraturbankenFixtures.ReadQueryValue(message.RequestUri!, "sort_field") == "lbworkid|asc");
        var queries = catalogRequests
            .Select(message => LitteraturbankenFixtures.ReadQueryValue(message.RequestUri!, "q"))
            .ToArray();
        queries.Should().ContainSingle(query => !query.Contains("lbworkid:>="));
        queries.Should().ContainSingle(query => query.EndsWith("lbworkid:>=lb1100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_PublishesEachWorkOnceWhenTheSourceScattersItsRowsAndExceedsTenThousandOffsets()
    {
        // Regression for the first production sync: rows of one work were far
        // apart in the source's default order, so two pages produced the same
        // item and the host's primary key rejected the generation. Ids with
        // letters are skipped as items but must still be walked past.
        var catalog = new List<(string Id, object Row)>();
        for (var index = 0; index < 230; index++)
        {
            var id = "lb" + (20000 + index);
            catalog.Add((id, LitteraturbankenFixtures.CatalogRow(id, "Work " + index)));
            if (index % 7 == 0)
            {
                catalog.Add((id, LitteraturbankenFixtures.CatalogRow(id, "Work " + index, mediaType: "faksimil", format: "pdf", size: 4_000_000)));
            }
        }

        catalog.Add(("lb0889slfmxtqsqq3m", LitteraturbankenFixtures.CatalogRow("lb0889slfmxtqsqq3m", "Letter id")));
        var (provider, handler) = CreateProvider(request => LitteraturbankenFixtures.ServeCatalog(request, catalog));

        var (items, pages) = await WalkSnapshotAsync(provider);

        pages.Should().BeGreaterThan(2);
        items.Select(item => item.ExternalId).Should()
            .OnlyHaveUniqueItems()
            .And.HaveCount(230);
        handler.RecordedRequests
            .Where(message => message.RequestUri!.AbsolutePath == "/api/query_string/etext,faksimil")
            .Should().OnlyContain(message => LitteraturbankenFixtures.ReadQueryValue(message.RequestUri!, "from") == "0");
    }

    [Fact]
    public async Task Snapshot_RejectsMalformedContinuationCursor()
    {
        var (provider, handler) = CreateProvider();

        var act = () => provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: "v2|100|1"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    public async Task Search_MapsTransportFailureToProviderUnavailable(Type exceptionType)
    {
        var (provider, _) = CreateProvider(_ =>
            throw (Exception)Activator.CreateInstance(exceptionType, "connection reset")!);

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain(LitteraturbankenProvider.ProviderIdentifier);
    }

    [Fact]
    public async Task Search_MapsClientTimeoutToProviderUnavailable_WhilePreservingCallerCancellation()
    {
        var timeoutProvider = new LitteraturbankenProvider(
            TransientHttpTestHelpers.CreateTimeoutFactory(
                new Uri(LitteraturbankenProvider.BaseUrl),
                TimeSpan.FromMilliseconds(25)));

        var timeoutAct = () => timeoutProvider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);
        var error = (await timeoutAct.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain(LitteraturbankenProvider.ProviderIdentifier);

        using var cts = new CancellationTokenSource();
        var (cancelProvider, _) = CreateProvider(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("caller cancelled", null, cts.Token);
        });

        var cancelAct = () => cancelProvider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            cts.Token);
        await cancelAct.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<(List<ProviderItem> Items, int Pages)> WalkSnapshotAsync(
        LitteraturbankenProvider provider)
    {
        var items = new List<ProviderItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var snapshot = await provider.ReadAsync(
                new ProviderSnapshotRequest(Cursor: cursor),
                CancellationToken.None);
            items.AddRange(await ReadItemsAsync(snapshot.Items));
            cursor = snapshot.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 20);

        return (items, pages);
    }

    private static async Task<List<ProviderItem>> ReadItemsAsync(IAsyncEnumerable<ProviderItem> items)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items)
            result.Add(item);
        return result;
    }
}

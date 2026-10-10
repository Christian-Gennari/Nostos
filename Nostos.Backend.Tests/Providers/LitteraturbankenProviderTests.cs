using System.Net;
using System.Text.Json;
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
    private const string RightsLicenseJson = """
        {
          "cc-0": "<text><p>Utgåvan är fri från kända upphovsrättsliga begränsningar. CC0. Hänvisa till Litteraturbanken.</p><a rel=\"license\" href=\"https://creativecommons.org/publicdomain/zero/1.0/deed.sv\">CC0</a></text>",
          "pd": "<text><p>Detta verk är fritt från kända upphovsrättsliga begränsningar. Hänvisa till {{provenance}} och Litteraturbanken.</p><a rel=\"license\" href=\"https://creativecommons.org/publicdomain/mark/1.0/deed.sv\">Public Domain</a></text>",
          "cc-by": "<text><p>Litteraturbanken. För detta verk gäller licensen CC BY.</p><a rel=\"license\" href=\"https://creativecommons.org/licenses/by/4.0/deed.sv\">CC BY</a></text>"
        }
        """;

    private const string ProvenanceJson = """
        {
          "GUB": {
            "fullname": "Göteborgs universitetsbibliotek",
            "text": {
              "etext": "Det exemplar som ligger till grund för Litteraturbankens utgåva tillhör Göteborgs universitetsbibliotek{{signum}}.",
              "faksimilprint": "Det avbildade exemplaret tillhör Göteborgs universitetsbibliotek{{signum}}.",
              "faksimilnoprint": "Det avbildade manuskriptet tillhör Göteborgs universitetsbibliotek{{signum}}."
            }
          }
        }
        """;

    private const string TwoFormatCatalogJson = """
        {
          "hits": 2,
          "distinct_hits": 1,
          "data": [
            {
              "lbworkid": "lb100",
              "title": "Kåtornas folk",
              "shorttitle": "Kåtornas folk",
              "titleid": "KatornasFolk",
              "work_titleid": "KatornasFolk",
              "mediatype": "etext",
              "license": "cc-0",
              "show": true,
              "searchable": true,
              "sort_date_imprint": { "plain": "1916" },
              "authors": [{ "authorid": "NordstromEB", "full_name": "Ester Blenda Nordström" }],
              "provenance": [{ "library": "GUB", "signum": "Geogr. Sv. Lappland" }],
              "export": [{ "type": "epub", "size": 301593 }]
            },
            {
              "lbworkid": "lb100",
              "title": "Kåtornas folk",
              "shorttitle": "Kåtornas folk",
              "titleid": "KatornasFolk",
              "work_titleid": "KatornasFolk",
              "mediatype": "faksimil",
              "license": "pd",
              "printed": true,
              "show": true,
              "searchable": true,
              "sort_date_imprint": { "plain": "1916" },
              "authors": [{ "authorid": "NordstromEB", "full_name": "Ester Blenda Nordström" }],
              "provenance": [{ "library": "GUB", "signum": "Shelf 42" }],
              "export": [{ "type": "pdf", "size": 8000000 }]
            }
          ]
        }
        """;

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
    public void Identity_IsOptInAndUsesOnlyTheSourceHost()
    {
        var (provider, _) = CreateProvider();

        provider.Id.Should().Be("litteraturbanken");
        provider.DisplayName.Should().Be("Litteraturbanken");
        provider.EnabledByDefault.Should().BeFalse();
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
    public void ProductComposition_RegistersLitteraturbankenDisabledByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());

        using var serviceProvider = services.BuildServiceProvider();
        var registration = serviceProvider
            .GetRequiredService<IProviderRegistry>()
            .Find(LitteraturbankenProvider.ProviderIdentifier);

        registration.Should().NotBeNull();
        registration!.Provider.EnabledByDefault.Should().BeFalse();
    }

    [Fact]
    public async Task Search_CombinesListedFormatsAndDropsRestrictedOrOversizedFiles()
    {
        var (provider, handler) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/query_string/etext,faksimil")
                return Json(TwoFormatCatalogJson);
            if (path == "/red/etc/license/license.json")
                return Json(RightsLicenseJson);
            if (path == "/red/etc/provenance/provenance.json")
                return Json(ProvenanceJson);

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
        var query = ReadQueryValue(searchRequest.RequestUri!, "q");
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
                return Json(TwoFormatCatalogJson);
            if (path == "/red/etc/license/license.json")
                return Json(RightsLicenseJson);
            if (path == "/red/etc/provenance/provenance.json")
                return Json(ProvenanceJson);

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
        var restrictedJson = """
            {
              "hits": 1,
              "data": [{
                "lbworkid": "lb200",
                "title": "Restricted",
                "titleid": "Restricted",
                "mediatype": "etext",
                "license": "lb-2-20120306",
                "show": true,
                "authors": [{ "authorid": "Author", "full_name": "Author Name" }],
                "export": [{ "type": "epub", "size": 200000 }]
              }]
            }
            """;
        var (provider, handler) = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/query_string/etext,faksimil")
                return Json(restrictedJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        (await provider.GetItemAsync("lb200", CancellationToken.None)).Should().BeNull();
        (await provider.GetItemAsync("lb200/other", CancellationToken.None)).Should().BeNull();
        handler.RecordedRequests.Should().ContainSingle();
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
                return Json(TwoFormatCatalogJson);
            if (path == "/red/etc/license/license.json")
                return Json(RightsLicenseJson);
            if (path == "/red/etc/provenance/provenance.json")
                return Json(ProvenanceJson);

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
        ReadQueryValue(request.RequestUri!, "from").Should().Be("0");
        ReadQueryValue(request.RequestUri!, "to").Should().Be("100");
        ReadQueryValue(request.RequestUri!, "q")
            .Should().Contain("searchable:true")
            .And.Contain("show:true")
            .And.Contain("license:cc-0 OR license:pd OR license:cc-by");
    }

    [Fact]
    public async Task Snapshot_OverlapsPageBoundaryToKeepAllFormatsOfOneWorkTogether()
    {
        var rows = Enumerable.Range(1, 99)
            .Select(index => CatalogRow("lb" + (1000 + index), "Work " + index))
            .Concat(
            [
                CatalogRow("lb100", "Kåtornas folk"),
                CatalogRow("lb100", "Kåtornas folk", mediaType: "faksimil", format: "pdf", size: 8_000_000),
                CatalogRow("lb9999", "Last work"),
            ])
            .ToArray();
        var (provider, handler) = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/query_string/etext,faksimil")
            {
                var offset = int.Parse(ReadQueryValue(request.RequestUri!, "from"));
                var page = rows.Skip(offset).Take(100).ToArray();
                return Json(CatalogJson(rows.Length, page));
            }

            if (path == "/red/etc/license/license.json")
                return Json(RightsLicenseJson);
            if (path == "/red/etc/provenance/provenance.json")
                return Json(ProvenanceJson);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var firstPage = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        var firstItems = await ReadItemsAsync(firstPage.Items);
        firstItems.Should().HaveCount(99);
        firstItems.Should().NotContain(item => item.ExternalId == "lb100");
        firstPage.NextCursor.Should().NotBeNull();

        var secondPage = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: firstPage.NextCursor),
            CancellationToken.None);
        var secondItems = await ReadItemsAsync(secondPage.Items);
        secondPage.NextCursor.Should().BeNull();
        secondItems.Should().HaveCount(2);
        var combinedWork = secondItems.Single(item => item.ExternalId == "lb100");
        combinedWork.Assets.Should().BeEmpty();
        combinedWork.Source!.RightsStatement.Should().Contain("EPUB:").And.Contain("PDF:");

        var offsets = handler.RecordedRequests
            .Where(message => message.RequestUri!.AbsolutePath == "/api/query_string/etext,faksimil")
            .Select(message => ReadQueryValue(message.RequestUri!, "from"))
            .ToArray();
        offsets.Should().HaveCount(2);
        offsets.Should().Contain("0").And.Contain("99");
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

    [Fact]
    public async Task Search_MapsTransportFailureToProviderUnavailable()
    {
        var (provider, _) = CreateProvider(_ =>
            throw new HttpRequestException("connection reset"));

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json"),
        };

    private static object CatalogRow(
        string id,
        string title,
        string mediaType = "etext",
        string format = "epub",
        long size = 301593) => new
    {
        lbworkid = id,
        title,
        shorttitle = title,
        titleid = title.Replace(' ', '_'),
        work_titleid = title.Replace(' ', '_'),
        mediatype = mediaType,
        license = mediaType == "faksimil" ? "pd" : "cc-0",
        printed = true,
        searchable = true,
        show = true,
        sort_date_imprint = new { plain = "1916" },
        authors = new[] { new { authorid = "TestAuthor", full_name = "Test Author" } },
        provenance = new[] { new { library = "GUB", signum = "Shelf 1" } },
        export = new[] { new { type = format, size } },
    };

    private static string CatalogJson(int hits, IReadOnlyCollection<object> rows) =>
        JsonSerializer.Serialize(new
        {
            hits,
            distinct_hits = rows.Select(row => JsonSerializer.SerializeToElement(row)
                .GetProperty("lbworkid").GetString()).Distinct().Count(),
            data = rows,
        });

    private static async Task<List<ProviderItem>> ReadItemsAsync(IAsyncEnumerable<ProviderItem> items)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items)
            result.Add(item);
        return result;
    }

    private static string ReadQueryValue(Uri uri, string name)
    {
        foreach (var field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator < 0)
                continue;

            if (WebUtility.UrlDecode(field[..separator]) == name)
                return WebUtility.UrlDecode(field[(separator + 1)..]) ?? string.Empty;
        }

        return string.Empty;
    }
}

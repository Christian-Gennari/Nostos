using System.Net;
using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Litteraturbanken;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// Pins the observable behaviour of <see cref="LitteraturbankenProvider"/> before
/// it is split into focused collaborators (#1597): the provider's own contract,
/// the mapping of the recorded catalog rows, format and rights selection, cursor
/// paging, and every failure code it surfaces. These tests must stay green,
/// unchanged, across the split.
/// </summary>
public sealed class LitteraturbankenProviderCharacterizationTests
{
    private const string CatalogPath = "/api/query_string/etext,faksimil";
    private const string LicensePath = "/red/etc/license/license.json";
    private const string ProvenancePath = "/red/etc/provenance/provenance.json";

    private static (LitteraturbankenProvider Provider, StubHttpMessageHandler Handler) CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var handler = new StubHttpMessageHandler();
        if (route is not null)
            handler.SetFallback(route);

        var factory = new StubHttpClientFactory(handler, new Uri(LitteraturbankenProvider.BaseUrl));
        return (new LitteraturbankenProvider(factory), handler);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> RecordedCatalog() =>
        request => request.RequestUri!.AbsolutePath switch
        {
            CatalogPath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.TwoFormatCatalogJson),
            LicensePath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.RightsLicenseJson),
            ProvenancePath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.ProvenanceJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    private static async Task<List<ProviderItem>> ReadItemsAsync(IAsyncEnumerable<ProviderItem> items)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items)
            result.Add(item);

        return result;
    }

    [Fact]
    public void Identity_IsRegisteredButWithdrawn()
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
    }

    [Fact]
    public async Task Search_OnTheRecordedCatalog_CombinesFormatsAndStaysThin()
    {
        var (provider, handler) = CreateProvider(RecordedCatalog());

        var result = await provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.HasMore.Should().BeFalse();
        result.Notice.Should().BeNull();

        var item = result.Items.Single();
        item.ProviderId.Should().Be("litteraturbanken");
        item.ExternalId.Should().Be("lb100");
        item.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        item.Metadata.Should().Be(new ProviderMetadata(
            Title: "Kåtornas folk",
            Author: "Ester Blenda Nordström",
            Language: "Swedish",
            Publisher: "Litteraturbanken",
            PublishedDate: "1916"));
        item.Assets.Should().BeEmpty("search results stay thin");
        item.Cover.Should().BeNull();
        item.PartCount.Should().BeNull();
        item.Source!.ItemUrl.Should().Be(
            "https://litteraturbanken.se/författare/NordstromEB/titlar/KatornasFolk/info");
        item.Source.RightsUrl.Should().BeNull("the two formats carry two different licenses");
        item.Source.RightsStatement.Should().StartWith("EPUB: ")
            .And.Contain("Utgåvan är fri från kända upphovsrättsliga begränsningar. CC0.")
            .And.Contain("Provenance: Det exemplar som ligger till grund för Litteraturbankens utgåva tillhör Göteborgs universitetsbibliotek (Geogr. Sv. Lappland).")
            .And.Contain("(https://creativecommons.org/publicdomain/zero/1.0/deed.sv)")
            .And.Contain("PDF: ")
            .And.Contain("Det avbildade exemplaret tillhör Göteborgs universitetsbibliotek (Shelf 42).")
            .And.Contain("(https://creativecommons.org/publicdomain/mark/1.0/deed.sv)");

        var catalogRequest = handler.RecordedRequests.Single(request =>
            request.RequestUri!.AbsolutePath == CatalogPath);
        LitteraturbankenFixtures.ReadQueryValue(catalogRequest.RequestUri!, "from").Should().Be("0");
        LitteraturbankenFixtures.ReadQueryValue(catalogRequest.RequestUri!, "to").Should().Be("42");
        var query = LitteraturbankenFixtures.ReadQueryValue(catalogRequest.RequestUri!, "q");
        query.Should().StartWith(
            "@type=cross_fields @default_operator=AND @fields=autocomplete.scandinavian Kåtornas folk AND ")
            .And.Contain("searchable:true")
            .And.Contain("show:true")
            .And.Contain("(license:cc-0 OR license:pd OR license:cc-by)");
        handler.RecordedRequestPaths.Should().Contain(LicensePath).And.Contain(ProvenancePath);
    }

    [Fact]
    public async Task Search_WithAnOffsetPastTheCatalogCeiling_ReturnsANotice()
    {
        var (provider, handler) = CreateProvider();

        var result = await provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk", Offset: 50),
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
        result.Notice.Should().Be("Use a more specific search to narrow Litteraturbanken results.");
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_WithATooShortQueryOrANonEbookKind_ReturnsNothingWithoutRequesting()
    {
        var (provider, handler) = CreateProvider();

        var shortResult = await provider.SearchAsync(new ProviderSearchQuery("a"), CancellationToken.None);
        var kindResult = await provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk", Kind: ProviderMediaKind.Audiobook),
            CancellationToken.None);

        shortResult.Items.Should().BeEmpty();
        kindResult.Items.Should().BeEmpty();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetItem_OnTheRecordedCatalog_ExposesBothFormatsAndFullMetadata()
    {
        var (provider, _) = CreateProvider(RecordedCatalog());

        var item = await provider.GetItemAsync("lb100", CancellationToken.None);

        item.Should().NotBeNull();
        item!.ExternalId.Should().Be("lb100");
        item.Metadata.Title.Should().Be("Kåtornas folk");
        item.Metadata.Author.Should().Be("Ester Blenda Nordström");
        item.Assets.Select(asset => asset.Id).Should().Equal("epub", "pdf");
        item.Assets[0].Should().Be(new ProviderAsset(
            "epub",
            ProviderMediaKind.Ebook,
            "EPUB",
            "application/epub+zip",
            301593,
            IsPreferred: true));
        item.Assets[1].Should().Be(new ProviderAsset(
            "pdf",
            ProviderMediaKind.Ebook,
            "PDF",
            "application/pdf",
            8000000));
        item.Source!.RightsStatement.Should().StartWith("EPUB: ").And.Contain("PDF: ");
    }

    [Fact]
    public async Task PlanAcquisition_WithNoAssetChosen_PlansThePreferredEpub()
    {
        var (provider, _) = CreateProvider(RecordedCatalog());

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("lb100", AssetId: null),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.ProviderId.Should().Be("litteraturbanken");
        plan.ExternalId.Should().Be("lb100");
        plan.Asset.Should().Be(new ProviderAsset(
            "epub",
            ProviderMediaKind.Ebook,
            "EPUB",
            "application/epub+zip",
            301593,
            IsPreferred: true));
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://litteraturbanken.se/txt/epub/NordstromEB_KatornasFolk.epub"));
        plan.Parts[0].FileExtension.Should().Be(".epub");
        plan.Parts[0].ExpectedBytes.Should().Be(301593);
        plan.Parts[0].Label.Should().Be("EPUB");
        plan.Output.Should().Be(new ProviderOutput(".epub", "application/epub+zip", "EPUB"));
        plan.Source.Should().Be(new ProviderSourceInfo(
            ItemUrl: "https://litteraturbanken.se/författare/NordstromEB/titlar/KatornasFolk/info",
            RightsStatement: "Utgåvan är fri från kända upphovsrättsliga begränsningar. CC0. Hänvisa till Litteraturbanken.CC0 Provenance: Det exemplar som ligger till grund för Litteraturbankens utgåva tillhör Göteborgs universitetsbibliotek (Geogr. Sv. Lappland).",
            RightsUrl: "https://creativecommons.org/publicdomain/zero/1.0/deed.sv"));
    }

    [Fact]
    public async Task PlanAcquisition_WithAnExplicitPdf_UsesThatFormatsOwnRightsAndProvenance()
    {
        var (provider, _) = CreateProvider(RecordedCatalog());

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("lb100", AssetId: "pdf"),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("pdf");
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://litteraturbanken.se/export/faksimil/lb100.pdf"));
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
        plan.Source.Should().Be(new ProviderSourceInfo(
            ItemUrl: "https://litteraturbanken.se/författare/NordstromEB/titlar/KatornasFolk/info",
            RightsStatement: "Detta verk är fritt från kända upphovsrättsliga begränsningar. Hänvisa till Det avbildade exemplaret tillhör Göteborgs universitetsbibliotek (Shelf 42). och Litteraturbanken.Public Domain",
            RightsUrl: "https://creativecommons.org/publicdomain/mark/1.0/deed.sv"));
    }

    [Fact]
    public async Task PlanAcquisition_ForAnUnknownAsset_IsAssetUnavailable()
    {
        var (provider, _) = CreateProvider(RecordedCatalog());

        var act = () => provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("lb100", AssetId: "audiobook"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.AssetUnavailable);
        error.Message.Should().Be("litteraturbanken item lb100 does not offer asset 'audiobook'.");
    }

    [Fact]
    public async Task GetItem_ForARestrictedLicenseOrAnInvalidId_IsNullBeforeAcquisition()
    {
        var (provider, handler) = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath == CatalogPath)
                return LitteraturbankenFixtures.Json(LitteraturbankenFixtures.RestrictedCatalogJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        (await provider.GetItemAsync("lb200", CancellationToken.None)).Should().BeNull();
        (await provider.GetItemAsync("lb200/other", CancellationToken.None)).Should().BeNull();
        handler.RecordedRequests.Should().ContainSingle("an invalid id never reaches the catalog");
    }

    [Fact]
    public async Task Search_OnAServerError_IsUnavailableAndNamesTheStatus()
    {
        var (provider, _) = CreateProvider(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Be(
            "The litteraturbanken source is unavailable: the public catalog answered HTTP 503");
    }

    [Fact]
    public async Task Search_OnANonJsonBody_IsAResponseInvalid()
    {
        var (provider, _) = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<html>not the catalog</html>",
                System.Text.Encoding.UTF8,
                "application/json"),
        });

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task Search_WhenTheCatalogOmitsItsDataList_IsAResponseInvalid()
    {
        var (provider, _) = CreateProvider(_ => LitteraturbankenFixtures.Json("""{"hits":1,"data":null}"""));

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Message.Should().Contain("the catalog omitted its data list");
    }

    [Fact]
    public async Task Search_WhenTheRightsMetadataFails_IsUnavailableAndNamesIt()
    {
        var (provider, _) = CreateProvider(request => request.RequestUri!.AbsolutePath switch
        {
            CatalogPath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.TwoFormatCatalogJson),
            LicensePath => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("rights metadata answered HTTP 500");
    }

    [Fact]
    public async Task Search_WhenTheProvenanceMetadataFails_IsUnavailableAndNamesIt()
    {
        var (provider, _) = CreateProvider(request => request.RequestUri!.AbsolutePath switch
        {
            CatalogPath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.TwoFormatCatalogJson),
            LicensePath => LitteraturbankenFixtures.Json(LitteraturbankenFixtures.RightsLicenseJson),
            ProvenancePath => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("Kåtornas folk"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Contain("provenance metadata answered HTTP 500");
    }

    [Fact]
    public async Task Snapshot_WithAnInvalidCursor_IsAResponseInvalidWithoutRequests()
    {
        var (provider, handler) = CreateProvider();

        var act = () => provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: "v2|100|1"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Message.Should().Contain("the catalog cursor was invalid");
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Snapshot_OverAPageBoundary_EncodesTheNextCursorAndResumesAfterTheLastWork()
    {
        var catalog = Enumerable.Range(1000, 101)
            .Select(index =>
            {
                var id = "lb" + index;
                return (Id: id, Row: LitteraturbankenFixtures.CatalogRow(id, "Work " + index));
            })
            .ToArray();
        var (provider, _) = CreateProvider(request => LitteraturbankenFixtures.ServeCatalog(request, catalog));

        var firstPage = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        var firstItems = await ReadItemsAsync(firstPage.Items);

        firstPage.Status.Should().Be(ProviderSnapshotStatus.Updated);
        firstPage.ETag.Should().BeNull();
        firstPage.LastModified.Should().BeNull();
        firstPage.NextCursor.Should().Be("v2|lb1099|1");
        firstItems.Should().HaveCount(99);
        firstItems.Should().NotContain(item => item.ExternalId == "lb1099");
        firstItems.Should().OnlyContain(item => item.Assets.Count == 0);
        firstItems.Should().OnlyContain(item => item.ProviderId == "litteraturbanken");

        var secondPage = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: firstPage.NextCursor),
            CancellationToken.None);
        var secondItems = await ReadItemsAsync(secondPage.Items);

        secondPage.NextCursor.Should().BeNull();
        secondItems.Select(item => item.ExternalId).Should().Equal("lb1099", "lb1100");
    }
}

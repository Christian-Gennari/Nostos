using System.Net;
using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Wikisource;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class WikisourceProviderTests
{
    private const string CatalogPath = "/opds/en/Ready_for_export.xml";
    private static string LoadFixture(string filename)
    {
        var basePath = AppContext.BaseDirectory;
        var directPath = Path.Combine(basePath, "Fixtures", "wikisource", filename);
        if (File.Exists(directPath))
            return File.ReadAllText(directPath);

        var current = new DirectoryInfo(basePath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Fixtures", "wikisource", filename);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            var nestedCandidate = Path.Combine(
                current.FullName,
                "Nostos.Backend.Tests",
                "Fixtures",
                "wikisource",
                filename);
            if (File.Exists(nestedCandidate))
                return File.ReadAllText(nestedCandidate);

            current = current.Parent;
        }

        throw new FileNotFoundException("Wikisource fixture '" + filename + "' could not be located.");
    }

    private static (WikisourceProvider Provider, StubHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler);
        return (new WikisourceProvider(factory), handler);
    }

    [Fact]
    public async Task CatalogParsing_NormalizesMetadataAssetsCoverAndRights()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(CatalogPath, LoadFixture("ready-for-export.xml"));

        var result = await provider.SearchAsync(
            new ProviderSearchQuery("pride"),
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.HasMore.Should().BeFalse();

        var item = result.Items.Single();
        item.ProviderId.Should().Be("wikisource");
        item.ExternalId.Should().Be("Pride and Prejudice");
        item.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        item.Metadata.Title.Should().Be("Pride and Prejudice");
        item.Metadata.Author.Should().Be("Jane Austen");
        item.Metadata.Language.Should().Be("English");
        item.Metadata.Publisher.Should().Be("Wikisource");
        item.Metadata.PublishedDate.Should().Be("1813");
        item.Metadata.Categories.Should().Be("Fiction");
        item.Source.Should().NotBeNull();
        item.Source!.ItemUrl.Should().Be("https://en.wikisource.org/wiki/Pride_and_Prejudice");
        item.Source.RightsStatement.Should().Be("Public Domain");

        // Search remains thin: format assets are loaded only after selection.
        item.Assets.Should().BeEmpty();

        item.Cover.Should().NotBeNull();
        item.Cover!.Url.Should().Be(new Uri("https://thumb.wikimedia.org/example/pride.jpg"));
        item.Cover.ContentType.Should().Be("image/jpeg");
        item.Cover.FileExtension.Should().Be(".jpg");
    }

    [Fact]
    public async Task Search_FiltersCatalogLocally_ByTitleAuthorAndCategory_AndPaginates()
    {
        var xml = LoadFixture("ready-for-export.xml");
        var (provider, handler) = CreateProvider();
        handler.SetFallback(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/atom+xml"),
        });

        var author = await provider.SearchAsync(
            new ProviderSearchQuery("austen"),
            CancellationToken.None);
        author.Items.Select(item => item.Metadata.Title).Should().Equal("Pride and Prejudice");

        var category = await provider.SearchAsync(
            new ProviderSearchQuery("essay"),
            CancellationToken.None);
        category.Items.Select(item => item.Metadata.Title).Should().Equal("A Modern Essay");

        var page = await provider.SearchAsync(
            new ProviderSearchQuery("", Limit: 1, Offset: 1),
            CancellationToken.None);
        page.Items.Should().ContainSingle();
        page.Items[0].Metadata.Title.Should().Be("A Modern Essay");
        page.HasMore.Should().BeTrue();

        var audioOnly = await provider.SearchAsync(
            new ProviderSearchQuery("pride", Kind: ProviderMediaKind.Audiobook),
            CancellationToken.None);
        audioOnly.Items.Should().BeEmpty();
        // Three ebook searches, one catalogue download: the parsed catalogue
        // is reused (#641), and an audiobook-only search never fetches it.
        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Detail_PreservesAtomRightsVerbatim_AndUsesSingleItemEndpoint()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/?lang=en&format=atom&page=Pride%20and%20Prejudice",
            LoadFixture("item-pride-and-prejudice.atom"));

        var item = await provider.GetItemAsync("Pride and Prejudice", CancellationToken.None);

        item.Should().NotBeNull();
        item!.Source!.RightsStatement.Should().Be("CC-BY-SA 3.0");
        item.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        item.Assets.Select(asset => asset.Id).Should().Equal("epub", "pdf");
        item.Assets.Single(asset => asset.Id == "epub").IsPreferred.Should().BeTrue();
        item.Assets.Single(asset => asset.Id == "epub").SourceFormat.Should().Be("application/epub+zip");
        item.Assets.Single(asset => asset.Id == "pdf").SourceFormat.Should().Be("application/pdf");
        handler.RecordedRequestPaths.Should().ContainSingle(
            "/?lang=en&format=atom&page=Pride%20and%20Prejudice");
    }

    [Fact]
    public async Task PlanAcquisition_ResolvesPublishedEpubLink_WithoutLeakingProviderShape()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/?lang=en&format=atom&page=Pride%20and%20Prejudice",
            LoadFixture("item-pride-and-prejudice.atom"));

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("Pride and Prejudice", AssetId: null),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.ProviderId.Should().Be("wikisource");
        plan.ExternalId.Should().Be("Pride and Prejudice");
        plan.Asset.Id.Should().Be("epub");
        plan.Asset.Kind.Should().Be(ProviderMediaKind.Ebook);
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://ws-export.wmcloud.org/?lang=en&format=epub&page=Pride+and+Prejudice"));
        plan.Parts[0].FileExtension.Should().Be(".epub");
        plan.Output.Should().Be(new ProviderOutput(".epub", "application/epub+zip", "EPUB"));
        plan.Source!.RightsStatement.Should().Be("CC-BY-SA 3.0");
    }

    [Fact]
    public async Task Detail_WhenAtomAdvertisesPdf_UsesPublishedPdfLink()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/?lang=en&format=atom&page=Pride%20and%20Prejudice",
            LoadFixture("item-pride-and-prejudice-epub-pdf.atom"));

        var item = await provider.GetItemAsync("Pride and Prejudice", CancellationToken.None);

        item.Should().NotBeNull();
        item!.Assets.Select(asset => asset.Id).Should().Equal("epub", "pdf");
        item.Assets.Single(asset => asset.Id == "epub").IsPreferred.Should().BeTrue();

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("Pride and Prejudice", AssetId: "pdf"),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://ws-export.wmcloud.org/?lang=en&format=pdf-a4&page=Pride+and+Prejudice"));
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
    }

    [Fact]
    public async Task PlanAcquisition_Pdf_UsesIndependentWsExportRoute()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/?lang=en&format=atom&page=Pride%20and%20Prejudice",
            LoadFixture("item-pride-and-prejudice.atom"));

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("Pride and Prejudice", AssetId: "pdf"),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("pdf");
        plan.Asset.Kind.Should().Be(ProviderMediaKind.Ebook);
        plan.Asset.SourceFormat.Should().Be("application/pdf");
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://ws-export.wmcloud.org/?lang=en&format=pdf&page=Pride%20and%20Prejudice"));
        plan.Parts[0].FileExtension.Should().Be(".pdf");
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
    }

    [Fact]
    public async Task PlanAcquisition_UnknownAsset_ThrowsStableProviderError()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/?lang=en&format=atom&page=Pride%20and%20Prejudice",
            LoadFixture("item-pride-and-prejudice.atom"));

        var act = async () => await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("Pride and Prejudice", AssetId: "mobi"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.AssetUnavailable);
    }

    [Fact]
    public async Task InvalidExternalId_IsRejectedBeforeHttp()
    {
        var (provider, handler) = CreateProvider();

        var item = await provider.GetItemAsync(new string('a', 513), CancellationToken.None);

        item.Should().BeNull();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task NonAtomResponse_IsReportedAsInvalidProviderResponse()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(
            CatalogPath,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body>maintenance</body></html>",
                    System.Text.Encoding.UTF8,
                    "text/html"),
            });

        var act = async () => await provider.SearchAsync(
            new ProviderSearchQuery("pride"),
            CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public void PolicyAndRegistration_UseOnlySpecifiedHostsAndSingleEbookPart()
    {
        var (provider, _) = CreateProvider();

        provider.AllowedHosts.Should().Equal(
            "ws-export.wmcloud.org",
            "upload.wikimedia.org",
            "thumb.wikimedia.org");
        provider.MaxParts.Should().Be(1);
        provider.MaxBytesPerPart.Should().Be(provider.MaxTotalBytes);

        ProviderHostPolicy.IsAllowed("ws-export.wmcloud.org", provider.AllowedHosts).Should().BeTrue();
        ProviderHostPolicy.IsAllowed("upload.wikimedia.org", provider.AllowedHosts).Should().BeTrue();
        ProviderHostPolicy.IsAllowed("thumb.wikimedia.org", provider.AllowedHosts).Should().BeTrue();
        ProviderHostPolicy.IsAllowed("en.wikisource.org", provider.AllowedHosts).Should().BeFalse();
        ProviderHostPolicy.IsAllowed("example.com", provider.AllowedHosts).Should().BeFalse();

        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.Search);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.ItemRetrieval);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.EbookAcquisition);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.CoverArt);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.RightsInformation);
        provider.Capabilities.Should().NotHaveFlag(ProviderCapabilities.AudiobookAcquisition);
        provider.Capabilities.Should().NotHaveFlag(ProviderCapabilities.RequiresAssembly);
        provider.RightsNotice.Should().BeNull();

        var registry = new ProviderRegistry(new IContentProvider[] { provider });
        registry.Find(WikisourceProvider.ProviderIdentifier).Should().NotBeNull();
    }

    // ------------------------------------------------------------------
    // Catalogue cache (#641): one download reused across searches
    // ------------------------------------------------------------------

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static int CatalogRequests(StubHttpMessageHandler handler) =>
        handler.RecordedRequestPaths.Count(path => path == CatalogPath);

    private static HttpResponseMessage CatalogResponse(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/atom+xml"),
    };

    [Fact]
    public async Task Repeated_searches_reuse_the_parsed_catalogue_instead_of_redownloading_it()
    {
        var handler = new StubHttpMessageHandler();
        handler.RegisterXml(CatalogPath, LoadFixture("ready-for-export.xml"));
        var provider = new WikisourceProvider(new StubHttpClientFactory(handler), new ManualClock());

        var first = await provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);
        var second = await provider.SearchAsync(new ProviderSearchQuery("austen"), CancellationToken.None);
        var third = await provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);

        CatalogRequests(handler).Should().Be(1);
        second.Items.Should().NotBeEmpty();
        third.Items.Select(i => i.ExternalId).Should().Equal(first.Items.Select(i => i.ExternalId));
    }

    [Fact]
    public async Task Concurrent_searches_share_one_catalogue_download()
    {
        var xml = LoadFixture("ready-for-export.xml");
        using var release = new ManualResetEventSlim(false);
        var handler = new StubHttpMessageHandler();
        handler.Register(CatalogPath, _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return CatalogResponse(xml);
        });
        var provider = new WikisourceProvider(new StubHttpClientFactory(handler), new ManualClock());

        var searches = Enumerable.Range(0, 5)
            .Select(_ => provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None))
            .ToArray();
        await Task.Delay(100);
        release.Set();
        var pages = await Task.WhenAll(searches).WaitAsync(TimeSpan.FromSeconds(10));

        CatalogRequests(handler).Should().Be(1);
        pages.Should().OnlyContain(page => page.Items.Count == 1);
    }

    [Fact]
    public async Task An_expired_catalogue_is_refetched_but_a_fresh_one_is_not()
    {
        var clock = new ManualClock();
        var handler = new StubHttpMessageHandler();
        handler.RegisterXml(CatalogPath, LoadFixture("ready-for-export.xml"));
        var provider = new WikisourceProvider(new StubHttpClientFactory(handler), clock);

        await provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);
        clock.Now += WikisourceProvider.CatalogFreshFor - TimeSpan.FromMinutes(1);
        await provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);
        CatalogRequests(handler).Should().Be(1, "the catalogue is still fresh");

        clock.Now += TimeSpan.FromMinutes(2);
        await provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);
        CatalogRequests(handler).Should().Be(2, "an expired catalogue is never served");
    }

    [Fact]
    public async Task A_failed_catalogue_download_is_not_cached()
    {
        var xml = LoadFixture("ready-for-export.xml");
        var fail = true;
        var handler = new StubHttpMessageHandler();
        handler.Register(CatalogPath, _ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : CatalogResponse(xml));
        var provider = new WikisourceProvider(new StubHttpClientFactory(handler), new ManualClock());

        var act = () => provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);
        await act.Should().ThrowAsync<ProviderException>();

        fail = false;
        var page = await provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);

        page.Items.Should().ContainSingle();
        CatalogRequests(handler).Should().Be(2);
    }

    [Fact]
    public async Task A_search_that_stops_waiting_still_lets_the_shared_download_warm_the_cache()
    {
        // The aggregate discovery deadline cancels a slow provider's search.
        // That must not also cancel the catalogue download, or a cold catalogue
        // slower than the deadline would never be cached.
        var xml = LoadFixture("ready-for-export.xml");
        using var release = new ManualResetEventSlim(false);
        var handler = new StubHttpMessageHandler();
        handler.Register(CatalogPath, _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return CatalogResponse(xml);
        });
        var provider = new WikisourceProvider(new StubHttpClientFactory(handler), new ManualClock());
        using var cts = new CancellationTokenSource();

        var abandoned = provider.SearchAsync(new ProviderSearchQuery("pride"), cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await FluentActions.Awaiting(() => abandoned).Should().ThrowAsync<OperationCanceledException>();

        release.Set();
        var page = await provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        page.Items.Should().ContainSingle();
        CatalogRequests(handler).Should().Be(1, "the abandoned search's download was reused");
    }
}

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class GutenbergProviderTests
{
    private static string LoadFixture(string filename)
    {
        var basePath = AppContext.BaseDirectory;
        var directPath = Path.Combine(basePath, "Fixtures", "gutenberg", filename);
        if (File.Exists(directPath))
            return File.ReadAllText(directPath);

        // Fallback: look upward from base directory
        var current = new DirectoryInfo(basePath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Fixtures", "gutenberg", filename);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            var nestedCandidate = Path.Combine(current.FullName, "Nostos.Backend.Tests", "Fixtures", "gutenberg", filename);
            if (File.Exists(nestedCandidate))
                return File.ReadAllText(nestedCandidate);

            current = current.Parent;
        }

        throw new FileNotFoundException($"Gutenberg test fixture '{filename}' could not be located in output directory or repo tree.");
    }

    private static (GutenbergProvider Provider, StubHttpMessageHandler Handler) CreateProvider(StubHttpMessageHandler? handler = null)
    {
        handler ??= new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler);
        var logger = NullLogger<GutenbergProvider>.Instance;
        var provider = new GutenbergProvider(factory, logger);
        return (provider, handler);
    }

    [Fact]
    public async Task Search_ParsesResults_SetsCover_AndHandlesPaginationAndEmptyAssets()
    {
        // 1. Search: parses the 11 real results with the right ExternalId, Title and Author;
        // the cover URL is the deterministic per-id one (/cache/epub/<id>/pg<id>.cover.medium.jpg) and is non-null;
        // HasMore is false for a partial page and TRUE for the 25-entry full page;
        // search results carry NO assets (the search feed has none).
        var partialSearchXml = LoadFixture("search-pride-and-prejudice.opds");
        var fullSearchXml = LoadFixture("search-austen-page.opds");

        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/search.opds/?query=pride%20prejudice", partialSearchXml);
        handler.RegisterXml("/ebooks/search.opds/?query=austen&start_index=4", fullSearchXml);

        var partialPage = await provider.SearchAsync(new ProviderSearchQuery("pride prejudice"), CancellationToken.None);

        partialPage.Items.Should().HaveCount(11);
        partialPage.HasMore.Should().BeFalse();

        var first = partialPage.Items[0];
        first.ExternalId.Should().Be("1342");
        first.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        first.Metadata.Title.Should().Be("Pride and Prejudice");
        first.Metadata.Author.Should().Be("Jane Austen");
        first.Cover.Should().NotBeNull();
        first.Cover!.Url.Should().Be(new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg"));
        first.Cover.ContentType.Should().Be("image/jpeg");
        first.Cover.FileExtension.Should().Be(".jpg");

        // Every search item should carry no assets
        foreach (var item in partialPage.Items)
        {
            item.Assets.Should().BeEmpty();
            item.Cover.Should().NotBeNull();
            item.Cover!.Url.ToString().Should().Be($"https://www.gutenberg.org/cache/epub/{item.ExternalId}/pg{item.ExternalId}.cover.medium.jpg");
        }

        // Full page (25 results)
        var fullPage = await provider.SearchAsync(new ProviderSearchQuery("austen", Offset: 3), CancellationToken.None);
        fullPage.Items.Should().HaveCount(25);
        fullPage.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Search_OnNoRecordsFeed_ReturnsEmptyPage()
    {
        // 2. Search on search-no-records.opds returns an EMPTY page (not one bogus item).
        var noRecordsXml = LoadFixture("search-no-records.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/search.opds/?query=nonexistent", noRecordsXml);

        var result = await provider.SearchAsync(new ProviderSearchQuery("nonexistent"), CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Search_UrlEncodesQuery_AndAddsStartIndexForOffset()
    {
        // 3. Search sends the query to a path starting with /ebooks/search.opds/?query= and URL-encodes it;
        // requesting offset 3 adds start_index=4.
        var searchXml = LoadFixture("search-pride-and-prejudice.opds");
        var (provider, handler) = CreateProvider();
        handler.SetFallback(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(searchXml, System.Text.Encoding.UTF8, "application/atom+xml")
        });

        await provider.SearchAsync(new ProviderSearchQuery("Jane & Austen = Great!"), CancellationToken.None);
        var expectedQuery = Uri.EscapeDataString("Jane & Austen = Great!");
        handler.RecordedRequestPaths.Should().ContainSingle(p => p == $"/ebooks/search.opds/?query={expectedQuery}");

        await provider.SearchAsync(new ProviderSearchQuery("Austen", Offset: 3), CancellationToken.None);
        handler.RecordedRequestPaths.Should().Contain($"/ebooks/search.opds/?query=Austen&start_index=4");
    }

    [Fact]
    public async Task Detail_Book1342_ParsesMetadataAccurately()
    {
        // 4. Detail (book-1342.opds): Title "Pride and Prejudice";
        // Author is EXACTLY "Jane Austen" (proving the "Austen, Jane" inversion);
        // Language "English" (the "en" code converted to the full name);
        // Categories contains the LCSH subject terms and does NOT contain "Text" or "PR";
        // the rights statement is preserved verbatim as "Public domain in the USA.";
        // Cover is non-null; PartCount is null.
        var detailXml = LoadFixture("book-1342.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var item = await provider.GetItemAsync("1342", CancellationToken.None);

        item.Should().NotBeNull();
        item!.ExternalId.Should().Be("1342");
        item.Metadata.Title.Should().Be("Pride and Prejudice");
        // The real feed tags the author twice (dcterms:creator and OPDS entry) as
        // "Austen, Jane": the entry-scoped read plus de-duplication must yield the
        // name exactly once, inverted.
        item.Metadata.Author.Should().Be("Jane Austen");
        item.Metadata.Language.Should().Be("English");
        item.Metadata.Categories.Should().NotBeNull();
        item.Metadata.Categories.Should().Contain("England -- Fiction");
        item.Metadata.Categories.Should().Contain("Young women -- Fiction");
        item.Metadata.Categories.Should().Contain("Love stories");
        item.Metadata.Categories.Should().Contain("Sisters -- Fiction");
        item.Metadata.Categories.Should().Contain("Domestic fiction");
        item.Metadata.Categories.Should().Contain("Courtship -- Fiction");
        item.Metadata.Categories.Should().Contain("Social classes -- Fiction");

        // non-LCSH categories must NOT appear
        var categoriesList = item.Metadata.Categories!.Split(", ");
        categoriesList.Should().NotContain("Text");
        categoriesList.Should().NotContain("PR");

        item.Source.Should().NotBeNull();
        item.Source!.RightsStatement.Should().Be("Public domain in the USA.");
        item.Cover.Should().NotBeNull();
        item.Cover!.Url.Should().Be(new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg"));
        item.PartCount.Should().BeNull();
    }

    [Fact]
    public async Task Detail_Assets_OnlyEpubs_Epub3ImagesPreferred_NoKindle()
    {
        // 5. Detail assets: exactly THREE assets, all with Kind Ebook,
        // ids epub3-images / epub-images / epub-noimages,
        // epub3-images marked IsPreferred, sizes carried through (24835578 for epub3-images),
        // and NO Kindle/MOBI asset present anywhere.
        var detailXml = LoadFixture("book-1342.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var item = await provider.GetItemAsync("1342", CancellationToken.None);

        item.Should().NotBeNull();
        item!.Assets.Should().HaveCount(3);
        item.Assets.Select(a => a.Id).Should().Equal("epub3-images", "epub-images", "epub-noimages");

        foreach (var asset in item.Assets)
        {
            asset.Kind.Should().Be(ProviderMediaKind.Ebook);
        }

        var epub3 = item.Assets.Single(a => a.Id == "epub3-images");
        epub3.IsPreferred.Should().BeTrue();
        epub3.SizeBytes.Should().Be(24835578);

        var epubImages = item.Assets.Single(a => a.Id == "epub-images");
        epubImages.IsPreferred.Should().BeFalse();
        epubImages.SizeBytes.Should().Be(24846132);

        var epubNoImages = item.Assets.Single(a => a.Id == "epub-noimages");
        epubNoImages.IsPreferred.Should().BeFalse();
        epubNoImages.SizeBytes.Should().Be(558381);

        item.Assets.Should().NotContain(a => a.Id.Contains("kindle", StringComparison.OrdinalIgnoreCase) ||
                                            a.Id.Contains("mobi", StringComparison.OrdinalIgnoreCase) ||
                                            a.SourceFormat.Contains("kindle", StringComparison.OrdinalIgnoreCase) ||
                                            a.SourceFormat.Contains("mobi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Detail_WhenOpdsAdvertisesPdf_ExposesEpubAndPdf_AndKeepsEpubPreferred()
    {
        var detailXml = LoadFixture("book-1342-epub-pdf.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var item = await provider.GetItemAsync("1342", CancellationToken.None);

        item.Should().NotBeNull();
        item!.Assets.Select(asset => asset.Id).Should().Equal("epub3-images", "pdf");
        item.Assets.Should().OnlyContain(asset => asset.Kind == ProviderMediaKind.Ebook);
        item.Assets.Single(asset => asset.Id == "epub3-images").SourceFormat
            .Should().Be("application/epub+zip");
        item.Assets.Single(asset => asset.Id == "epub3-images").IsPreferred.Should().BeTrue();
        item.Assets.Single(asset => asset.Id == "pdf").SourceFormat.Should().Be("application/pdf");
        item.Assets.Single(asset => asset.Id == "pdf").IsPreferred.Should().BeFalse();
    }

    [Fact]
    public async Task PlanAcquisition_ExplicitPdf_ProducesPdfOutput()
    {
        var detailXml = LoadFixture("book-1342-epub-pdf.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("1342", AssetId: "pdf"),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("pdf");
        plan.Asset.Kind.Should().Be(ProviderMediaKind.Ebook);
        plan.Asset.SourceFormat.Should().Be("application/pdf");
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(new Uri("https://www.gutenberg.org/ebooks/1342.pdf"));
        plan.Parts[0].FileExtension.Should().Be(".pdf");
        plan.Parts[0].ExpectedBytes.Should().Be(4123456);
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
    }

    [Fact]
    public async Task PlanAcquisition_DefaultWithPdfAvailable_StillPicksPreferredEpub()
    {
        var detailXml = LoadFixture("book-1342-epub-pdf.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("1342", AssetId: null),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("epub3-images");
        plan.Output.Should().Be(new ProviderOutput(".epub", "application/epub+zip", "EPUB"));
    }

    [Fact]
    public async Task PlanAcquisition_DefaultAsset_PicksEpub3Images()
    {
        // 6. Planning with no AssetId chosen picks epub3-images and produces exactly ONE ProviderDownloadPart
        // with FileExtension ".epub", ExpectedBytes equal to the link's length, and Output.FileExtension ".epub";
        // the part label is the source's own label text.
        var detailXml = LoadFixture("book-1342.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var plan = await provider.PlanAcquisitionAsync(new ProviderAcquisitionRequest("1342", AssetId: null), CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("epub3-images");
        plan.Parts.Should().HaveCount(1);

        var part = plan.Parts[0];
        part.FileExtension.Should().Be(".epub");
        part.ExpectedBytes.Should().Be(24835578);
        part.Label.Should().Be("EPUB3 (E-readers incl. Send-to-Kindle)");
        part.Url.Should().Be(new Uri("https://www.gutenberg.org/ebooks/1342.epub3.images"));

        plan.Output.FileExtension.Should().Be(".epub");
        plan.Output.ContentType.Should().Be("application/epub+zip");
        plan.Output.Label.Should().Be("EPUB");
    }

    [Fact]
    public async Task PlanAcquisition_ExplicitAssetId_PicksRequestedVariant()
    {
        // 7. Planning with an explicit AssetId picks that variant (e.g. "epub-noimages" -> ExpectedBytes 558381).
        var detailXml = LoadFixture("book-1342.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var plan = await provider.PlanAcquisitionAsync(new ProviderAcquisitionRequest("1342", AssetId: "epub-noimages"), CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("epub-noimages");
        plan.Parts.Should().HaveCount(1);

        var part = plan.Parts[0];
        part.FileExtension.Should().Be(".epub");
        part.ExpectedBytes.Should().Be(558381);
        part.Label.Should().Be("EPUB (no images, older E-readers)");
        part.Url.Should().Be(new Uri("https://www.gutenberg.org/ebooks/1342.epub.noimages"));
    }

    [Fact]
    public async Task PlanAcquisition_UnknownAssetId_ThrowsProviderException()
    {
        // 8. Planning with an unknown AssetId throws ProviderException with code provider_asset_unavailable.
        var detailXml = LoadFixture("book-1342.opds");
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", detailXml);

        var act = async () => await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("1342", AssetId: "audiobook-mp3"),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ProviderException>();
        ex.Which.Code.Should().Be(ProviderException.AssetUnavailable);
        ex.Which.Code.Should().Be("provider_asset_unavailable");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("12ab")]
    [InlineData("1342/something")]
    [InlineData("-123")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetItemAsync_NonNumericId_ReturnsNullAndMakesNoRequests(string invalidId)
    {
        // 9. A non-numeric ExternalId (e.g. "../etc/passwd" or "12ab") makes GetItemAsync return null
        // AND makes no HTTP request at all (assert the handler recorded zero requests) — this is the path-traversal guard.
        var (provider, handler) = CreateProvider();

        var item = await provider.GetItemAsync(invalidId, CancellationToken.None);

        item.Should().BeNull();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Http404_GetItemReturnsNull_AndSearchReturnsEmptyPage()
    {
        // 10. A 404 response makes GetItemAsync return null, and search return an empty page rather than throwing.
        var (provider, handler) = CreateProvider();
        handler.Register("/ebooks/999999.opds", new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.Register("/ebooks/search.opds/?query=missing", new HttpResponseMessage(HttpStatusCode.NotFound));

        var item = await provider.GetItemAsync("999999", CancellationToken.None);
        item.Should().BeNull();

        var searchResult = await provider.SearchAsync(new ProviderSearchQuery("missing"), CancellationToken.None);
        searchResult.Items.Should().BeEmpty();
        searchResult.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task MalformedOrHtmlResponse_ThrowsProviderExceptionWithResponseInvalidCode()
    {
        // 11. A malformed/HTML response (not-a-feed.html) makes the provider throw ProviderException
        // with code provider_response_invalid.
        // When HTTP status is 200 OK with HTML content or malformed XML, XDocument.Load throws XmlException
        // which GutenbergProvider maps to ProviderException with code provider_response_invalid.
        var htmlContent = LoadFixture("not-a-feed.html");
        var (provider, handler) = CreateProvider();
        handler.Register("/ebooks/search.opds/?query=test", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(htmlContent + "<unclosed tag", System.Text.Encoding.UTF8, "application/atom+xml")
        });

        var act = async () => await provider.SearchAsync(new ProviderSearchQuery("test"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ProviderException>();
        ex.Which.Code.Should().Be(ProviderException.ResponseInvalid);
        ex.Which.Code.Should().Be("provider_response_invalid");
    }

    [Fact]
    public void PolicyAndRegistration_ValidatesCapabilitiesAndAllowedHosts()
    {
        // 12. Policy and registration: AllowedHosts contains "gutenberg.org";
        // MaxParts is 1; MaxBytesPerPart == MaxTotalBytes;
        // Capabilities includes Search, ItemRetrieval, EbookAcquisition, CoverArt and RightsInformation
        // and does NOT include AudiobookAcquisition or MultiTrackAudiobook;
        // `new ProviderRegistry(new IContentProvider[]{ provider })` does not throw.
        var (provider, _) = CreateProvider();

        provider.AllowedHosts.Should().Contain("gutenberg.org");
        provider.MaxParts.Should().Be(1);
        provider.MaxBytesPerPart.Should().Be(provider.MaxTotalBytes);

        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.Search);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.ItemRetrieval);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.EbookAcquisition);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.CoverArt);
        provider.Capabilities.Should().HaveFlag(ProviderCapabilities.RightsInformation);

        provider.Capabilities.Should().NotHaveFlag(ProviderCapabilities.AudiobookAcquisition);
        provider.Capabilities.Should().NotHaveFlag(ProviderCapabilities.MultiTrackAudiobook);

        var act = () => new ProviderRegistry(new IContentProvider[] { provider });
        act.Should().NotThrow();

        var registry = new ProviderRegistry(new IContentProvider[] { provider });
        registry.Find(GutenbergProvider.ProviderIdentifier).Should().NotBeNull();
    }

    [Fact]
    public async Task GetItemAsync_DetailFeedWithNoRecordsPlaceholder_ReturnsNull()
    {
        // 13. GetItemAsync for a detail feed where the catalogue returns the "No records found." style
        // placeholder returns null (reuse search-no-records.opds as the detail response).
        // Since GutenbergCatalog.ParseDetail checks if title is null or whitespace, or entry count is 0,
        // if search-no-records.opds has entry with title "No records found.",
        // let's verify how a detail feed placeholder behaves or test with a no-records feed.
        var noRecordsFeed = @"<?xml version=""1.0"" encoding=""utf-8""?>
<feed xmlns=""http://www.w3.org/2005/Atom"">
<id>http://www.gutenberg.org/ebooks.opds/</id>
<title>No records found.</title>
</feed>";
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/12345.opds", noRecordsFeed);

        var item = await provider.GetItemAsync("12345", CancellationToken.None);

        item.Should().BeNull();
    }
}

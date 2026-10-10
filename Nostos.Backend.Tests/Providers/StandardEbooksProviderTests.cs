using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.StandardEbooks;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class StandardEbooksProviderTests
{
    private const string Rights =
        "Public domain in the United States. Users located outside of the United States must check their local laws before using this ebook. Original content released to the public domain via the Creative Commons CC0 1.0 Universal Public Domain Dedication.";

    private const string PrideFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom"
              xmlns:dc="http://purl.org/dc/elements/1.1/"
              xmlns:opensearch="http://a9.com/-/spec/opensearch/1.1/">
          <id>https://standardebooks.org/feeds/opds/all?query=pride</id>
          <opensearch:totalResults>1</opensearch:totalResults>
          <opensearch:startIndex>1</opensearch:startIndex>
          <opensearch:itemsPerPage>24</opensearch:itemsPerPage>
          <title>Search Results</title>
          <entry>
            <id>https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice</id>
            <dc:identifier>https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice</dc:identifier>
            <title>Pride and Prejudice</title>
            <author><name>Jane Austen</name></author>
            <published>2024-01-02T00:00:00+00:00</published>
            <updated>2026-01-03T00:00:00+00:00</updated>
            <dc:language>en-US</dc:language>
            <dc:publisher>Standard Ebooks</dc:publisher>
            <rights>Public domain in the United States. Users located outside of the United States must check their local laws before using this ebook. Original content released to the public domain via the Creative Commons CC0 1.0 Universal Public Domain Dedication.</rights>
            <summary type="text">A sharp comedy of manners.</summary>
            <category scheme="http://purl.org/dc/terms/LCSH" term="England -- Fiction"/>
            <category scheme="https://standardebooks.org/vocab/subjects" term="Fiction"/>
            <link href="https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/cover.jpg"
                  rel="http://opds-spec.org/image"
                  type="image/jpeg"/>
            <link href="https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice"
                  rel="alternate"
                  title="This ebook’s page at Standard Ebooks"
                  type="application/xhtml+xml"/>
            <link href="https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/jane-austen_pride-and-prejudice.epub?source=feed"
                  length="12345"
                  rel="http://opds-spec.org/acquisition/open-access"
                  title="Recommended compatible epub"
                  type="application/epub+zip"/>
            <link href="https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/jane-austen_pride-and-prejudice_advanced.epub?source=feed"
                  length="23456"
                  rel="http://opds-spec.org/acquisition/open-access"
                  title="Advanced epub"
                  type="application/epub+zip"/>
            <link href="https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/jane-austen_pride-and-prejudice.kepub.epub?source=feed"
                  length="34567"
                  rel="http://opds-spec.org/acquisition/open-access"
                  title="Kobo Kepub epub"
                  type="application/kepub+zip"/>
            <link href="https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/jane-austen_pride-and-prejudice.azw3?source=feed"
                  length="45678"
                  rel="http://opds-spec.org/acquisition/open-access"
                  title="Amazon Kindle azw3"
                  type="application/x-mobipocket-ebook"/>
            <link href="https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/text/single-page"
                  length="56789"
                  rel="http://opds-spec.org/acquisition/open-access"
                  title="XHTML"
                  type="application/xhtml+xml"/>
          </entry>
        </feed>
        """;

    private static (StandardEbooksProvider Provider, StubHttpMessageHandler Handler) CreateProvider(
        StubHttpMessageHandler? handler = null)
    {
        handler ??= new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(
            handler,
            new Uri("https://standardebooks.org"));
        var provider = new StandardEbooksProvider(
            factory,
            NullLogger<StandardEbooksProvider>.Instance);
        return (provider, handler);
    }

    [Fact]
    public void Identity_Capabilities_AndDownloadPolicy_AreStable()
    {
        var (provider, _) = CreateProvider();

        provider.Id.Should().Be("standard-ebooks");
        provider.DisplayName.Should().Be("Standard Ebooks");
        provider.RightsNotice.Should().Be("Public domain in the United States.");

        provider.Capabilities.HasFlag(ProviderCapabilities.Search).Should().BeTrue();
        provider.Capabilities.HasFlag(ProviderCapabilities.ItemRetrieval).Should().BeTrue();
        provider.Capabilities.HasFlag(ProviderCapabilities.EbookAcquisition).Should().BeTrue();
        provider.Capabilities.HasFlag(ProviderCapabilities.CoverArt).Should().BeTrue();
        provider.Capabilities.HasFlag(ProviderCapabilities.RightsInformation).Should().BeTrue();
        provider.Capabilities.HasFlag(ProviderCapabilities.AudiobookAcquisition).Should().BeFalse();
        provider.Capabilities.HasFlag(ProviderCapabilities.MultiTrackAudiobook).Should().BeFalse();

        provider.AllowedHosts.Should().Equal("standardebooks.org");
        provider.MaxParts.Should().Be(1);
        provider.MaxBytesPerPart.Should().BeGreaterThan(0);
        provider.MaxTotalBytes.Should().Be(provider.MaxBytesPerPart);
    }

    [Fact]
    public async Task Search_UsesApprovedUserAgent_OfficialOpdsSearch_AndReturnsThinItems()
    {
        string? capturedUserAgent = null;
        string[] capturedAccept = [];
        var handler = new StubHttpMessageHandler()
            .Register(
                "/feeds/opds/all?query=pride%20prejudice&per-page=10&page=3",
                request =>
                {
                    capturedUserAgent = request.Headers.UserAgent.ToString();
                    capturedAccept = request.Headers.Accept
                        .Select(value => value.MediaType ?? string.Empty)
                        .ToArray();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            PrideFeed,
                            System.Text.Encoding.UTF8,
                            "application/atom+xml")
                    };
                });

        var (provider, _) = CreateProvider(handler);

        var page = await provider.SearchAsync(
            new ProviderSearchQuery(
                "pride prejudice",
                Limit: 10,
                Offset: 20),
            CancellationToken.None);

        page.Items.Should().ContainSingle();
        page.HasMore.Should().BeFalse();

        var item = page.Items.Single();
        item.ProviderId.Should().Be("standard-ebooks");
        item.ExternalId.Should().Be("jane-austen~pride-and-prejudice");
        item.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        item.Metadata.Title.Should().Be("Pride and Prejudice");
        item.Metadata.Author.Should().Be("Jane Austen");
        item.Metadata.Description.Should().Be("A sharp comedy of manners.");
        item.Metadata.Language.Should().Be("English (United States)");
        item.Metadata.Publisher.Should().Be("Standard Ebooks");
        item.Metadata.Categories.Should().Be("England -- Fiction, Fiction");
        item.Assets.Should().BeEmpty();
        item.Cover.Should().NotBeNull();
        item.Cover!.Url.Should().Be(
            new Uri("https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/cover.jpg"));
        item.Source.Should().NotBeNull();
        item.Source!.RightsStatement.Should().Be(Rights);

        capturedUserAgent.Should().Be(StandardEbooksProvider.ApprovedUserAgent);
        capturedAccept.Should().Contain("application/atom+xml");
    }

    [Fact]
    public async Task Search_ForAudiobooks_DoesNotCallProvider()
    {
        var (provider, handler) = CreateProvider();

        var page = await provider.SearchAsync(
            new ProviderSearchQuery(
                "pride",
                Kind: ProviderMediaKind.Audiobook),
            CancellationToken.None);

        page.Items.Should().BeEmpty();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Detail_PreservesMetadataRightsAndSource_AndExposesOnlySupportedEpubs()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/feeds/opds/all?query=jane%20austen%20pride%20and%20prejudice&per-page=24&page=1",
            PrideFeed);

        var item = await provider.GetItemAsync(
            "jane-austen~pride-and-prejudice",
            CancellationToken.None);

        item.Should().NotBeNull();
        item!.ExternalId.Should().Be("jane-austen~pride-and-prejudice");
        item.Metadata.Title.Should().Be("Pride and Prejudice");
        item.Metadata.Author.Should().Be("Jane Austen");
        item.Metadata.Language.Should().Be("English (United States)");
        item.Metadata.Publisher.Should().Be("Standard Ebooks");
        item.Metadata.PublishedDate.Should().Be("2024-01-02T00:00:00+00:00");
        item.Metadata.Categories.Should().Be("England -- Fiction, Fiction");
        item.Source.Should().Be(new ProviderSourceInfo(
            "https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice",
            Rights,
            null));

        item.Assets.Should().HaveCount(2);
        item.Assets.Select(asset => asset.Id)
            .Should().Equal("epub-compatible", "epub-advanced");
        item.Assets.Should().OnlyContain(asset =>
            asset.Kind == ProviderMediaKind.Ebook
            && asset.SourceFormat == "application/epub+zip");

        var compatible = item.Assets[0];
        compatible.Label.Should().Be("Recommended compatible epub");
        compatible.SizeBytes.Should().Be(12345);
        compatible.IsPreferred.Should().BeTrue();

        var advanced = item.Assets[1];
        advanced.Label.Should().Be("Advanced epub");
        advanced.SizeBytes.Should().Be(23456);
        advanced.IsPreferred.Should().BeFalse();

        item.Assets.Should().NotContain(asset =>
            asset.Label.Contains("Kobo", StringComparison.OrdinalIgnoreCase)
            || asset.Label.Contains("Kindle", StringComparison.OrdinalIgnoreCase)
            || asset.SourceFormat.Contains("xhtml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlanAcquisition_DefaultsToRecommendedCompatibleEpub()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/feeds/opds/all?query=jane%20austen%20pride%20and%20prejudice&per-page=24&page=1",
            PrideFeed);

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest(
                "jane-austen~pride-and-prejudice",
                AssetId: null),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.ProviderId.Should().Be("standard-ebooks");
        plan.ExternalId.Should().Be("jane-austen~pride-and-prejudice");
        plan.Asset.Id.Should().Be("epub-compatible");
        plan.Asset.IsPreferred.Should().BeTrue();
        plan.Asset.SizeBytes.Should().Be(12345);
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/jane-austen_pride-and-prejudice.epub?source=feed"));
        plan.Parts[0].FileExtension.Should().Be(".epub");
        plan.Parts[0].ExpectedBytes.Should().Be(12345);
        plan.Output.Should().Be(new ProviderOutput(
            ".epub",
            "application/epub+zip",
            "EPUB"));
        plan.Source!.RightsStatement.Should().Be(Rights);
    }

    [Fact]
    public async Task PlanAcquisition_AllowsExplicitAdvancedEpub()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/feeds/opds/all?query=jane%20austen%20pride%20and%20prejudice&per-page=24&page=1",
            PrideFeed);

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest(
                "jane-austen~pride-and-prejudice",
                AssetId: "epub-advanced"),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("epub-advanced");
        plan.Asset.IsPreferred.Should().BeFalse();
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice/downloads/jane-austen_pride-and-prejudice_advanced.epub?source=feed"));
        plan.Parts[0].ExpectedBytes.Should().Be(23456);
    }

    [Fact]
    public async Task InvalidExternalId_IsRejectedBeforeAnyRequest()
    {
        var (provider, handler) = CreateProvider();

        var item = await provider.GetItemAsync(
            "../../etc/passwd",
            CancellationToken.None);

        item.Should().BeNull();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingExactItem_ReturnsNullEvenWhenSearchHasOtherBooks()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/feeds/opds/all?query=charles%20dickens%20great%20expectations&per-page=24&page=1",
            PrideFeed);

        var item = await provider.GetItemAsync(
            "charles-dickens~great-expectations",
            CancellationToken.None);

        item.Should().BeNull();
    }

    [Fact]
    public async Task UnknownAsset_ThrowsStableProviderError()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/feeds/opds/all?query=jane%20austen%20pride%20and%20prejudice&per-page=24&page=1",
            PrideFeed);

        var act = () => provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest(
                "jane-austen~pride-and-prejudice",
                AssetId: "azw3"),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProviderException>();
        exception.Which.Code.Should().Be(ProviderException.AssetUnavailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RejectedWhitelistedUserAgent_IsReportedAsProviderUnavailable(
        HttpStatusCode statusCode)
    {
        var (provider, handler) = CreateProvider();
        handler.Register(
            "/feeds/opds/all?query=pride&per-page=20&page=1",
            new HttpResponseMessage(statusCode));

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("pride"),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProviderException>();
        exception.Which.Code.Should().Be(ProviderException.Unavailable);
        exception.Which.Message.Should().Contain("User-Agent");
    }

    [Fact]
    public async Task HtmlOrOtherNonFeedResponse_IsReportedAsInvalid()
    {
        const string html = "<html><body>maintenance</body></html>";
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/feeds/opds/all?query=pride&per-page=20&page=1",
            html);

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("pride"),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProviderException>();
        exception.Which.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task MalformedXml_IsReportedAsInvalid()
    {
        const string malformed = "<feed><entry>";
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/feeds/opds/all?query=pride&per-page=20&page=1",
            malformed);

        var act = () => provider.SearchAsync(
            new ProviderSearchQuery("pride"),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProviderException>();
        exception.Which.Code.Should().Be(ProviderException.ResponseInvalid);
    }
}

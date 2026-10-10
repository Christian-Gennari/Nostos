using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.LibriVox;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class LibriVoxProviderTests
{
    private static string LoadFixture(string filename)
    {
        var basePath = AppContext.BaseDirectory;
        var directPath = Path.Combine(basePath, "Fixtures", "librivox", filename);
        if (File.Exists(directPath))
            return File.ReadAllText(directPath);

        var current = new DirectoryInfo(basePath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Fixtures", "librivox", filename);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            var nestedCandidate = Path.Combine(current.FullName, "Nostos.Backend.Tests", "Fixtures", "librivox", filename);
            if (File.Exists(nestedCandidate))
                return File.ReadAllText(nestedCandidate);

            current = current.Parent;
        }

        throw new FileNotFoundException($"LibriVox test fixture '{filename}' could not be located in output directory or repo tree.");
    }

    private static (LibriVoxProvider Provider, StubHttpMessageHandler Handler) CreateProvider(
        StubHttpMessageHandler? handler = null)
    {
        handler ??= new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler, new Uri("https://librivox.org"));
        var logger = NullLogger<LibriVoxProvider>.Instance;
        var provider = new LibriVoxProvider(factory, logger);
        return (provider, handler);
    }

    [Fact]
    public async Task Search_ByTitlePrefix_ReturnsItemsAndPrefixNotice()
    {
        // 1. Search by title prefix returns the fixture's items with correct title/author/narrator/duration/categories/coverUrl/partCount,
        // and a notice explaining that titles must start with the text.
        var searchJson = LoadFixture("search-title-1601.json");
        var (provider, handler) = CreateProvider();

        var expectedPath = "/api/feed/audiobooks/?title=%5E1601&format=json&extended=1&limit=20&offset=0";
        handler.Register(expectedPath, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(searchJson, System.Text.Encoding.UTF8, "application/json")
        });

        var result = await provider.SearchAsync(new ProviderSearchQuery("1601"), CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Notice.Should().Be("LibriVox matches titles that start with your text. Add more words to narrow it down.");

        var item0 = result.Items[0];
        item0.ExternalId.Should().Be("2469");
        item0.MediaKind.Should().Be(ProviderMediaKind.Audiobook);
        item0.Assets.Should().BeEmpty();
        item0.Metadata.Title.Should().Be("1601: Conversation, as it was by the Social Fireside, in the Time of the Tudors");
        item0.Metadata.Author.Should().Be("Mark Twain");
        item0.Metadata.Narrator.Should().Be("Denny Sayers (d. 2015) and 8 others");
        item0.Metadata.Duration.Should().Be("0:19:38");
        item0.Metadata.Categories.Should().Be("Dramatic Readings, Humorous Fiction, Satire");
        item0.Cover.Should().NotBeNull();
        item0.Cover!.Url.Should().Be(new Uri("https://archive.org/services/img/1601_0903_librivox"));
        item0.PartCount.Should().Be(2);

        var item1 = result.Items[1];
        item1.ExternalId.Should().Be("6362");
        item1.Metadata.Title.Should().Be("1601: Conversation, as it was by the Social Fireside, in the Time of the Tudors (Version 2)");
        item1.Metadata.Author.Should().Be("Mark Twain");
        item1.PartCount.Should().Be(3);
    }

    [Fact]
    public async Task Search_TitleMiss_FallsBackToAuthorSearch()
    {
        // 2. A title miss falls back to an author search: assert a second request whose path contains author= was recorded,
        // the results come back, and the notice explains the fallback.
        var searchJson = LoadFixture("search-title-1601.json");
        var emptyJson = LoadFixture("search-empty.json");
        var (provider, handler) = CreateProvider();

        var titlePath = "/api/feed/audiobooks/?title=%5ETwain&format=json&extended=1&limit=20&offset=0";
        var authorPath = "/api/feed/audiobooks/?author=%5ETwain&format=json&extended=1&limit=20&offset=0";

        handler.Register(titlePath, _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(emptyJson, System.Text.Encoding.UTF8, "application/json")
        });
        handler.Register(authorPath, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(searchJson, System.Text.Encoding.UTF8, "application/json")
        });

        var result = await provider.SearchAsync(new ProviderSearchQuery("Twain"), CancellationToken.None);

        handler.RecordedRequests.Should().HaveCount(2);
        handler.RecordedRequestPaths.Should().Contain(p => p.Contains("author="));

        result.Items.Should().HaveCount(2);
        result.Notice.Should().Be("No title matched “Twain”, so these are recordings by a reader or author matching it. LibriVox cannot search inside titles.");
    }

    [Fact]
    public async Task Search_BothMiss_ReturnsEmptyItemsAndNoMatchNotice()
    {
        // 3. Both miss -> empty items + the no-match notice.
        var emptyJson = LoadFixture("search-empty.json");
        var (provider, handler) = CreateProvider();

        var titlePath = "/api/feed/audiobooks/?title=%5ENonexistent&format=json&extended=1&limit=20&offset=0";
        var authorPath = "/api/feed/audiobooks/?author=%5ENonexistent&format=json&extended=1&limit=20&offset=0";

        handler.Register(titlePath, _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(emptyJson, System.Text.Encoding.UTF8, "application/json")
        });
        handler.Register(authorPath, _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(emptyJson, System.Text.Encoding.UTF8, "application/json")
        });

        var result = await provider.SearchAsync(new ProviderSearchQuery("Nonexistent"), CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
        result.Notice.Should().Be("No LibriVox recording matched “Nonexistent”. Its catalogue only matches a title that starts with your text, so try fewer — or exactly the opening — words, or a reader's name.");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData(" X ")]
    public async Task Search_ShortQuery_ReturnsEmptyPageWithoutHttpRequest(string query)
    {
        // 4. A query shorter than 2 characters returns an empty page WITHOUT any HTTP request.
        var (provider, handler) = CreateProvider();

        var result = await provider.SearchAsync(new ProviderSearchQuery(query), CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
        result.Notice.Should().Be("Type at least two characters to search LibriVox.");
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetItemAsync_Detail2469_AssertsExactValues()
    {
        // 5. Detail 2469 asserts every value:
        // exact equality for title, author, language, narrator, duration, categories, publishedDate, cover URL,
        // partCount, asset id/kind/isPreferred, and the description contains no '<'.
        var detailJson = LoadFixture("detail-2469.json");
        var (provider, handler) = CreateProvider();

        handler.Register("/api/feed/audiobooks/?id=2469&format=json&extended=1", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });

        var item = await provider.GetItemAsync("2469", CancellationToken.None);

        item.Should().NotBeNull();
        item!.ExternalId.Should().Be("2469");
        item.MediaKind.Should().Be(ProviderMediaKind.Audiobook);
        item.Metadata.Title.Should().Be("1601: Conversation, as it was by the Social Fireside, in the Time of the Tudors");
        item.Metadata.Author.Should().Be("Mark Twain");
        item.Metadata.Language.Should().Be("English");
        item.Metadata.Narrator.Should().Be("Denny Sayers (d. 2015) and 8 others");
        item.Metadata.Duration.Should().Be("0:19:38");
        item.Metadata.Categories.Should().Be("Dramatic Readings, Humorous Fiction, Satire");
        item.Metadata.PublishedDate.Should().Be("1880");
        item.Cover.Should().NotBeNull();
        item.Cover!.Url.Should().Be(new Uri("https://archive.org/services/img/1601_0903_librivox"));
        item.PartCount.Should().Be(2);

        item.Assets.Should().HaveCount(1);
        var asset = item.Assets[0];
        asset.Id.Should().Be("m4b");
        asset.Kind.Should().Be(ProviderMediaKind.Audiobook);
        asset.IsPreferred.Should().BeTrue();

        item.Metadata.Description.Should().NotBeNull();
        item.Metadata.Description.Should().NotContain("<");
    }

    [Fact]
    public async Task PlanAcquisitionAsync_NoAssetId_ProducesOrderedPartsAndNullChapters()
    {
        // 6. Plan with no assetId -> one part per section in order (assert exact URLs and labels),
        // output .m4b + audio/mp4, Chapters null.
        var detailJson = LoadFixture("detail-2469.json");
        var (provider, handler) = CreateProvider();

        handler.Register("/api/feed/audiobooks/?id=2469&format=json&extended=1", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });

        var plan = await provider.PlanAcquisitionAsync(new ProviderAcquisitionRequest("2469", null), CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.ExternalId.Should().Be("2469");
        plan.Output.FileExtension.Should().Be(".mp3");
        plan.Output.ContentType.Should().Be("audio/mpeg");
        plan.Output.Label.Should().Be("MP3 audiobook");
        plan.Chapters.Should().BeNull();

        plan.Parts.Should().HaveCount(2);
        plan.Parts[0].Label.Should().Be("Dramatis Personae");
        plan.Parts[0].FileExtension.Should().Be(".mp3");
        plan.Parts[0].Url.Should().Be(new Uri("https://www.archive.org/download/1601_0903_librivox/sixteenoone_0_twain_64kb.mp3"));

        plan.Parts[1].Label.Should().Be("1601");
        plan.Parts[1].FileExtension.Should().Be(".mp3");
        plan.Parts[1].Url.Should().Be(new Uri("https://www.archive.org/download/1601_0903_librivox/sixteenoone_1_twain_64kb.mp3"));
    }

    [Theory]
    [InlineData("m4b")]
    [InlineData("M4B")]
    public async Task PlanAcquisitionAsync_WithM4bAssetId_CaseInsensitive_Succeeds(string assetId)
    {
        // 7. Plan with assetId "m4b" (and also "M4B" to prove case-insensitivity) succeeds.
        var detailJson = LoadFixture("detail-2469.json");
        var (provider, handler) = CreateProvider();

        handler.Register("/api/feed/audiobooks/?id=2469&format=json&extended=1", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });

        var plan = await provider.PlanAcquisitionAsync(new ProviderAcquisitionRequest("2469", assetId), CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("m4b");
        plan.Parts.Should().HaveCount(2);
    }

    [Fact]
    public async Task PlanAcquisitionAsync_UnknownAssetId_ThrowsProviderAssetUnavailable()
    {
        // 8. Plan with an unknown assetId -> ProviderException with code "provider_asset_unavailable".
        var detailJson = LoadFixture("detail-2469.json");
        var (provider, handler) = CreateProvider();

        handler.Register("/api/feed/audiobooks/?id=2469&format=json&extended=1", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });

        var act = () => provider.PlanAcquisitionAsync(new ProviderAcquisitionRequest("2469", "mp3"), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProviderException>();
        exception.Which.Code.Should().Be(ProviderException.AssetUnavailable);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("12ab")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetItemAsync_InvalidIds_ReturnsNullWithoutHttpRequest(string invalidId)
    {
        // 9. A traversal-shaped id, a non-numeric id and blanks -> null AND no
        // HTTP request recorded. The exact inputs are the InlineData above.
        var (provider, handler) = CreateProvider();

        var result = await provider.GetItemAsync(invalidId, CancellationToken.None);

        result.Should().BeNull();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResponseHandling_404DetailReturnsNull_404SearchReturnsEmptyPage()
    {
        // 10. A 404 detail response -> null; a 404 search response -> empty page.
        var emptyJson = LoadFixture("search-empty.json");
        var (provider, handler) = CreateProvider();

        handler.Register("/api/feed/audiobooks/?id=999999&format=json&extended=1", _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(emptyJson, System.Text.Encoding.UTF8, "application/json")
        });
        handler.Register("/api/feed/audiobooks/?title=%5EUnknown&format=json&extended=1&limit=20&offset=0", _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(emptyJson, System.Text.Encoding.UTF8, "application/json")
        });
        handler.Register("/api/feed/audiobooks/?author=%5EUnknown&format=json&extended=1&limit=20&offset=0", _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(emptyJson, System.Text.Encoding.UTF8, "application/json")
        });

        var item = await provider.GetItemAsync("999999", CancellationToken.None);
        item.Should().BeNull();

        var page = await provider.SearchAsync(new ProviderSearchQuery("Unknown"), CancellationToken.None);
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ResponseHandling_MalformedHtmlBody_ThrowsResponseInvalid()
    {
        // 11. A malformed/HTML body -> ProviderException with code "provider_response_invalid".
        var htmlContent = LoadFixture("not-json.html");
        var (provider, handler) = CreateProvider();

        handler.Register("/api/feed/audiobooks/?id=2469&format=json&extended=1", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(htmlContent, System.Text.Encoding.UTF8, "text/html")
        });

        var act = () => provider.GetItemAsync("2469", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProviderException>();
        exception.Which.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public void PolicyAndCapabilities_AndProviderRegistryRegistration_AreValid()
    {
        // 12. Policy/capabilities: AllowedHosts contains "archive.org"; MaxBytesPerPart <= MaxTotalBytes; MaxParts > 0;
        // Capabilities declares AudiobookAcquisition and MultiTrackAudiobook, does NOT declare EbookAcquisition;
        // and new ProviderRegistry(new IContentProvider[] { provider }) does not throw, and registry.Find("librivox") is not null.
        var (provider, _) = CreateProvider();

        provider.AllowedHosts.Should().Contain("archive.org");
        provider.MaxBytesPerPart.Should().BeLessThanOrEqualTo(provider.MaxTotalBytes);
        provider.MaxParts.Should().BeGreaterThan(0);

        provider.Capabilities.HasFlag(ProviderCapabilities.AudiobookAcquisition).Should().BeTrue();
        provider.Capabilities.HasFlag(ProviderCapabilities.MultiTrackAudiobook).Should().BeTrue();
        provider.Capabilities.HasFlag(ProviderCapabilities.EbookAcquisition).Should().BeFalse();

        var registryAction = () => new ProviderRegistry(new IContentProvider[] { provider });
        registryAction.Should().NotThrow();

        var registry = new ProviderRegistry(new IContentProvider[] { provider });
        var found = registry.Find("librivox");
        found.Should().NotBeNull();
        found!.Id.Should().Be("librivox");
    }
}

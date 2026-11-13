using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// Pins the observable behaviour of <see cref="GutenbergProvider"/> before it is
/// split into focused collaborators (#1597): the provider's own contract, the
/// mapping of the recorded OPDS feeds, the acquisition choice for an ebook, and
/// every failure code it surfaces. These tests must stay green, unchanged,
/// across the split.
/// </summary>
public sealed class GutenbergProviderCharacterizationTests
{
    private const string SnapshotPath = "/cache/epub/feeds/rdf-files.tar.zip";
    private static readonly DateTimeOffset LastModified = new(2026, 10, 6, 14, 41, 27, TimeSpan.Zero);

    private const string PrideRdf = """
        <?xml version="1.0" encoding="utf-8"?>
        <rdf:RDF xml:base="http://www.gutenberg.org/"
          xmlns:dcterms="http://purl.org/dc/terms/"
          xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
          xmlns:pgterms="http://www.gutenberg.org/2009/pgterms/"
          xmlns:dcam="http://purl.org/dc/dcam/">
          <pgterms:ebook rdf:about="ebooks/1342">
            <dcterms:title>Pride and Prejudice</dcterms:title>
            <dcterms:creator>
              <pgterms:agent rdf:about="2009/agents/68">
                <pgterms:name>Austen, Jane</pgterms:name>
              </pgterms:agent>
            </dcterms:creator>
            <dcterms:language>
              <rdf:Description rdf:nodeID="Nlanguage">
                <rdf:value rdf:datatype="http://purl.org/dc/terms/RFC4646">en</rdf:value>
              </rdf:Description>
            </dcterms:language>
            <dcterms:type>
              <rdf:Description rdf:nodeID="Ntype">
                <rdf:value>Text</rdf:value>
              </rdf:Description>
            </dcterms:type>
            <dcterms:rights>Public domain in the USA.</dcterms:rights>
          </pgterms:ebook>
        </rdf:RDF>
        """;

    private static (GutenbergProvider Provider, StubHttpMessageHandler Handler) CreateProvider()
    {
        var handler = new StubHttpMessageHandler();
        var provider = new GutenbergProvider(
            new StubHttpClientFactory(handler),
            NullLogger<GutenbergProvider>.Instance)
        {
            CatalogueRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        };

        return (provider, handler);
    }

    private static byte[] BuildSnapshotArchive(params (string Name, string Content)[] entries)
    {
        using var tarBuffer = new MemoryStream();
        using (var tar = new TarWriter(tarBuffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("rdf-files.tar", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            tarBuffer.Position = 0;
            tarBuffer.CopyTo(stream);
        }

        return buffer.ToArray();
    }

    private static HttpResponseMessage ArchiveResponse(byte[] archive)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
        };
        response.Content.Headers.LastModified = LastModified;
        return response;
    }

    private static async Task<List<ProviderItem>> DrainAsync(IAsyncEnumerable<ProviderItem> items)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items)
            result.Add(item);

        return result;
    }

    [Fact]
    public async Task Search_OnTheRecordedFeed_ProducesThinItemsWithDeterministicCovers()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/ebooks/search.opds/?query=pride%20prejudice",
            GutenbergFixtures.Load("search-pride-and-prejudice.opds"));

        var page = await provider.SearchAsync(new ProviderSearchQuery("pride prejudice"), CancellationToken.None);

        page.Items.Should().HaveCount(11);
        page.HasMore.Should().BeFalse();
        page.Notice.Should().BeNull();

        var first = page.Items[0];
        first.ProviderId.Should().Be("gutenberg");
        first.ExternalId.Should().Be("1342");
        first.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        first.Metadata.Title.Should().Be("Pride and Prejudice");
        first.Metadata.Author.Should().Be("Jane Austen");
        first.Metadata.Description.Should().BeNull();
        first.Metadata.Language.Should().BeNull();
        first.Metadata.Categories.Should().BeNull();
        first.Assets.Should().BeEmpty("the search feed carries no acquisition links");
        first.Cover.Should().Be(new ProviderCover(
            new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg"),
            "image/jpeg",
            ".jpg"));
        first.Source.Should().Be(new ProviderSourceInfo(
            ItemUrl: "https://www.gutenberg.org/ebooks/1342",
            RightsStatement: null,
            RightsUrl: "https://www.gutenberg.org/policy/terms_of_use.html"));
        first.PartCount.Should().BeNull();

        handler.RecordedRequestPaths.Should().ContainSingle()
            .Which.Should().Be("/ebooks/search.opds/?query=pride%20prejudice");
    }

    [Fact]
    public async Task Search_OnAFullPage_ReportsThatMoreResultsExist()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/ebooks/search.opds/?query=austen&start_index=4",
            GutenbergFixtures.Load("search-austen-page.opds"));

        var page = await provider.SearchAsync(new ProviderSearchQuery("austen", Offset: 3), CancellationToken.None);

        page.Items.Should().HaveCount(25);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Search_OnTheNoRecordsPlaceholder_IsAnEmptyPage()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml(
            "/ebooks/search.opds/?query=nonexistent",
            GutenbergFixtures.Load("search-no-records.opds"));

        var page = await provider.SearchAsync(new ProviderSearchQuery("nonexistent"), CancellationToken.None);

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Detail_OnTheRecordedFeed_ProducesMetadataRightsAndAssets()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", GutenbergFixtures.Load("book-1342.opds"));

        var item = await provider.GetItemAsync("1342", CancellationToken.None);

        item.Should().NotBeNull();
        item!.ProviderId.Should().Be("gutenberg");
        item.ExternalId.Should().Be("1342");
        item.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        item.Metadata.Title.Should().Be("Pride and Prejudice");
        item.Metadata.Author.Should().Be("Jane Austen");
        item.Metadata.Language.Should().Be("English");
        item.Metadata.Description.Should().StartWith("\"Pride and Prejudice\" by Jane Austen is a novel published in 1813.");
        item.Metadata.Subtitle.Should().BeNull();
        item.Metadata.Publisher.Should().BeNull();
        item.Metadata.PublishedDate.Should().BeNull();
        item.Metadata.Categories.Should().Be(
            "England -- Fiction, Young women -- Fiction, Love stories, Sisters -- Fiction, "
            + "Domestic fiction, Courtship -- Fiction, Social classes -- Fiction");
        item.Assets.Select(asset => asset.Id).Should().Equal("epub3-images", "epub-images", "epub-noimages");
        item.Assets.Should().OnlyContain(asset => asset.Kind == ProviderMediaKind.Ebook);
        item.Assets.Single(asset => asset.Id == "epub3-images").IsPreferred.Should().BeTrue();
        item.Assets.Single(asset => asset.Id == "epub3-images").SizeBytes.Should().Be(24835578);
        item.Assets.Single(asset => asset.Id == "epub-images").SizeBytes.Should().Be(24846132);
        item.Assets.Single(asset => asset.Id == "epub-noimages").SizeBytes.Should().Be(558381);
        item.Cover.Should().Be(new ProviderCover(
            new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg"),
            "image/jpeg",
            ".jpg"));
        item.Source.Should().Be(new ProviderSourceInfo(
            ItemUrl: "https://www.gutenberg.org/ebooks/1342",
            RightsStatement: "Public domain in the USA.",
            RightsUrl: "https://www.gutenberg.org/policy/terms_of_use.html"));
        item.PartCount.Should().BeNull();
    }

    [Fact]
    public async Task PlanAcquisition_WithNoAssetChosen_PlansThePreferredEpub()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", GutenbergFixtures.Load("book-1342.opds"));

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("1342", AssetId: null),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.ProviderId.Should().Be("gutenberg");
        plan.ExternalId.Should().Be("1342");
        plan.Asset.Should().Be(new ProviderAsset(
            "epub3-images",
            ProviderMediaKind.Ebook,
            "EPUB3 (E-readers incl. Send-to-Kindle)",
            "application/epub+zip",
            24835578,
            IsPreferred: true));
        plan.Metadata.Title.Should().Be("Pride and Prejudice");
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(new Uri("https://www.gutenberg.org/ebooks/1342.epub3.images"));
        plan.Parts[0].FileExtension.Should().Be(".epub");
        plan.Parts[0].ExpectedBytes.Should().Be(24835578);
        plan.Parts[0].Label.Should().Be("EPUB3 (E-readers incl. Send-to-Kindle)");
        plan.Output.Should().Be(new ProviderOutput(".epub", "application/epub+zip", "EPUB"));
        plan.Cover.Should().Be(new ProviderCover(
            new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg"),
            "image/jpeg",
            ".jpg"));
        plan.Source.Should().Be(new ProviderSourceInfo(
            ItemUrl: "https://www.gutenberg.org/ebooks/1342",
            RightsStatement: "Public domain in the USA.",
            RightsUrl: "https://www.gutenberg.org/policy/terms_of_use.html"));
    }

    [Fact]
    public async Task PlanAcquisition_WithAnExplicitPdf_PlansThatFormat()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", GutenbergFixtures.Load("book-1342-epub-pdf.opds"));

        var plan = await provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("1342", AssetId: "pdf"),
            CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Asset.Id.Should().Be("pdf");
        plan.Asset.SourceFormat.Should().Be("application/pdf");
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(new Uri("https://www.gutenberg.org/ebooks/1342.pdf"));
        plan.Parts[0].ExpectedBytes.Should().Be(4123456);
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
    }

    [Fact]
    public async Task PlanAcquisition_ForAnUnknownAsset_IsAssetUnavailable()
    {
        var (provider, handler) = CreateProvider();
        handler.RegisterXml("/ebooks/1342.opds", GutenbergFixtures.Load("book-1342.opds"));

        var act = () => provider.PlanAcquisitionAsync(
            new ProviderAcquisitionRequest("1342", AssetId: "audiobook-mp3"),
            CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.AssetUnavailable);
        error.Message.Should().Be("gutenberg item 1342 does not offer asset 'audiobook-mp3'.");
    }

    [Fact]
    public async Task MissingItems_AreAnEmptySearchAndANullDetail()
    {
        var (provider, handler) = CreateProvider();
        handler.Register("/ebooks/search.opds/?query=missing", new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.Register("/ebooks/999999.opds", new HttpResponseMessage(HttpStatusCode.Gone));

        var page = await provider.SearchAsync(new ProviderSearchQuery("missing"), CancellationToken.None);
        var item = await provider.GetItemAsync("999999", CancellationToken.None);

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        item.Should().BeNull();
    }

    [Fact]
    public async Task Search_OnAServerError_IsUnavailableAndNamesTheStatus()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(
            "/ebooks/search.opds/?query=pride",
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.SearchAsync(new ProviderSearchQuery("pride"), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
        error.Message.Should().Be("The gutenberg source is unavailable: the catalogue answered HTTP 503");
    }

    [Fact]
    public async Task Search_OnANonXmlBody_IsAResponseInvalid()
    {
        var (provider, handler) = CreateProvider();
        handler.Register("/ebooks/search.opds/?query=test", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                GutenbergFixtures.Load("not-a-feed.html") + "<unclosed tag",
                Encoding.UTF8,
                "application/atom+xml"),
        });

        var act = () => provider.SearchAsync(new ProviderSearchQuery("test"), CancellationToken.None);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task Snapshot_OnANotModifiedProbe_ReportsUnchangedWithoutDownloading()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var snapshot = await provider.ReadAsync(
            new ProviderSnapshotRequest(ETag: "\"snapshot-1\"", IfModifiedSince: LastModified),
            CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.NotModified);
        snapshot.ETag.Should().Be("\"snapshot-1\"");
        snapshot.LastModified.Should().Be(LastModified);
        (await DrainAsync(snapshot.Items)).Should().BeEmpty();
        handler.RecordedRequests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Head);
    }

    [Fact]
    public async Task Snapshot_OnAnUpdatedArchive_EnumeratesTheCatalogue()
    {
        var archive = BuildSnapshotArchive(("cache/epub/1342/pg1342.rdf", PrideRdf));
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, _ => ArchiveResponse(archive));

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        var items = await DrainAsync(snapshot.Items);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        snapshot.LastModified.Should().Be(LastModified);
        snapshot.NextCursor.Should().BeNull();
        items.Should().ContainSingle();
        items[0].ExternalId.Should().Be("1342");
        items[0].Metadata.Title.Should().Be("Pride and Prejudice");
        items[0].Metadata.Author.Should().Be("Jane Austen");
        items[0].Metadata.Language.Should().Be("English");
        items[0].Assets.Should().BeEmpty();
    }

    [Fact]
    public async Task Snapshot_OnAFailedDownload_IsUnavailable()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(SnapshotPath, request => request.Method == HttpMethod.Head
            ? ArchiveResponse(BuildSnapshotArchive(("cache/epub/1342/pg1342.rdf", PrideRdf)))
            : new HttpResponseMessage(HttpStatusCode.BadGateway));

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        var act = () => DrainAsync(snapshot.Items);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.Unavailable);
    }
}

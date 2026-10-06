using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Wikisource;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// Contract tests for the bulk snapshot seam (#776 P0-A). The Wikisource feed
/// is the whole catalog, so a read is either "here it is" or 304; both must
/// carry the validators a host persists for its next conditional read.
/// </summary>
public sealed class WikisourceSnapshotSourceTests
{
    private const string CatalogPath = "/opds/en/Ready_for_export.xml";
    private const string Etag = "\"ws-export-20260925\"";
    private static readonly DateTimeOffset LastModified = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

    private static string LoadFixture(string filename)
    {
        var basePath = AppContext.BaseDirectory;
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
        return (new WikisourceProvider(new StubHttpClientFactory(handler)), handler);
    }

    private static HttpResponseMessage FeedResponse(string xml, string? etag = null, DateTimeOffset? lastModified = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/atom+xml"),
        };

        if (etag is not null)
            response.Headers.ETag = new EntityTagHeaderValue(etag);

        if (lastModified is not null)
            response.Content.Headers.LastModified = lastModified;

        return response;
    }

    private static async Task<List<ProviderItem>> ReadItemsAsync(IAsyncEnumerable<ProviderItem> items)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items)
            result.Add(item);

        return result;
    }

    [Fact]
    public async Task Read_returns_the_full_snapshot_with_card_metadata_cover_and_validators()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(CatalogPath, _ => FeedResponse(LoadFixture("ready-for-export.xml"), Etag, LastModified));

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        snapshot.ETag.Should().Be(Etag);
        snapshot.LastModified.Should().Be(LastModified);
        snapshot.NextCursor.Should().BeNull("the Wikisource feed is a single page");

        var items = await ReadItemsAsync(snapshot.Items);

        items.Select(item => item.ExternalId).Should().Equal(
            "Pride and Prejudice",
            "A Modern Essay",
            "Alice's Adventures in Wonderland");
        items.Should().OnlyContain(item => item.ProviderId == WikisourceProvider.ProviderIdentifier);
        items.Should().OnlyContain(item => item.MediaKind == ProviderMediaKind.Ebook);
        items.Should().OnlyContain(item => item.Assets.Count == 0);

        var pride = items[0];
        pride.Metadata.Title.Should().Be("Pride and Prejudice");
        pride.Metadata.Author.Should().Be("Jane Austen");
        pride.Metadata.Language.Should().Be("English");
        pride.Metadata.Categories.Should().Be("Fiction");
        pride.Metadata.Publisher.Should().Be("Wikisource");
        pride.Cover.Should().NotBeNull();
        pride.Cover!.Url.Should().Be(new Uri("https://thumb.wikimedia.org/example/pride.jpg"));
        pride.Cover.ContentType.Should().Be("image/jpeg");
        pride.Cover.FileExtension.Should().Be(".jpg");

        items[1].Cover!.FileExtension.Should().Be(".png");
        items[2].Cover.Should().BeNull();

        var request = handler.RecordedRequests.Should().ContainSingle().Which;
        request.Headers.IfNoneMatch.Should().BeEmpty("a full read has no validator to send");
        request.Headers.IfModifiedSince.Should().BeNull();
    }

    [Fact]
    public async Task Read_with_validators_sends_conditional_headers_and_reports_not_modified()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(CatalogPath, _ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var snapshot = await provider.ReadAsync(
            new ProviderSnapshotRequest(ETag: Etag, IfModifiedSince: LastModified),
            CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.NotModified);
        snapshot.ETag.Should().Be(Etag);
        snapshot.LastModified.Should().Be(LastModified);
        (await ReadItemsAsync(snapshot.Items)).Should().BeEmpty();

        var request = handler.RecordedRequests.Should().ContainSingle().Which;
        request.Headers.IfNoneMatch.Should().ContainSingle().Which.Tag.Should().Be(Etag);
        request.Headers.IfModifiedSince.Should().Be(LastModified);
    }

    [Fact]
    public async Task Read_reports_a_non_atom_body_as_invalid_provider_response()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(CatalogPath, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>maintenance</body></html>", Encoding.UTF8, "text/html"),
        });

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task Read_reports_an_upstream_failure_as_provider_unavailable()
    {
        var (provider, handler) = CreateProvider();
        handler.Register(CatalogPath, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public void Snapshot_source_is_reachable_through_the_provider_registry()
    {
        var (provider, _) = CreateProvider();

        var registration = new ProviderRegistry(new IContentProvider[] { provider })
            .Find(WikisourceProvider.ProviderIdentifier);

        registration.Should().NotBeNull();
        registration!.Provider.Should().BeSameAs(provider);
        registration.Provider.Should().BeAssignableTo<IProviderSnapshotSource>(
            "a host reaches the bulk source through the registered provider");
    }
}

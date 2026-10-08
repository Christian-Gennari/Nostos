using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.StandardEbooks;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>Recorded-feed contract tests for the Standard Ebooks catalog source.</summary>
public sealed class StandardEbooksSnapshotSourceTests
{
    private const string NewReleasesPath = "/feeds/opds/new-releases";
    private const string AllEbooksPath = "/feeds/opds/all";
    private const string ETag = "\"standard-ebooks-20261007\"";
    private static readonly DateTimeOffset LastModified = new(2026, 10, 7, 2, 12, 11, TimeSpan.Zero);
    private static readonly DateTimeOffset ScanStart = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);

    private static string LoadFixture(string filename)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Fixtures", "standard-ebooks", filename);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            var nestedCandidate = Path.Combine(
                current.FullName,
                "Nostos.Backend.Tests",
                "Fixtures",
                "standard-ebooks",
                filename);
            if (File.Exists(nestedCandidate))
                return File.ReadAllText(nestedCandidate);

            current = current.Parent;
        }

        throw new FileNotFoundException($"Standard Ebooks fixture '{filename}' could not be located.");
    }

    private static HttpResponseMessage FeedResponse(
        string xml,
        string? etag = null,
        DateTimeOffset? lastModified = null)
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

    private static (StandardEbooksProvider Provider, StubHttpClientFactory Factory, StubHttpMessageHandler Handler, RecordingTimeProvider Clock)
        CreateProvider()
    {
        var handler = new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler, new Uri("https://standardebooks.org"));
        var clock = new RecordingTimeProvider(ScanStart);
        var provider = new StandardEbooksProvider(
            factory,
            NullLogger<StandardEbooksProvider>.Instance,
            clock);
        return (provider, factory, handler, clock);
    }

    private static async Task<List<ProviderItem>> ReadItemsAsync(IAsyncEnumerable<ProviderItem> items)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items)
            result.Add(item);

        return result;
    }

    [Fact]
    public async Task Read_without_a_checkpoint_returns_the_complete_catalog_page_for_reconcile()
    {
        var (provider, factory, handler, _) = CreateProvider();
        handler.Register(AllEbooksPath, _ => FeedResponse(LoadFixture("all-ebooks.opds"), ETag, LastModified));

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        snapshot.ETag.Should().Be(ETag);
        snapshot.LastModified.Should().Be(LastModified);
        snapshot.NextCursor.Should().BeNull("the recorded complete feed has no next-page relation");

        var items = await ReadItemsAsync(snapshot.Items);
        items.Select(item => item.ExternalId).Should().Equal(
            "ambrose-bierce~the-devils-dictionary",
            "saki~short-fiction",
            "ivan-turgenev~fathers-and-children~constance-garnett");
        items.Should().OnlyContain(item => item.ProviderId == StandardEbooksProvider.ProviderIdentifier);
        items.Should().OnlyContain(item => item.MediaKind == ProviderMediaKind.Ebook);
        items.Should().OnlyContain(item => item.Assets.Count == 0, "catalog sync stores metadata but does not download or expose files");

        items[0].Metadata.Title.Should().Be("The Devil’s Dictionary");
        items[0].Metadata.Author.Should().Be("Ambrose Bierce");
        items[0].Metadata.Language.Should().Be("English (United States)");
        items[0].Cover.Should().NotBeNull();
        items[0].Cover!.Url.Should().Be(
            new Uri("https://standardebooks.org/ebooks/ambrose-bierce/the-devils-dictionary/downloads/cover.jpg"));
        items[0].Source!.RightsStatement.Should().Contain("Public domain in the United States");

        var request = handler.RecordedRequests.Should().ContainSingle().Which;
        request.RequestUri!.PathAndQuery.Should().Be(AllEbooksPath);
        request.Headers.UserAgent.ToString().Should().Be(StandardEbooksProvider.ApprovedUserAgent);
        request.Headers.Accept.Should().ContainSingle().Which.MediaType.Should().Be("application/atom+xml");
        request.Headers.IfNoneMatch.Should().BeEmpty();
        request.Headers.IfModifiedSince.Should().BeNull();
        factory.RequestedClientNames.Should().Contain(StandardEbooksProvider.SnapshotHttpClientName);
    }

    [Fact]
    public async Task Read_with_validators_checks_new_releases_conditionally_and_returns_not_modified()
    {
        var (provider, _, handler, _) = CreateProvider();
        handler.Register(NewReleasesPath, _ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var snapshot = await provider.ReadAsync(
            new ProviderSnapshotRequest(ETag: ETag, IfModifiedSince: LastModified),
            CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.NotModified);
        snapshot.ETag.Should().Be(ETag);
        snapshot.LastModified.Should().Be(LastModified);
        (await ReadItemsAsync(snapshot.Items)).Should().BeEmpty();

        var request = handler.RecordedRequests.Should().ContainSingle().Which;
        request.RequestUri!.PathAndQuery.Should().Be(NewReleasesPath);
        request.Headers.IfNoneMatch.Should().ContainSingle().Which.Tag.Should().Be(ETag);
        request.Headers.IfModifiedSince.Should().Be(LastModified);
        request.Headers.UserAgent.ToString().Should().Be(StandardEbooksProvider.ApprovedUserAgent);
    }

    [Fact]
    public async Task Read_changed_new_releases_feed_returns_incremental_cards_and_validators()
    {
        var (provider, _, handler, _) = CreateProvider();
        handler.Register(NewReleasesPath, _ => FeedResponse(
            LoadFixture("new-releases.opds"),
            "\"standard-ebooks-new-20261006\"",
            new DateTimeOffset(2026, 10, 6, 21, 26, 20, TimeSpan.Zero)));

        var snapshot = await provider.ReadAsync(
            new ProviderSnapshotRequest(ETag: ETag, IfModifiedSince: LastModified),
            CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        snapshot.ETag.Should().Be("\"standard-ebooks-new-20261006\"");
        snapshot.LastModified.Should().Be(new DateTimeOffset(2026, 10, 6, 21, 26, 20, TimeSpan.Zero));

        var items = await ReadItemsAsync(snapshot.Items);
        items.Should().HaveCount(15);
        items[0].ExternalId.Should().Be("ernst-junger~the-storm-of-steel~basil-creighton");
        items[0].Metadata.Title.Should().Be("The Storm of Steel");
        items[0].Metadata.Author.Should().Be("Ernst Jünger");
        items.Should().OnlyContain(item => item.Assets.Count == 0);
        items[0].Cover.Should().NotBeNull();
        handler.RecordedRequests.Should().ContainSingle()
            .Which.RequestUri!.PathAndQuery.Should().Be(NewReleasesPath);
    }

    [Fact]
    public async Task Read_follows_advertised_full_feed_pages_sequentially_and_preserves_the_checkpoint()
    {
        var (provider, _, handler, clock) = CreateProvider();
        var firstPage = LoadFixture("all-ebooks.opds").Replace(
            "</feed>",
            "<link href=\"https://standardebooks.org/feeds/opds/all?page=2\" rel=\"next\" /></feed>",
            StringComparison.Ordinal);
        handler.Register(AllEbooksPath, _ => FeedResponse(firstPage, ETag, LastModified));
        handler.Register(AllEbooksPath + "?page=2", _ => FeedResponse(LoadFixture("all-ebooks.opds")));

        var first = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        first.NextCursor.Should().NotBeNull();
        first.ETag.Should().Be(ETag);
        first.LastModified.Should().Be(LastModified);

        var second = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: first.NextCursor),
            CancellationToken.None);

        second.NextCursor.Should().BeNull();
        second.ETag.Should().Be(ETag, "the initial page's validator remains the scan checkpoint");
        second.LastModified.Should().Be(LastModified);
        (await ReadItemsAsync(second.Items)).Should().HaveCount(3);
        clock.Delays.Should().ContainSingle().Which.Should().Be(StandardEbooksProvider.SnapshotPageDelay);
        handler.RecordedRequestPaths.Should().BeEquivalentTo(AllEbooksPath, AllEbooksPath + "?page=2");
    }

    [Fact]
    public async Task Read_retries_a_rate_limited_page_after_retry_after()
    {
        var (provider, _, handler, clock) = CreateProvider();
        var attempts = 0;
        handler.Register(AllEbooksPath, _ =>
        {
            if (++attempts > 1)
                return FeedResponse(LoadFixture("all-ebooks.opds"), ETag, LastModified);

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        });

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        attempts.Should().Be(2);
        clock.Delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(17));
    }

    [Fact]
    public async Task Read_on_full_feed_failure_throws_instead_of_returning_an_empty_reconcile_snapshot()
    {
        var (provider, _, handler, _) = CreateProvider();
        handler.Register(AllEbooksPath, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    private sealed class RecordingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }
}

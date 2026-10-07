using System.Net;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// Contract tests for the LibriVox bulk snapshot seam (#776 P0-C): pages of
/// 500 with a deliberate delay, a since watermark for incremental reads, a
/// full paged scan, and pages that never pull the sections array.
/// </summary>
public sealed class LibriVoxSnapshotSourceTests
{
    private static readonly DateTimeOffset ScanStart = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Watermark = new(2026, 10, 6, 8, 30, 0, TimeSpan.Zero);

    private const string Fields =
        "id,title,language,num_sections,totaltime,copyright_year,authors,genres,url_librivox,url_iarchive,coverart_thumbnail";

    private static string PagePath(int offset, long? since = null) =>
        $"/api/feed/audiobooks/?format=json&extended=1&coverart=1&sort_order=asc&limit=500&offset={offset}&fields={Fields}"
        + (since is { } value ? $"&since={value}" : string.Empty);

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

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string FullPageJson(int count, bool withUnparsableRecord = false)
    {
        var books = Enumerable.Range(1, count)
            .Select(index => (object)new { id = (4000 + index).ToString(), title = $"Snapshot Book {index}" })
            .ToList();

        if (withUnparsableRecord)
            books[^1] = new { };

        return JsonSerializer.Serialize(new { books });
    }

    private static async Task<List<ProviderItem>> ReadItemsAsync(IAsyncEnumerable<ProviderItem> items)
    {
        var result = new List<ProviderItem>();
        await foreach (var item in items)
            result.Add(item);

        return result;
    }

    private sealed class StubMediaProcessRunner : IMediaProcessRunner
    {
        public MediaToolAvailability Availability { get; set; } =
            new(true, "/usr/bin/ffmpeg", "/usr/bin/ffprobe", null);

        public Task<TimeSpan?> ProbeDurationAsync(string path, CancellationToken ct) =>
            Task.FromResult<TimeSpan?>(TimeSpan.FromSeconds(10));

        public Task<int?> ProbeChapterCountAsync(string path, CancellationToken ct) =>
            Task.FromResult<int?>(1);

        public Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A clock fixed at the scan start whose timers fire immediately while
    /// recording the delay they were asked for, so the inter-page pause is
    /// asserted without a real one-second wait.
    /// </summary>
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

    /// <summary>
    /// Hands out a distinct stub per named client, so a test can prove which
    /// client a read actually used.
    /// </summary>
    private sealed class PerClientStubHttpClientFactory : IHttpClientFactory
    {
        private readonly Dictionary<string, StubHttpMessageHandler> _handlers = new(StringComparer.Ordinal);

        public StubHttpMessageHandler HandlerFor(string name)
        {
            if (!_handlers.TryGetValue(name, out var handler))
            {
                handler = new StubHttpMessageHandler();
                _handlers[name] = handler;
            }

            return handler;
        }

        public HttpClient CreateClient(string name) =>
            new(HandlerFor(name), disposeHandler: false)
            {
                BaseAddress = new Uri("https://librivox.org"),
            };
    }

    private static LibriVoxProvider CreateProvider(IHttpClientFactory factory, TimeProvider clock) =>
        new(
            factory,
            new LibriVoxM4bAssembler(new StubMediaProcessRunner(), NullLogger<LibriVoxM4bAssembler>.Instance),
            NullLogger<LibriVoxProvider>.Instance,
            clock);

    private static (LibriVoxProvider Provider, StubHttpMessageHandler Handler, RecordingTimeProvider Clock) CreateProvider()
    {
        var handler = new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler, new Uri("https://librivox.org"));
        var clock = new RecordingTimeProvider(ScanStart);
        return (CreateProvider(factory, clock), handler, clock);
    }

    [Fact]
    public async Task Read_full_scan_returns_card_items_without_sections_and_reports_the_scan_start()
    {
        var (provider, handler, _) = CreateProvider();
        handler.Register(PagePath(0), JsonResponse(LoadFixture("snapshot-page.json")));

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated);
        snapshot.ETag.Should().BeNull("LibriVox supplies no entity tag");
        snapshot.LastModified.Should().Be(ScanStart, "the scan start is the watermark for the next since read");
        snapshot.NextCursor.Should().BeNull("three items is a short final page");

        var items = await ReadItemsAsync(snapshot.Items);
        items.Select(item => item.ExternalId).Should().Equal("47", "52", "53");
        items.Should().OnlyContain(item => item.ProviderId == LibriVoxProvider.ProviderIdentifier);
        items.Should().OnlyContain(item => item.MediaKind == ProviderMediaKind.Audiobook);
        items.Should().OnlyContain(item => item.Assets.Count == 0);
        items.Should().OnlyContain(item => item.Metadata.Narrator == null, "sections are deliberately not requested");

        var monteCristo = items[0];
        monteCristo.Metadata.Title.Should().Be("Count of Monte Cristo");
        monteCristo.Metadata.Author.Should().Be("Alexandre Dumas");
        monteCristo.Metadata.Language.Should().Be("English");
        monteCristo.Metadata.Categories.Should().Be("Literary Fiction, Published 1800 -1900");
        monteCristo.Metadata.Duration.Should().Be("49:43:15");
        monteCristo.PartCount.Should().Be(128, "num_sections arrives even without the sections array");
        monteCristo.Cover.Should().NotBeNull();
        monteCristo.Cover!.Url.Should().Be(
            new Uri("https://www.archive.org/download/LibrivoxCdCoverArt12/Count_Monte_Cristo_1110_thumb.jpg"));
        monteCristo.Cover.ContentType.Should().Be("image/jpeg");
        monteCristo.Cover.FileExtension.Should().Be(".jpg");

        items[1].Cover!.Url.Should().Be(
            new Uri("https://archive.org/services/img/letters_brides_0709_librivox"),
            "an item without cover art still has the archive.org item thumbnail");

        items[2].Cover.Should().BeNull();
        items[2].Metadata.Author.Should().BeNull();
        items[2].Metadata.Categories.Should().BeNull();
        items[2].PartCount.Should().Be(67);
        items[2].Source!.ItemUrl.Should().Be("https://librivox.org/bleak-house-by-charles-dickens/");

        var request = handler.RecordedRequests.Should().ContainSingle().Which;
        request.RequestUri!.PathAndQuery.Should().Be(PagePath(0));

        var requestedFields = request.RequestUri.Query
            .Split('&')
            .Single(part => part.StartsWith("fields=", StringComparison.Ordinal))["fields=".Length..]
            .Split(',');
        requestedFields.Should().NotContain("sections", "the bulk page must not pull the sections array");
    }

    [Fact]
    public async Task Read_uses_the_dedicated_snapshot_client_not_the_discovery_client()
    {
        var factory = new PerClientStubHttpClientFactory();
        var snapshotHandler = factory.HandlerFor(LibriVoxProvider.SnapshotHttpClientName);
        snapshotHandler.Register(PagePath(0), JsonResponse(LoadFixture("snapshot-page.json")));
        var provider = CreateProvider(factory, new RecordingTimeProvider(ScanStart));

        var snapshot = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        (await ReadItemsAsync(snapshot.Items)).Should().HaveCount(3);
        snapshotHandler.RecordedRequests.Should().ContainSingle("the page came from the snapshot client");
        factory.HandlerFor(LibriVoxProvider.HttpClientName).RecordedRequests.Should().BeEmpty(
            "the 20 s discovery client must not serve bulk snapshot reads");
    }

    [Fact]
    public async Task Read_continues_a_full_scan_when_a_full_page_contains_an_unparsable_record()
    {
        var (provider, handler, clock) = CreateProvider();
        handler.Register(PagePath(0), JsonResponse(FullPageJson(500, withUnparsableRecord: true)));
        handler.Register(PagePath(500), JsonResponse(LoadFixture("snapshot-page.json")));

        var first = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        (await ReadItemsAsync(first.Items)).Should().HaveCount(499, "the unparsable record is skipped");
        first.NextCursor.Should().NotBeNull("the raw page was full, so the scan must continue");

        var second = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: first.NextCursor),
            CancellationToken.None);

        (await ReadItemsAsync(second.Items)).Should().HaveCount(3);
        second.NextCursor.Should().BeNull();
        clock.Delays.Should().ContainSingle().Which.Should().Be(LibriVoxProvider.SnapshotPageDelay);
    }

    [Fact]
    public async Task Read_full_scan_pages_with_a_delay_until_the_last_page()
    {
        var (provider, handler, clock) = CreateProvider();
        var requested = new List<string>();
        handler.Register(PagePath(0), request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            return JsonResponse(FullPageJson(500));
        });
        handler.Register(PagePath(500), request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            return JsonResponse(LoadFixture("snapshot-page.json"));
        });

        var first = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        (await ReadItemsAsync(first.Items)).Should().HaveCount(500);
        first.NextCursor.Should().NotBeNull("a full page cannot prove the catalogue ended");
        clock.Delays.Should().BeEmpty("the first page of a scan is not delayed");

        var second = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: first.NextCursor),
            CancellationToken.None);

        (await ReadItemsAsync(second.Items)).Should().HaveCount(3);
        second.NextCursor.Should().BeNull();
        second.LastModified.Should().Be(ScanStart, "every page of a scan reports the same watermark");
        clock.Delays.Should().ContainSingle().Which.Should().Be(LibriVoxProvider.SnapshotPageDelay);

        requested.Should().Equal(new[] { PagePath(0), PagePath(500) }, "page 0 is fetched before its continuation");
    }

    [Fact]
    public async Task Read_with_an_if_modified_since_watermark_pages_additions_since_then()
    {
        var (provider, handler, clock) = CreateProvider();
        var since = Watermark.ToUnixTimeSeconds() - 1;
        var requested = new List<string>();
        handler.Register(PagePath(0, since), request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            return JsonResponse(FullPageJson(500));
        });
        handler.Register(PagePath(500, since), request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            return JsonResponse(LoadFixture("snapshot-page.json"));
        });

        var first = await provider.ReadAsync(
            new ProviderSnapshotRequest(IfModifiedSince: Watermark),
            CancellationToken.None);

        first.LastModified.Should().Be(ScanStart);
        first.NextCursor.Should().NotBeNull();
        (await ReadItemsAsync(first.Items)).Should().HaveCount(500);

        var second = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: first.NextCursor),
            CancellationToken.None);

        (await ReadItemsAsync(second.Items)).Should().HaveCount(3);
        second.NextCursor.Should().BeNull();
        clock.Delays.Should().ContainSingle().Which.Should().Be(LibriVoxProvider.SnapshotPageDelay);

        requested.Should().Equal(new[] { PagePath(0, since), PagePath(500, since) }, "the since boundary carries into page 2");
    }

    [Fact]
    public async Task Read_since_mode_with_no_additions_is_an_empty_updated_page()
    {
        var (provider, handler, _) = CreateProvider();
        var since = Watermark.ToUnixTimeSeconds() - 1;
        handler.Register(
            PagePath(0, since),
            JsonResponse("{\"error\":\"Audiobooks could not be found\"}", HttpStatusCode.NotFound));

        var snapshot = await provider.ReadAsync(
            new ProviderSnapshotRequest(IfModifiedSince: Watermark),
            CancellationToken.None);

        snapshot.Status.Should().Be(ProviderSnapshotStatus.Updated, "the API's 404 means no additions, not a failure");
        snapshot.LastModified.Should().Be(ScanStart);
        snapshot.NextCursor.Should().BeNull();
        (await ReadItemsAsync(snapshot.Items)).Should().BeEmpty();
    }

    [Fact]
    public async Task Read_reports_a_404_on_the_first_page_of_a_full_scan_as_provider_unavailable()
    {
        var (provider, handler, _) = CreateProvider();
        handler.Register(
            PagePath(0),
            JsonResponse("{\"error\":\"Audiobooks could not be found\"}", HttpStatusCode.NotFound));

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public async Task Read_ends_a_full_scan_at_a_page_boundary_instead_of_failing_on_the_probe_404()
    {
        // The API answers 404 when the offset reaches the end of the catalogue,
        // so a catalogue whose size is a multiple of the page size always gets
        // a missing page after its last full page: that is the end of the scan.
        var (provider, handler, _) = CreateProvider();
        handler.Register(PagePath(0), JsonResponse(FullPageJson(500)));
        handler.Register(
            PagePath(500),
            JsonResponse("{\"error\":\"Audiobooks could not be found\"}", HttpStatusCode.NotFound));

        var first = await provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);
        first.NextCursor.Should().NotBeNull();

        var second = await provider.ReadAsync(
            new ProviderSnapshotRequest(Cursor: first.NextCursor),
            CancellationToken.None);

        second.Status.Should().Be(ProviderSnapshotStatus.Updated);
        second.NextCursor.Should().BeNull();
        (await ReadItemsAsync(second.Items)).Should().BeEmpty();
    }

    [Fact]
    public async Task Read_reports_a_truncated_page_as_invalid_provider_response()
    {
        var (provider, handler, _) = CreateProvider();
        handler.Register(PagePath(0), JsonResponse("{\"books\":[{\"id\":\"47\",\"title\":\"Count of Monte"));

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task Read_reports_an_upstream_timeout_as_provider_unavailable()
    {
        var (provider, handler, _) = CreateProvider();
        handler.Register(PagePath(0), _ => throw new TaskCanceledException("The catalogue timed out."));

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public async Task Read_reports_an_upstream_http_failure_as_provider_unavailable()
    {
        var (provider, handler, _) = CreateProvider();
        handler.Register(PagePath(0), _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public async Task Read_lets_caller_cancellation_through()
    {
        var (provider, handler, _) = CreateProvider();
        using var cts = new CancellationTokenSource();
        handler.Register(PagePath(0), _ =>
        {
            cts.Cancel();
            return JsonResponse(LoadFixture("snapshot-page.json"));
        });

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("v1|f|1759000000|250")]
    [InlineData("v1|s|1759000000|1759000000|250")]
    public async Task Read_rejects_a_corrupt_cursor(string cursor)
    {
        var (provider, _, _) = CreateProvider();

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(Cursor: cursor), CancellationToken.None);

        var error = await act.Should().ThrowAsync<ProviderException>();
        error.Which.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public void Snapshot_source_is_reachable_through_the_provider_registry()
    {
        var (provider, _, _) = CreateProvider();

        var registration = new ProviderRegistry(new IContentProvider[] { provider })
            .Find(LibriVoxProvider.ProviderIdentifier);

        registration.Should().NotBeNull();
        registration!.Provider.Should().BeSameAs(provider);
        registration.Provider.Should().BeAssignableTo<IProviderSnapshotSource>(
            "a host reaches the bulk source through the registered provider");
    }
}

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

    private static string FullPageJson(int count) =>
        JsonSerializer.Serialize(new
        {
            books = Enumerable.Range(1, count)
                .Select(index => new { id = (4000 + index).ToString(), title = $"Snapshot Book {index}" })
                .ToArray(),
        });

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

    private static (LibriVoxProvider Provider, StubHttpMessageHandler Handler, RecordingTimeProvider Clock) CreateProvider()
    {
        var handler = new StubHttpMessageHandler();
        var factory = new StubHttpClientFactory(handler, new Uri("https://librivox.org"));
        var clock = new RecordingTimeProvider(ScanStart);
        var provider = new LibriVoxProvider(
            factory,
            new LibriVoxM4bAssembler(new StubMediaProcessRunner(), NullLogger<LibriVoxM4bAssembler>.Instance),
            NullLogger<LibriVoxProvider>.Instance,
            clock);
        return (provider, handler, clock);
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
    public async Task Read_full_scan_pages_with_a_delay_until_the_last_page()
    {
        var (provider, handler, clock) = CreateProvider();
        handler.Register(PagePath(0), JsonResponse(FullPageJson(500)));
        handler.Register(PagePath(500), JsonResponse(LoadFixture("snapshot-page.json")));

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

        handler.RecordedRequestPaths.Should().BeEquivalentTo(new[] { PagePath(0), PagePath(500) });
    }

    [Fact]
    public async Task Read_with_an_if_modified_since_watermark_pages_additions_since_then()
    {
        var (provider, handler, clock) = CreateProvider();
        var since = Watermark.ToUnixTimeSeconds() - 1;
        handler.Register(PagePath(0, since), JsonResponse(FullPageJson(500)));
        handler.Register(PagePath(500, since), JsonResponse(LoadFixture("snapshot-page.json")));

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

        handler.RecordedRequestPaths.Should().BeEquivalentTo(new[] { PagePath(0, since), PagePath(500, since) });
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

    [Fact]
    public async Task Read_rejects_a_corrupt_cursor()
    {
        var (provider, _, _) = CreateProvider();

        var act = () => provider.ReadAsync(new ProviderSnapshotRequest(Cursor: "not-a-cursor"), CancellationToken.None);

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

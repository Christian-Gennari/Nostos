using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class ProviderDiscoveryEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public ProviderDiscoveryEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    [Fact]
    public async Task AggregateSearch_BindsKind_AndReturnsNormalizedItemsAndSourceStatuses()
    {
        var ebook = new EndpointProvider(
            "ebooks",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage(
                [Item("ebooks", "shared", ProviderMediaKind.Ebook, "Ebook result")],
                Notice: "Ebook notice."));
        var audio = new EndpointProvider(
            "audio",
            ProviderCapabilities.AudiobookAcquisition,
            new ProviderSearchPage(
                [Item("audio", "shared", ProviderMediaKind.Audiobook, "Audio result")]));

        await using var app = _factory.WithWebHostBuilder(builder =>
            ReplaceProviders(builder, ebook, audio));
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<ProviderDiscoverySearchResultDto>(
            "/api/providers/search?query=classic&kind=ebook");

        response.Should().NotBeNull();
        response!.Items.Should().ContainSingle();
        response.Items[0].ProviderId.Should().Be("ebooks");
        response.Items[0].ExternalId.Should().Be("shared");
        response.Items[0].MediaKind.Should().Be("ebook");
        response.Items[0].Assets.Should().BeEmpty();
        response.Sources.Should().ContainSingle();
        response.Sources[0].ProviderId.Should().Be("ebooks");
        response.Sources[0].Succeeded.Should().BeTrue();
        response.Sources[0].Notice.Should().Be("Ebook notice.");
        ebook.SearchCount.Should().Be(1);
        audio.SearchCount.Should().Be(0);
    }

    [Fact]
    public async Task AggregateSearch_PreservesSiblingResultsWhenOneProviderFails()
    {
        var good = new EndpointProvider(
            "alpha",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([Item("alpha", "1", ProviderMediaKind.Ebook, "Good")]));
        var bad = new EndpointProvider(
            "beta",
            ProviderCapabilities.EbookAcquisition,
            ProviderException.UnavailableFor("beta", "test"));

        await using var app = _factory.WithWebHostBuilder(builder =>
            ReplaceProviders(builder, bad, good));
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<ProviderDiscoverySearchResultDto>(
            "/api/providers/search?query=classic");

        response.Should().NotBeNull();
        response!.Items.Should().ContainSingle();
        response.Items[0].ProviderId.Should().Be("alpha");
        response.Sources.Should().HaveCount(2);
        response.Sources.Single(source => source.ProviderId == "alpha").Succeeded.Should().BeTrue();
        var failure = response.Sources.Single(source => source.ProviderId == "beta");
        failure.Succeeded.Should().BeFalse();
        failure.ErrorCode.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public async Task AggregateSearch_TimesOutHungProvider_AndReturnsSiblingResult()
    {
        var never = new TaskCompletionSource<ProviderSearchPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var good = new EndpointProvider(
            "alpha",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([Item("alpha", "1", ProviderMediaKind.Ebook, "Good")]));
        var hung = new EndpointProvider(
            "beta",
            ProviderCapabilities.EbookAcquisition,
            (_, _) => never.Task);

        await using var app = _factory.WithWebHostBuilder(builder =>
            ReplaceProviders(builder, TimeSpan.FromMilliseconds(60), good, hung));
        using var client = app.CreateClient();

        var response = await client
            .GetFromJsonAsync<ProviderDiscoverySearchResultDto>(
                "/api/providers/search?query=classic")
            .WaitAsync(TimeSpan.FromSeconds(2));

        response.Should().NotBeNull();
        response!.Items.Should().ContainSingle();
        response.Items[0].ProviderId.Should().Be("alpha");
        response.Sources.Should().HaveCount(2);
        response.Sources.Single(source => source.ProviderId == "alpha").Succeeded.Should().BeTrue();

        var timedOut = response.Sources.Single(source => source.ProviderId == "beta");
        timedOut.Succeeded.Should().BeFalse();
        timedOut.ErrorCode.Should().Be(ProviderDiscoveryErrorCodes.Timeout);
    }

    [Fact]
    public async Task AggregateSearch_ConsumesHostDiscoveryBackend_WithoutLiveProviderSearches()
    {
        var gutenberg = new EndpointProvider(
            "gutenberg",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([Item("gutenberg", "live", ProviderMediaKind.Ebook, "Never searched")]));
        var librivox = new EndpointProvider(
            "librivox",
            ProviderCapabilities.AudiobookAcquisition,
            new ProviderSearchPage([]));
        var snapshot = new SnapshotDiscovery(new ProviderDiscoveryResult(
            [Item("gutenberg", "42", ProviderMediaKind.Ebook, "The Republic")],
            HasMore: true,
            Sources:
            [
                new ProviderDiscoverySourceStatus(
                    "gutenberg",
                    "Project Gutenberg",
                    Succeeded: true,
                    Notice: "Catalog snapshot; results may lag the source."),
                new ProviderDiscoverySourceStatus(
                    "librivox",
                    "LibriVox",
                    Succeeded: false,
                    ErrorCode: ProviderException.Unavailable),
            ]));

        await using var app = _factory.WithWebHostBuilder(builder =>
            ReplaceProvidersWithDiscovery(builder, snapshot, gutenberg, librivox));
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<ProviderDiscoverySearchResultDto>(
            "/api/providers/search?query=classic&kind=ebook&limit=10");

        response.Should().NotBeNull();
        response!.Items.Should().ContainSingle();
        response.Items[0].ProviderId.Should().Be("gutenberg");
        response.Items[0].ExternalId.Should().Be("42");
        response.Items[0].MediaKind.Should().Be("ebook");
        response.Items[0].Title.Should().Be("The Republic");
        response.Items[0].Assets.Should().BeEmpty();
        response.HasMore.Should().BeTrue();
        response.Sources.Should().HaveCount(2);
        response.Sources.Single(source => source.ProviderId == "gutenberg").Notice
            .Should().Be("Catalog snapshot; results may lag the source.");
        var stale = response.Sources.Single(source => source.ProviderId == "librivox");
        stale.Succeeded.Should().BeFalse();
        stale.ErrorCode.Should().Be(ProviderException.Unavailable);

        gutenberg.SearchCount.Should().Be(0, "a host discovery backend must not fall back to live provider search");
        librivox.SearchCount.Should().Be(0);
        snapshot.CallCount.Should().Be(1);
        snapshot.LastRequest!.Query.Should().Be("classic");
        snapshot.LastRequest.Kind.Should().Be(ProviderMediaKind.Ebook);
        snapshot.LastRequest.Limit.Should().Be(10);
        snapshot.LastRequest.ProviderIds.Should().BeEquivalentTo(
            ["gutenberg", "librivox"],
            "#774 sends the enabled set on every aggregate discovery call");
    }

    [Fact]
    public async Task CoverProxy_UsesHostCoverMetadataWithoutFetchingItemDetailsPerResult()
    {
        var coverUri = new Uri("https://covers.example.com/42.jpg");
        var item = Item("gutenberg", "42", ProviderMediaKind.Ebook, "The Republic") with
        {
            Cover = new ProviderCover(coverUri, "image/png", ".png"),
        };
        var provider = new EndpointProvider(
            "gutenberg",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([]));
        var snapshot = new SnapshotDiscovery(new ProviderDiscoveryResult(
            [item],
            HasMore: false,
            Sources: [new ProviderDiscoverySourceStatus("gutenberg", "Project Gutenberg", Succeeded: true)]));
        var covers = new SnapshotCoverLookup(new ProviderCover(coverUri, "image/png", ".png"));
        var downloader = new FakeProviderContentDownloader { DefaultCoverPayload = [1, 2, 3] };

        await using var app = _factory.WithWebHostBuilder(builder =>
        {
            ReplaceProvidersWithDiscovery(builder, snapshot, provider);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProviderCoverLookup>();
                services.AddSingleton<IProviderCoverLookup>(covers);
                services.RemoveAll<IProviderContentDownloader>();
                services.AddSingleton<IProviderContentDownloader>(downloader);
            });
        });
        using var client = app.CreateClient();

        var search = await client.GetFromJsonAsync<ProviderDiscoverySearchResultDto>(
            "/api/providers/search?query=republic");
        search!.Items.Should().ContainSingle();
        search.Items[0].CoverUrl.Should().Be("/api/providers/gutenberg/items/42/cover");

        using var response = await client.GetAsync(search.Items[0].CoverUrl);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
        covers.CallCount.Should().Be(1);
        covers.LastProviderId.Should().Be("gutenberg");
        covers.LastExternalId.Should().Be("42");
        downloader.RequestedUrls.Should().ContainSingle().Which.Should().Be(coverUri);
        provider.SearchCount.Should().Be(0);
    }

    [Fact]
    public async Task AggregateSearch_DropsRowsForProvidersThisHostDoesNotRun()
    {
        var gutenberg = new EndpointProvider(
            "gutenberg",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([]));
        var snapshot = new SnapshotDiscovery(new ProviderDiscoveryResult(
            [
                Item("gutenberg", "42", ProviderMediaKind.Ebook, "The Republic"),
                Item("cloud-only", "99", ProviderMediaKind.Ebook, "Not registered here"),
            ],
            HasMore: false,
            Sources:
            [
                new ProviderDiscoverySourceStatus("gutenberg", "Project Gutenberg", Succeeded: true),
                new ProviderDiscoverySourceStatus("cloud-only", "Cloud Only", Succeeded: true),
            ]));

        await using var app = _factory.WithWebHostBuilder(builder =>
            ReplaceProvidersWithDiscovery(builder, snapshot, gutenberg));
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<ProviderDiscoverySearchResultDto>(
            "/api/providers/search?query=classic");

        response.Should().NotBeNull();
        response!.Items.Should().ContainSingle().Which.ProviderId.Should().Be("gutenberg");
        response.Sources.Should().ContainSingle().Which.ProviderId.Should().Be("gutenberg");
    }

    [Fact]
    public void BoundaryDropsRowsOutsideTheAllowedProviderSet()
    {
        var alpha = new EndpointProvider(
            "alpha",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([]));
        var beta = new EndpointProvider(
            "beta",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([]));
        var registry = new ProviderRegistry([alpha, beta]);
        var result = new ProviderDiscoveryResult(
            [
                Item("alpha", "1", ProviderMediaKind.Ebook, "Alpha"),
                Item("beta", "2", ProviderMediaKind.Ebook, "Beta"),
            ],
            HasMore: false,
            Sources:
            [
                new ProviderDiscoverySourceStatus("alpha", "Source alpha", Succeeded: true),
                new ProviderDiscoverySourceStatus("beta", "Source beta", Succeeded: true),
            ]);

        var filtered = ProviderDiscoveryBoundary.Apply(
            result,
            registry,
            new HashSet<string> { "alpha" });

        filtered.Items.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
        filtered.Sources.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
    }

    [Fact]
    public void BoundaryDropsRowsWhoseIdOnlyNormalisesToARegisteredOne()
    {
        var alpha = new EndpointProvider(
            "alpha",
            ProviderCapabilities.EbookAcquisition,
            new ProviderSearchPage([]));
        var registry = new ProviderRegistry([alpha]);
        var result = new ProviderDiscoveryResult(
            [
                Item(" alpha ", "1", ProviderMediaKind.Ebook, "Trimmed"),
            ],
            HasMore: false,
            Sources:
            [
                new ProviderDiscoverySourceStatus(" alpha ", "Source alpha", Succeeded: true),
            ]);

        // The registry lookup trims, so a backend could smuggle " alpha " past
        // a lookup-only check; it is not this process's id and no acquisition
        // call could resolve it, so the boundary must reject it.
        var filtered = ProviderDiscoveryBoundary.Apply(
            result,
            registry,
            allowedProviderIds: null);

        filtered.Items.Should().BeEmpty();
        filtered.Sources.Should().BeEmpty();
    }

    private static void ReplaceProvidersWithDiscovery(
        IWebHostBuilder builder,
        IProviderDiscovery discovery,
        params IContentProvider[] providers)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IContentProvider>();
            services.RemoveAll<IProviderRegistry>();
            services.RemoveAll<ProviderDiscoveryService>();
            services.RemoveAll<IProviderDiscovery>();

            foreach (var provider in providers)
                services.AddSingleton(typeof(IContentProvider), provider);

            services.AddSingleton<IProviderRegistry, ProviderRegistry>();
            services.AddSingleton<IProviderDiscovery>(discovery);
        });
    }

    private static void ReplaceProviders(
        IWebHostBuilder builder,
        params IContentProvider[] providers) =>
        ReplaceProvidersCore(builder, null, providers);

    private static void ReplaceProviders(
        IWebHostBuilder builder,
        TimeSpan searchTimeout,
        params IContentProvider[] providers) =>
        ReplaceProvidersCore(builder, searchTimeout, providers);

    private static void ReplaceProvidersCore(
        IWebHostBuilder builder,
        TimeSpan? searchTimeout,
        IReadOnlyList<IContentProvider> providers)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IContentProvider>();
            services.RemoveAll<IProviderRegistry>();
            services.RemoveAll<ProviderDiscoveryService>();
            services.RemoveAll<IProviderDiscovery>();

            foreach (var provider in providers)
                services.AddSingleton(typeof(IContentProvider), provider);

            if (searchTimeout is { } timeout)
            {
                services.Configure<ProviderDiscoveryOptions>(options =>
                    options.SearchTimeout = timeout);
            }

            services.AddSingleton<IProviderRegistry, ProviderRegistry>();
            services.AddSingleton<ProviderDiscoveryService>();
            services.AddSingleton<IProviderDiscovery>(sp =>
                sp.GetRequiredService<ProviderDiscoveryService>());
        });
    }

    private static ProviderItem Item(
        string providerId,
        string externalId,
        ProviderMediaKind kind,
        string title) =>
        new(
            ProviderId: providerId,
            ExternalId: externalId,
            MediaKind: kind,
            Metadata: new ProviderMetadata(title),
            Assets: []);

    private sealed class EndpointProvider :
        IContentProvider,
        IProviderSearch,
        IProviderAcquisitionPlanner,
        IProviderDownloadPolicy
    {
        private readonly Func<ProviderSearchQuery, CancellationToken, Task<ProviderSearchPage>> _search;

        public EndpointProvider(
            string id,
            ProviderCapabilities acquisition,
            ProviderSearchPage page)
            : this(id, acquisition, (_, _) => Task.FromResult(page))
        {
        }

        public EndpointProvider(
            string id,
            ProviderCapabilities acquisition,
            ProviderException failure)
            : this(
                id,
                acquisition,
                (_, _) => Task.FromException<ProviderSearchPage>(failure))
        {
        }

        public EndpointProvider(
            string id,
            ProviderCapabilities acquisition,
            Func<ProviderSearchQuery, CancellationToken, Task<ProviderSearchPage>> search)
        {
            Id = id;
            Capabilities = ProviderCapabilities.Search | acquisition;
            _search = search;
        }

        public string Id { get; }
        public string DisplayName => "Source " + Id;
        public ProviderCapabilities Capabilities { get; }
        public string? RightsNotice => null;

        // Replaced test providers ship in the same state the general sources do
        // (on by default), so #774's enabled-set plumbing does not change the
        // meaning of these discovery tests.
        public bool EnabledByDefault => true;

        public IReadOnlyList<string> AllowedHosts => ["example.com"];
        public long MaxBytesPerPart => 1;
        public long MaxTotalBytes => 1;
        public int MaxParts => 1;
        public int SearchCount { get; private set; }

        public Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct)
        {
            SearchCount++;
            return _search(query, ct);
        }

        public Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
            ProviderAcquisitionRequest request,
            CancellationToken ct) =>
            Task.FromResult<ProviderAcquisitionPlan?>(null);
    }

    private sealed class SnapshotDiscovery(ProviderDiscoveryResult result) : IProviderDiscovery
    {
        public int CallCount { get; private set; }
        public ProviderDiscoveryRequest? LastRequest { get; private set; }

        public Task<ProviderDiscoveryResult> SearchAsync(
            ProviderDiscoveryRequest request,
            CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class SnapshotCoverLookup(ProviderCover? cover) : IProviderCoverLookup
    {
        public int CallCount { get; private set; }
        public string? LastProviderId { get; private set; }
        public string? LastExternalId { get; private set; }

        public Task<ProviderCover?> GetCoverAsync(string providerId, string externalId, CancellationToken ct)
        {
            CallCount++;
            LastProviderId = providerId;
            LastExternalId = externalId;
            return Task.FromResult(cover);
        }
    }
}

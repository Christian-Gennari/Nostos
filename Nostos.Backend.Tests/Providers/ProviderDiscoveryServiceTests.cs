using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class ProviderDiscoveryServiceTests
{
    [Fact]
    public async Task All_SearchesEverySearchableAcquisitionProvider_ButNotAcquireOnlyProviders()
    {
        var ebook = SearchProvider.Ebook("ebooks", Item("ebooks", "e1", ProviderMediaKind.Ebook));
        var audio = SearchProvider.Audio("audio", Item("audio", "a1", ProviderMediaKind.Audiobook));
        var acquireOnly = new AcquireOnlyProvider("hidden");

        var service = CreateService(acquireOnly, audio, ebook);

        var result = await service.SearchAsync(
            new ProviderDiscoveryRequest("classic", null, 20),
            CancellationToken.None);

        ebook.SearchCount.Should().Be(1);
        audio.SearchCount.Should().Be(1);
        acquireOnly.PlanCount.Should().Be(0);
        result.Items.Select(item => item.ProviderId).Should().Equal("audio", "ebooks");
        result.Sources.Select(source => source.ProviderId).Should().Equal("audio", "ebooks");
    }

    [Fact]
    public async Task KindFilter_UsesCapabilities_NotProviderIds()
    {
        var arbitraryEbook = SearchProvider.Ebook("not-gutenberg", Item("not-gutenberg", "same", ProviderMediaKind.Ebook));
        var arbitraryAudio = SearchProvider.Audio("not-librivox", Item("not-librivox", "same", ProviderMediaKind.Audiobook));
        var service = CreateService(arbitraryAudio, arbitraryEbook);

        var ebooks = await service.SearchAsync(
            new ProviderDiscoveryRequest("x", ProviderMediaKind.Ebook, 20),
            CancellationToken.None);
        var audio = await service.SearchAsync(
            new ProviderDiscoveryRequest("x", ProviderMediaKind.Audiobook, 20),
            CancellationToken.None);

        ebooks.Items.Should().ContainSingle().Which.ProviderId.Should().Be("not-gutenberg");
        audio.Items.Should().ContainSingle().Which.ProviderId.Should().Be("not-librivox");
        arbitraryEbook.SeenKinds.Should().Contain(ProviderMediaKind.Ebook);
        arbitraryAudio.SeenKinds.Should().Contain(ProviderMediaKind.Audiobook);
    }

    [Fact]
    public async Task ProviderIds_ExcludedProvidersAreNotSearchedAndHaveNoSourceStatus()
    {
        var alpha = SearchProvider.Ebook("alpha", Item("alpha", "a1", ProviderMediaKind.Ebook));
        var beta = SearchProvider.Ebook("beta", Item("beta", "b1", ProviderMediaKind.Ebook));
        var gamma = SearchProvider.Ebook("gamma", Item("gamma", "g1", ProviderMediaKind.Ebook));

        var result = await CreateService(alpha, beta, gamma).SearchAsync(
            new ProviderDiscoveryRequest("x", null, 20, new HashSet<string> { "beta" }),
            CancellationToken.None);

        alpha.SearchCount.Should().Be(0);
        gamma.SearchCount.Should().Be(0);
        beta.SearchCount.Should().Be(1);
        result.Items.Should().ContainSingle().Which.ProviderId.Should().Be("beta");
        result.Sources.Should().ContainSingle().Which.ProviderId.Should().Be("beta");
    }

    [Fact]
    public async Task ProviderIds_UnknownIdsContributeNothing_AndNullMeansEveryProvider()
    {
        var alpha = SearchProvider.Ebook("alpha", Item("alpha", "a1", ProviderMediaKind.Ebook));
        var service = CreateService(alpha);

        var filtered = await service.SearchAsync(
            new ProviderDiscoveryRequest("x", null, 20, new HashSet<string> { "ghost" }),
            CancellationToken.None);

        alpha.SearchCount.Should().Be(0);
        filtered.Items.Should().BeEmpty();
        filtered.Sources.Should().BeEmpty();

        var all = await service.SearchAsync(
            new ProviderDiscoveryRequest("x", null, 20),
            CancellationToken.None);

        alpha.SearchCount.Should().Be(1);
        all.Items.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
        all.Sources.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
    }

    [Fact]
    public async Task Search_IsConcurrent_AndOneFailureDoesNotCancelSiblings()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = SearchProvider.Ebook("alpha", async (_, ct) =>
        {
            aStarted.SetResult();
            await gate.Task.WaitAsync(ct);
            return new ProviderSearchPage([Item("alpha", "1", ProviderMediaKind.Ebook)]);
        });
        var b = SearchProvider.Ebook("beta", async (_, ct) =>
        {
            bStarted.SetResult();
            await gate.Task.WaitAsync(ct);
            throw ProviderException.UnavailableFor("beta", "test outage");
        });

        var pending = CreateService(a, b).SearchAsync(
            new ProviderDiscoveryRequest("x", null, 20),
            CancellationToken.None);

        await Task.WhenAll(aStarted.Task, bStarted.Task);
        gate.SetResult();

        var result = await pending;

        result.Items.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
        result.Sources.Single(source => source.ProviderId == "alpha").Succeeded.Should().BeTrue();
        var failed = result.Sources.Single(source => source.ProviderId == "beta");
        failed.Succeeded.Should().BeFalse();
        failed.ErrorCode.Should().Be(ProviderException.Unavailable);
    }

    [Fact]
    public async Task HungProvider_TimesOutWithoutBlockingSuccessfulSibling()
    {
        var never = new TaskCompletionSource<ProviderSearchPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var good = SearchProvider.Ebook(
            "alpha",
            Item("alpha", "1", ProviderMediaKind.Ebook));
        var hung = SearchProvider.Ebook("beta", (_, _) => never.Task);

        var result = await CreateService(TimeSpan.FromMilliseconds(60), good, hung)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        result.Items.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
        result.Sources.Single(source => source.ProviderId == "alpha").Succeeded.Should().BeTrue();

        var timedOut = result.Sources.Single(source => source.ProviderId == "beta");
        timedOut.Succeeded.Should().BeFalse();
        timedOut.ErrorCode.Should().Be(ProviderDiscoveryErrorCodes.Timeout);
    }

    [Fact]
    public async Task ProviderLocalTaskCancellation_IsReportedAsTimeout_WithoutCancellingSibling()
    {
        var good = SearchProvider.Ebook(
            "alpha",
            Item("alpha", "1", ProviderMediaKind.Ebook));
        var locallyCancelled = SearchProvider.Ebook(
            "beta",
            (_, _) => Task.FromCanceled<ProviderSearchPage>(
                new CancellationToken(canceled: true)));

        var result = await CreateService(TimeSpan.FromSeconds(1), good, locallyCancelled)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None);

        result.Items.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
        result.Sources.Single(source => source.ProviderId == "alpha").Succeeded.Should().BeTrue();

        var timedOut = result.Sources.Single(source => source.ProviderId == "beta");
        timedOut.Succeeded.Should().BeFalse();
        timedOut.ErrorCode.Should().Be(ProviderDiscoveryErrorCodes.Timeout);
    }

    [Fact]
    public async Task NoticesAndFailuresRemainAttributedToTheirProvider()
    {
        var noticed = SearchProvider.Ebook(
            "alpha",
            _ => new ProviderSearchPage(
                [Item("alpha", "1", ProviderMediaKind.Ebook)],
                Notice: "Prefix search only."));
        var failed = SearchProvider.Ebook(
            "beta",
            (_, _) => throw ProviderException.InvalidResponse("beta", "broken"));

        var result = await CreateService(noticed, failed)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None);

        result.Sources.Single(source => source.ProviderId == "alpha").Notice.Should().Be("Prefix search only.");
        result.Sources.Single(source => source.ProviderId == "beta").ErrorCode
            .Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task UnexpectedFailure_ReportsGenericErrorWithoutLeakingDetail()
    {
        var failed = SearchProvider.Ebook(
            "beta",
            (_, _) => Task.FromException<ProviderSearchPage>(
                new InvalidOperationException("do not expose this")));

        var result = await CreateService(failed)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None);

        var source = result.Sources.Should().ContainSingle().Which;
        source.Succeeded.Should().BeFalse();
        source.ErrorCode.Should().Be(ProviderDiscoveryErrorCodes.SearchFailed);
        source.Notice.Should().BeNull();
    }

    [Fact]
    public async Task Merge_IsStableRoundRobin_IndependentOfRegistrationOrder()
    {
        var alpha = SearchProvider.Ebook(
            "alpha",
            Item("alpha", "a1", ProviderMediaKind.Ebook),
            Item("alpha", "a2", ProviderMediaKind.Ebook));
        var beta = SearchProvider.Ebook(
            "beta",
            Item("beta", "b1", ProviderMediaKind.Ebook),
            Item("beta", "b2", ProviderMediaKind.Ebook));

        var forward = await CreateService(alpha, beta)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None);
        var reverse = await CreateService(beta, alpha)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None);

        var expected = new[] { "alpha:a1", "beta:b1", "alpha:a2", "beta:b2" };
        forward.Items.Select(Key).Should().Equal(expected);
        reverse.Items.Select(Key).Should().Equal(expected);
    }

    [Fact]
    public async Task SameExternalIdFromDifferentProviders_RemainsDistinct()
    {
        var alpha = SearchProvider.Ebook("alpha", Item("alpha", "42", ProviderMediaKind.Ebook));
        var beta = SearchProvider.Ebook("beta", Item("beta", "42", ProviderMediaKind.Ebook));

        var result = await CreateService(alpha, beta)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items.Select(Key).Should().Equal("alpha:42", "beta:42");
    }

    [Fact]
    public async Task HasMore_ReflectsProviderPagingOrAggregateTruncation()
    {
        var providerMore = SearchProvider.Ebook(
            "alpha",
            _ => new ProviderSearchPage([Item("alpha", "a1", ProviderMediaKind.Ebook)], HasMore: true));

        var reported = await CreateService(providerMore)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None);
        reported.HasMore.Should().BeTrue();

        var alpha = SearchProvider.Ebook(
            "alpha",
            Item("alpha", "a1", ProviderMediaKind.Ebook),
            Item("alpha", "a2", ProviderMediaKind.Ebook));
        var beta = SearchProvider.Ebook(
            "beta",
            Item("beta", "b1", ProviderMediaKind.Ebook),
            Item("beta", "b2", ProviderMediaKind.Ebook));

        var truncated = await CreateService(alpha, beta)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 2), CancellationToken.None);

        truncated.Items.Should().HaveCount(2);
        truncated.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task RequestCancellation_Propagates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = SearchProvider.Ebook(
            "alpha",
            async (_, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ProviderSearchPage([]);
            });
        using var cts = new CancellationTokenSource();

        var pending = CreateService(TimeSpan.FromSeconds(5), provider)
            .SearchAsync(new ProviderDiscoveryRequest("x", null, 20), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSearchTimeout_FallsBackToFiniteDefault(int milliseconds)
    {
        var options = new ProviderDiscoveryOptions
        {
            SearchTimeout = TimeSpan.FromMilliseconds(milliseconds),
        };

        options.EffectiveSearchTimeout.Should().Be(ProviderDiscoveryOptions.DefaultSearchTimeout);
        options.EffectiveSearchTimeout.Should().BeGreaterThan(TimeSpan.Zero);
    }

    // ------------------------------------------------------------------
    // Interactive aggregate budget (#641)
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_hung_provider_cannot_hold_the_aggregate_search_for_the_per_provider_deadline()
    {
        var hungToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var good = SearchProvider.Ebook("alpha", Item("alpha", "1", ProviderMediaKind.Ebook));
        var hung = SearchProvider.Ebook("beta", async (_, ct) =>
        {
            hungToken.SetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProviderSearchPage([]);
        });
        var service = CreateService(
            new ProviderDiscoveryOptions
            {
                SearchTimeout = TimeSpan.FromSeconds(12),
                AggregateSearchTimeout = TimeSpan.FromMilliseconds(150),
            },
            good,
            hung);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.SearchAsync(
            new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the aggregate budget, not the 12 s provider deadline, bounds the response");
        result.Items.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
        result.Sources.Single(source => source.ProviderId == "beta").ErrorCode
            .Should().Be(ProviderDiscoveryErrorCodes.Timeout);
        (await hungToken.Task).IsCancellationRequested.Should().BeTrue("pending provider work is cancelled at the deadline");
    }

    [Fact]
    public async Task A_provider_finishing_after_the_deadline_cannot_change_the_returned_response()
    {
        var late = new TaskCompletionSource<ProviderSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var good = SearchProvider.Ebook("alpha", Item("alpha", "1", ProviderMediaKind.Ebook));
        var slow = SearchProvider.Ebook("beta", (_, _) => late.Task);
        var service = CreateService(
            new ProviderDiscoveryOptions { AggregateSearchTimeout = TimeSpan.FromMilliseconds(100) },
            good,
            slow);

        var result = await service.SearchAsync(
            new ProviderDiscoveryRequest("x", null, 20), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        var itemsBefore = result.Items.Select(Key).ToList();

        late.SetResult(new ProviderSearchPage([Item("beta", "late", ProviderMediaKind.Ebook)]));
        await Task.Delay(50);

        result.Items.Select(Key).Should().Equal(itemsBefore).And.Equal("alpha:1");
        result.Sources.Single(source => source.ProviderId == "beta").Succeeded.Should().BeFalse();
    }

    [Fact]
    public void The_provider_deadline_is_the_smaller_of_the_provider_and_aggregate_budgets()
    {
        new ProviderDiscoveryOptions().EffectiveProviderDeadline
            .Should().Be(ProviderDiscoveryOptions.DefaultAggregateSearchTimeout);
        ProviderDiscoveryOptions.DefaultAggregateSearchTimeout
            .Should().BeLessThan(ProviderDiscoveryOptions.DefaultSearchTimeout);

        new ProviderDiscoveryOptions { SearchTimeout = TimeSpan.FromSeconds(1) }.EffectiveProviderDeadline
            .Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveAggregateSearchTimeout_FallsBackToFiniteDefault(int milliseconds)
    {
        var options = new ProviderDiscoveryOptions
        {
            AggregateSearchTimeout = TimeSpan.FromMilliseconds(milliseconds),
        };

        options.EffectiveAggregateSearchTimeout.Should().Be(ProviderDiscoveryOptions.DefaultAggregateSearchTimeout);
        options.EffectiveProviderDeadline.Should().BeGreaterThan(TimeSpan.Zero);
    }

    private static ProviderDiscoveryService CreateService(
        ProviderDiscoveryOptions options,
        params IContentProvider[] providers) =>
        new(
            new ProviderRegistry(providers),
            Options.Create(options),
            NullLogger<ProviderDiscoveryService>.Instance);

    private static ProviderDiscoveryService CreateService(params IContentProvider[] providers) =>
        CreateService(ProviderDiscoveryOptions.DefaultSearchTimeout, providers);

    private static ProviderDiscoveryService CreateService(
        TimeSpan searchTimeout,
        params IContentProvider[] providers) =>
        new(
            new ProviderRegistry(providers),
            Options.Create(new ProviderDiscoveryOptions { SearchTimeout = searchTimeout }),
            NullLogger<ProviderDiscoveryService>.Instance);

    private static ProviderItem Item(string providerId, string externalId, ProviderMediaKind kind) =>
        new(
            ProviderId: providerId,
            ExternalId: externalId,
            MediaKind: kind,
            Metadata: new ProviderMetadata(Title: providerId + "-" + externalId),
            Assets: []);

    private static string Key(ProviderItem item) => item.ProviderId + ":" + item.ExternalId;

    private sealed class SearchProvider : IContentProvider, IProviderSearch, IProviderAcquisitionPlanner, IProviderDownloadPolicy
    {
        private readonly Func<ProviderSearchQuery, CancellationToken, Task<ProviderSearchPage>> _search;

        private SearchProvider(
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
        public IReadOnlyList<string> AllowedHosts => ["example.com"];
        public long MaxBytesPerPart => 1;
        public long MaxTotalBytes => 1;
        public int MaxParts => 1;
        public int SearchCount { get; private set; }
        public List<ProviderMediaKind?> SeenKinds { get; } = [];

        public static SearchProvider Ebook(string id, params ProviderItem[] items) =>
            Ebook(id, _ => new ProviderSearchPage(items));

        public static SearchProvider Ebook(string id, Func<ProviderSearchQuery, ProviderSearchPage> search) =>
            Ebook(id, (query, _) => Task.FromResult(search(query)));

        public static SearchProvider Ebook(
            string id,
            Func<ProviderSearchQuery, CancellationToken, Task<ProviderSearchPage>> search) =>
            new(id, ProviderCapabilities.EbookAcquisition, search);

        public static SearchProvider Audio(string id, params ProviderItem[] items) =>
            new(
                id,
                ProviderCapabilities.AudiobookAcquisition,
                (_, _) => Task.FromResult(new ProviderSearchPage(items)));

        public async Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct)
        {
            SearchCount++;
            SeenKinds.Add(query.Kind);
            return await _search(query, ct);
        }

        public Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
            ProviderAcquisitionRequest request,
            CancellationToken ct) =>
            Task.FromResult<ProviderAcquisitionPlan?>(null);
    }

    private sealed class AcquireOnlyProvider : IContentProvider, IProviderAcquisitionPlanner, IProviderDownloadPolicy
    {
        public AcquireOnlyProvider(string id) => Id = id;

        public string Id { get; }
        public string DisplayName => "Source " + Id;
        public ProviderCapabilities Capabilities => ProviderCapabilities.EbookAcquisition;
        public string? RightsNotice => null;
        public IReadOnlyList<string> AllowedHosts => ["example.com"];
        public long MaxBytesPerPart => 1;
        public long MaxTotalBytes => 1;
        public int MaxParts => 1;
        public int PlanCount { get; private set; }

        public Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
            ProviderAcquisitionRequest request,
            CancellationToken ct)
        {
            PlanCount++;
            return Task.FromResult<ProviderAcquisitionPlan?>(null);
        }
    }
}

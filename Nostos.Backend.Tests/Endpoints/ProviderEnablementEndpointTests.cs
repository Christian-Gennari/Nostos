using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Server-side enforcement of the user's provider choices (issue #774): a
/// disabled provider is indistinguishable from an unknown one on every
/// provider-specific route, aggregate discovery both filters and is asked with
/// the enabled set, and jobs already started keep working.
/// </summary>
public sealed class ProviderEnablementEndpointTests
{
    [Fact]
    public async Task A_disabled_provider_is_provider_unknown_on_every_provider_route()
    {
        var alpha = new EndpointProvider("alpha");
        await using var app = CreateHost(alpha);
        using var client = app.CreateClient();

        // Start enabled, then disable through the settings surface.
        var put = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/alpha",
            new ProviderPreferenceUpdateDto(false));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var search = await client.GetAsync("/api/providers/alpha/search?query=classic");
        search.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTitleAsync(search)).Should().Be("provider_unknown");

        var item = await client.GetAsync("/api/providers/alpha/items/1");
        item.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTitleAsync(item)).Should().Be("provider_unknown");

        var cover = await client.GetAsync("/api/providers/alpha/items/1/cover");
        cover.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTitleAsync(cover)).Should().Be("provider_unknown");

        var acquire = await client.PostAsJsonAsync(
            "/api/providers/alpha/acquire",
            new { externalId = "1" });
        acquire.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTitleAsync(acquire)).Should().Be("provider_unknown");

        // The consumer list omits it, and the provider itself was never touched.
        var consumer = await client.GetFromJsonAsync<List<ProviderSummaryDto>>("/api/providers");
        consumer.Should().BeEmpty();
        alpha.SearchCount.Should().Be(0);
        alpha.CatalogCount.Should().Be(0);
    }

    [Fact]
    public async Task Aggregate_search_is_asked_with_the_enabled_set_and_drops_disabled_rows()
    {
        var alpha = new EndpointProvider("alpha");
        var beta = new EndpointProvider("beta");
        var snapshot = new SnapshotDiscovery(new ProviderDiscoveryResult(
            [
                Item("alpha", "1", "Alpha result"),
                Item("beta", "2", "Beta result"),
            ],
            HasMore: false,
            Sources:
            [
                new ProviderDiscoverySourceStatus("alpha", "Alpha", Succeeded: true),
                new ProviderDiscoverySourceStatus("beta", "Beta", Succeeded: true),
            ]));

        await using var app = CreateHost([alpha, beta], snapshot);
        using var client = app.CreateClient();

        var put = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/beta",
            new ProviderPreferenceUpdateDto(false));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.GetFromJsonAsync<ProviderDiscoverySearchResultDto>(
            "/api/providers/search?query=classic");

        snapshot.LastRequest!.ProviderIds.Should().BeEquivalentTo(["alpha"]);
        response!.Items.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
        response.Sources.Should().ContainSingle().Which.ProviderId.Should().Be("alpha");
    }

    [Fact]
    public async Task A_job_started_while_enabled_stays_readable_and_cancellable_after_disable()
    {
        var alpha = new EndpointProvider("alpha");
        var jobs = new StubJobManager();
        await using var app = CreateHost(alpha, jobs);
        using var client = app.CreateClient();

        var started = await client.PostAsJsonAsync(
            "/api/providers/alpha/acquire",
            new { externalId = "1" });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = await started.Content.ReadFromJsonAsync<ProviderAcquisitionDto>();
        job!.ProviderId.Should().Be("alpha");

        var put = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/alpha",
            new ProviderPreferenceUpdateDto(false));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // Disabling only affects future discovery/acquisition: the in-flight job
        // is still readable and cancellable.
        var status = await client.GetAsync($"/api/providers/acquisitions/{job.JobId}");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        (await status.Content.ReadFromJsonAsync<ProviderAcquisitionDto>())!
            .ProviderId.Should().Be("alpha");

        var cancel = await client.DeleteAsync($"/api/providers/acquisitions/{job.JobId}");
        cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_imported_book_stays_readable_with_its_provenance_after_its_source_is_disabled()
    {
        var alpha = new EndpointProvider("alpha");
        using var factory = new LibraryEndpointFactory();
        var bookId = SeedAcquiredBook(factory.DatabasePath, providerId: "alpha");

        await using var app = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => ReplaceProviders(services, [alpha], null, null)));
        using var client = app.CreateClient();

        var before = await client.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
        before!.Source!.ProviderId.Should().Be("alpha");

        var put = await client.PutAsJsonAsync(
            $"{ProviderSettingsEndpoints.Route}/alpha",
            new ProviderPreferenceUpdateDto(false));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // Disabling affects future discovery/acquisition only: the book, its
        // file and its provenance are untouched and the reader can still open it.
        var after = await client.GetFromJsonAsync<BookDto>($"/api/books/{bookId}");
        after.Should().NotBeNull();
        after!.Id.Should().Be(bookId);
        after.Source!.ProviderId.Should().Be("alpha");
        after.Source.ProviderDisplayName.Should().Be("Source alpha");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static WebApplicationFactory<Program> CreateHost(
        EndpointProvider provider,
        StubJobManager? jobs = null) =>
        CreateHost([provider], discovery: null, jobs);

    private static WebApplicationFactory<Program> CreateHost(
        IReadOnlyList<EndpointProvider> providers,
        StubJobManager? jobs = null) =>
        CreateHost(providers, discovery: null, jobs);

    private static WebApplicationFactory<Program> CreateHost(
        IReadOnlyList<EndpointProvider> providers,
        SnapshotDiscovery snapshot) =>
        CreateHost(providers, snapshot, jobs: null);

    private static WebApplicationFactory<Program> CreateHost(
        IReadOnlyList<EndpointProvider> providers,
        SnapshotDiscovery? discovery = null,
        StubJobManager? jobs = null)
    {
        var factory = new LibraryEndpointFactory();
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => ReplaceProviders(services, providers, discovery, jobs)));
    }

    private static void ReplaceProviders(
        IServiceCollection services,
        IReadOnlyList<EndpointProvider> providers,
        SnapshotDiscovery? discovery,
        StubJobManager? jobs)
    {
        services.RemoveAll<IContentProvider>();
        services.RemoveAll<IProviderRegistry>();
        services.RemoveAll<ProviderDiscoveryService>();
        services.RemoveAll<IProviderDiscovery>();

        foreach (var provider in providers)
            services.AddSingleton(typeof(IContentProvider), provider);

        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        if (discovery is not null)
        {
            services.AddSingleton<IProviderDiscovery>(discovery);
        }
        else
        {
            services.AddSingleton<ProviderDiscoveryService>();
            services.AddSingleton<IProviderDiscovery>(sp =>
                sp.GetRequiredService<ProviderDiscoveryService>());
        }

        if (jobs is not null)
        {
            services.RemoveAll<IAcquisitionJobManager>();
            services.AddSingleton<IAcquisitionJobManager>(jobs);
        }
    }

    private static Guid SeedAcquiredBook(string databasePath, string providerId)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        using var db = new NostosDbContext(options);
        var now = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Acquired Book",
            Author = "Acquired Author",
            NormalizedTitle = "ACQUIRED BOOK",
            NormalizedAuthor = "ACQUIRED AUTHOR",
            CreatedAt = now,
        };
        var book = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = "Acquired Book",
            Author = "Acquired Author",
            CreatedAt = now,
        };

        db.Works.Add(work);
        db.Books.Add(book);
        db.BookAcquisitions.Add(new BookAcquisitionModel
        {
            BookId = book.Id,
            ProviderId = providerId,
            ProviderDisplayName = "Source " + providerId,
            ExternalId = "1",
            AssetId = "epub",
            AssetFormat = "epub",
            ImportedExtension = ".epub",
            AcquiredAt = now,
        });
        db.SaveChanges();
        return book.Id;
    }

    private static ProviderItem Item(string providerId, string externalId, string title) =>
        new(
            ProviderId: providerId,
            ExternalId: externalId,
            MediaKind: ProviderMediaKind.Ebook,
            Metadata: new ProviderMetadata(title),
            Assets: []);

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response)
    {
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        return document.GetProperty("title").GetString();
    }

    private sealed class EndpointProvider :
        IContentProvider,
        IProviderSearch,
        IProviderCatalog,
        IProviderAcquisitionPlanner,
        IProviderDownloadPolicy
    {
        public EndpointProvider(string id) => Id = id;

        public string Id { get; }
        public string DisplayName => "Source " + Id;
        public ProviderCapabilities Capabilities =>
            ProviderCapabilities.Search
            | ProviderCapabilities.ItemRetrieval
            | ProviderCapabilities.EbookAcquisition;
        public string? RightsNotice => null;

        // Shipped providers default on, so a replaced test provider starts in
        // the same state the four general sources ship in.
        public bool EnabledByDefault => true;

        public IReadOnlyList<string> AllowedHosts => ["example.com"];
        public long MaxBytesPerPart => 1;
        public long MaxTotalBytes => 1;
        public int MaxParts => 1;

        public int SearchCount { get; private set; }
        public int CatalogCount { get; private set; }

        public Task<ProviderSearchPage> SearchAsync(ProviderSearchQuery query, CancellationToken ct)
        {
            SearchCount++;
            return Task.FromResult(new ProviderSearchPage([]));
        }

        public Task<ProviderItem?> GetItemAsync(string externalId, CancellationToken ct)
        {
            CatalogCount++;
            return Task.FromResult<ProviderItem?>(null);
        }

        public Task<ProviderAcquisitionPlan?> PlanAcquisitionAsync(
            ProviderAcquisitionRequest request,
            CancellationToken ct) =>
            Task.FromResult<ProviderAcquisitionPlan?>(null);
    }

    private sealed class SnapshotDiscovery(ProviderDiscoveryResult result) : IProviderDiscovery
    {
        public ProviderDiscoveryRequest? LastRequest { get; private set; }

        public Task<ProviderDiscoveryResult> SearchAsync(
            ProviderDiscoveryRequest request,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// A job manager the endpoints can read without running any acquisition:
    /// Start queues a job and Cancel closes it, so the test proves the job
    /// surface never re-checks enablement.
    /// </summary>
    private sealed class StubJobManager : IAcquisitionJobManager
    {
        private AcquisitionJobStatus? _job;

        public AcquisitionJobStatus Start(AcquisitionRequest request)
        {
            _job = new AcquisitionJobStatus(
                JobId: "stub-job-1",
                State: AcquisitionJobState.Queued,
                Stage: "queued",
                Percent: 0,
                Detail: null,
                ProviderId: request.ProviderId,
                ExternalId: request.ExternalId,
                AssetId: request.AssetId,
                BookId: null,
                ErrorCode: null,
                Message: null,
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow);
            return _job;
        }

        public AcquisitionJobStatus? Get(string jobId) =>
            _job?.JobId == jobId ? _job : null;

        public IReadOnlyList<AcquisitionJobStatus> List() =>
            _job is null ? [] : [_job];

        public IReadOnlyList<AcquisitionJobStatus> ListActive() =>
            _job is { IsFinished: false } ? [_job] : [];

        public bool Cancel(string jobId)
        {
            if (_job?.JobId != jobId || _job.IsFinished)
                return false;

            _job = _job with { State = AcquisitionJobState.Cancelled, UpdatedAt = DateTime.UtcNow };
            return true;
        }
    }
}

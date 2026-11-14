using FluentAssertions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Providers.LibriVox;
using Nostos.Backend.Providers.Litteraturbanken;
using Nostos.Backend.Providers.StandardEbooks;
using Nostos.Backend.Providers.Wikisource;
using Nostos.Backend.Serialization;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Markdown;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.ReadingList;
using Nostos.Backend.Services.Recovery;
using Nostos.Backend.Services.Writings;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Product.Composition;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Composition;

public sealed class NostosProductCompositionTests
{
    [Fact]
    public void AddNostosProduct_returns_descriptor_matching_registered_option_singletons()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assistant:Model"] = "gpt-4o-mini",
                ["Speech:Model"] = "whisper-1",
                ["Opds:PageSize"] = "25",
                ["Opds:PublicBaseUrl"] = "https://books.example.com/",
            })
            .Build();

        var services = new ServiceCollection();
        var descriptor = services.AddNostosProduct(configuration);

        using var provider = services.BuildServiceProvider();

        descriptor.Assistant.Should().BeSameAs(provider.GetRequiredService<AssistantOptions>());
        descriptor.Speech.Should().BeSameAs(provider.GetRequiredService<SpeechOptions>());
        descriptor.Opds.Should().BeSameAs(provider.GetRequiredService<OpdsOptions>());
        descriptor.Assistant.Model.Should().Be("gpt-4o-mini");
        descriptor.Speech.Model.Should().Be("whisper-1");
        descriptor.Opds.PageSize.Should().Be(25);
        descriptor.Opds.PublicBaseUrl.Should().Be("https://books.example.com");
    }

    [Theory]
    [InlineData(0, OpdsOptions.DefaultPageSize)]
    [InlineData(-10, OpdsOptions.DefaultPageSize)]
    [InlineData(9999, OpdsOptions.MaxPageSize)]
    [InlineData(120, 120)]
    public void AddNostosProduct_normalizes_opds_page_size(int configured, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Opds:PageSize"] = configured.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        var descriptor = services.AddNostosProduct(configuration);

        descriptor.Opds.PageSize.Should().Be(expected);
    }

    [Fact]
    public void AddNostosProduct_rejects_invalid_opds_public_base_url()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Opds:PublicBaseUrl"] = "ftp://invalid.example.com",
            })
            .Build();

        var act = () => new ServiceCollection().AddNostosProduct(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Opds:PublicBaseUrl*");
    }

    [Fact]
    public void AddNostosProduct_normalizes_alignment_probe_settings_and_retention_options()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alignment:MaxProbes"] = "99",
                ["Alignment:ProbeSeconds"] = "2",
                ["Alignment:LeadInSeconds"] = "4",
                ["LibraryReceiptRetention:RetentionDays"] = "-1",
                ["LibraryReceiptRetention:MaximumReceipts"] = "50000000",
                ["LibraryReceiptRetention:CleanupIntervalHours"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddNostosProduct(configuration);

        using var provider = services.BuildServiceProvider();

        var alignment = provider.GetRequiredService<AlignmentOptions>();
        alignment.MaxProbes.Should().Be(8);
        alignment.ProbeSeconds.Should().Be(5);
        alignment.LeadInSeconds.Should().Be(4);

        var retention = provider.GetRequiredService<LibraryReceiptRetentionOptions>();
        retention.RetentionDays.Should().Be(LibraryReceiptRetentionOptions.MinRetentionDays);
        retention.MaximumReceipts.Should().Be(LibraryReceiptRetentionOptions.MaxMaximumReceipts);
        retention.CleanupIntervalHours.Should().Be(LibraryReceiptRetentionOptions.MinCleanupIntervalHours);
    }

    [Theory]
    [InlineData("ClipSeconds", "0", "*Alignment:ClipSeconds*")]
    [InlineData("ClipSeconds", "61", "*Alignment:ClipSeconds*")]
    [InlineData("MaxEpubBytes", "0", "*Alignment:MaxEpubBytes*")]
    [InlineData("MaxCachedCharacters", "0", "*Alignment:MaxCachedCharacters*")]
    [InlineData("MatchToleranceCharacters", "-1", "*Alignment:MatchToleranceCharacters*")]
    [InlineData("MatchToleranceCharacters", "501", "*Alignment:MatchToleranceCharacters*")]
    [InlineData("ConsistencyToleranceCharacters", "-1", "*Alignment:ConsistencyToleranceCharacters*")]
    [InlineData("ConsistencyToleranceCharacters", "501", "*Alignment:ConsistencyToleranceCharacters*")]
    [InlineData("LeadInSeconds", "35", "*Alignment:LeadInSeconds*")]
    [InlineData("MaximumLocalRateRatio", "0.5", "*Alignment:MaximumLocalRateRatio*")]
    [InlineData("MaximumLocalRateRatio", "101", "*Alignment:MaximumLocalRateRatio*")]
    public void AddNostosProduct_rejects_invalid_alignment_options(
        string key,
        string value,
        string expectedMessagePattern)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Alignment:{key}"] = value,
            })
            .Build();

        var act = () => new ServiceCollection().AddNostosProduct(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(expectedMessagePattern);
    }

    [Fact]
    public void ValidateAndNormalizeAlignmentOptions_handles_nan_probe_and_invalid_clip_seconds()
    {
        var nanProbe = new AlignmentOptions { ProbeSeconds = double.NaN, MaxProbes = 0 };
        NostosProductOptionsSetup.ValidateAndNormalizeAlignmentOptions(nanProbe);
        nanProbe.ProbeSeconds.Should().Be(20);
        nanProbe.MaxProbes.Should().Be(1);

        var nanClip = new AlignmentOptions { ClipSeconds = double.NaN };
        var act = () => NostosProductOptionsSetup.ValidateAndNormalizeAlignmentOptions(nanClip);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Alignment:ClipSeconds*");
    }

    [Fact]
    public void AddNostosProduct_configures_feature_options_and_utc_json_converter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Readwise:BaseUrl"] = "https://readwise.example",
                ["Acquisition:DownloadConcurrency"] = "6",
                ["ProviderDiscovery:SearchTimeout"] = "00:00:25",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddNostosProduct(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ReadwiseOptions>>().Value.BaseUrl
            .Should().Be("https://readwise.example");
        provider.GetRequiredService<IOptions<AcquisitionOptions>>().Value.DownloadConcurrency
            .Should().Be(6);
        provider.GetRequiredService<IOptions<ProviderDiscoveryOptions>>().Value.SearchTimeout
            .Should().Be(TimeSpan.FromSeconds(25));
        provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions.Converters
            .Should().Contain(converter => converter is UtcDateTimeJsonConverter);
    }

    [Fact]
    public void AddNostosProduct_registers_expected_service_lifetimes_across_all_modules()
    {
        var services = new ServiceCollection();
        services.AddNostosProduct(new ConfigurationBuilder().Build());

        // Options singletons
        AssertRegistered(services, typeof(AssistantOptions), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(SpeechOptions), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(EmbeddingOptions), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(OpdsOptions), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(BookTextOptions), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(AlignmentOptions), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(LibraryReceiptRetentionOptions), ServiceLifetime.Singleton);

        // BookText
        services.Where(d => d.ServiceType == typeof(IBookTextExtractor))
            .Select(d => (d.ImplementationType, d.Lifetime))
            .Should().Equal(
                (typeof(PdfBookTextExtractor), ServiceLifetime.Singleton),
                (typeof(EpubBookTextExtractor), ServiceLifetime.Singleton));
        AssertRegistered(services, typeof(IBookTextIndex), typeof(NoOpBookTextIndex), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IBookDerivedArtifactStorage), typeof(NoOpBookTextArtifactStorage), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IBookTextIngestionScheduler), typeof(NoOpBookTextIngestionScheduler), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IBookTextLifecycle), typeof(BookTextLifecycle), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(BookTextIngestionEngine), typeof(BookTextIngestionEngine), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IEmbeddingProvider), typeof(NoOpEmbeddingProvider), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IBookTextEmbeddingIndex), typeof(NoOpBookTextEmbeddingIndex), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(BookTextEmbeddingEngine), typeof(BookTextEmbeddingEngine), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IBookTextSearchService), typeof(BookTextSearchService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IBookTextQuoteFinder), typeof(BookTextQuoteFinder), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IBookPassageAnchors), typeof(BookPassageAnchors), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(BookTextBackfillService), typeof(BookTextBackfillService), ServiceLifetime.Scoped);

        // Alignment
        AssertRegistered(services, typeof(AlignmentDocumentCache), typeof(AlignmentDocumentCache), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IAlignmentDocumentProvider), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IAudioClipCutter), typeof(FfmpegAudioClipCutter), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IAlignmentAnchorStore), typeof(EfAlignmentAnchorStore), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IAlignmentAudioRevisionProvider), typeof(AlignmentAudioRevisionProvider), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IAlignmentService), typeof(AlignmentService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IAlignmentTranscriber), typeof(VoiceAlignmentTranscriber), ServiceLifetime.Scoped);

        // Library, repositories, imports & knowledge
        AssertRegistered(services, typeof(IBookRepository), typeof(BookRepository), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(INoteRepository), typeof(NoteRepository), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(ITopicRepository), typeof(TopicRepository), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IWritingRepository), typeof(WritingRepository), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(WritingTreeService), typeof(WritingTreeService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(BookFileCommit), typeof(BookFileCommit), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(BookTrackUpload), typeof(BookTrackUpload), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(BookLookupService), typeof(BookLookupService), ServiceLifetime.Scoped);
        // The facade is registered through a factory (two public constructors
        // would be ambiguous to the container); the factory builds LibraryService.
        AssertRegistered(services, typeof(ILibraryService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(ILibraryChangesetService), ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(ILibraryService)).ImplementationFactory.Should().NotBeNull();
        services.Single(d => d.ServiceType == typeof(ILibraryChangesetService)).ImplementationFactory.Should().NotBeNull();
        // LibraryService is a facade; its focused collaborators are resolved
        // from the container too (issue #1611).
        AssertRegistered(services, typeof(LibraryMutationExecutor), typeof(LibraryMutationExecutor), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(LibraryReadService), typeof(LibraryReadService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(LibraryBookCommandService), typeof(LibraryBookCommandService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(LibraryProgressService), typeof(LibraryProgressService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(LibraryWorkService), typeof(LibraryWorkService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(LibraryCollectionService), typeof(LibraryCollectionService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(LibraryChangesetService), typeof(LibraryChangesetService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(LibraryReceiptRetentionService), typeof(LibraryReceiptRetentionService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(MediaMetadataService), typeof(MediaMetadataService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(AudiobookPackageService), typeof(AudiobookPackageService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(NoteProcessorService), typeof(NoteProcessorService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(INoteService), typeof(NoteService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IReadingListService), typeof(ReadingListService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(KoreaderNoteImportService), typeof(KoreaderNoteImportService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(HighlightImportService), typeof(HighlightImportService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(ReadwiseApiClient), typeof(ReadwiseApiClient), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(ReadwiseConnectionService), typeof(ReadwiseConnectionService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(ReadwiseImportService), typeof(ReadwiseImportService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(ReadingHistoryImportService), typeof(ReadingHistoryImportService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(CalibreImportService), typeof(CalibreImportService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(CalibreUploadOwnerScope), typeof(CalibreUploadOwnerScope), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(CalibreImportJob), typeof(CalibreImportJob), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IKnowledgeRetrievalService), typeof(KnowledgeRetrievalService), ServiceLifetime.Scoped);

        // Portability & migration
        AssertRegistered(services, typeof(IPortableArchiveService), typeof(PortableArchiveService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IPortableArchiveExporter), typeof(DefaultPortableArchiveExporter), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IMarkdownNotesExporter), typeof(MarkdownNotesExporter), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IManagedBackupCatalog), typeof(NotSupportedManagedBackupCatalog), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IManagedBackupRestorer), typeof(NotSupportedManagedBackupRestorer), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IMigrationJobStore), typeof(EfMigrationJobStore), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IMigrationPhaseAvailability), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(ILibraryDestinationRevisionProvider), typeof(LibraryStateDestinationRevisionProvider), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(PortableArchiveReader), typeof(PortableArchiveReader), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(IPortableImportPreparer), ServiceLifetime.Scoped);

        // Assistant
        AssertRegistered(services, typeof(IThoughtProcessor), typeof(ThoughtProcessor), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(AssistantChangesetStore), typeof(AssistantChangesetStore), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IAssistantOwner), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(AssistantChangesetCoordinator), typeof(AssistantChangesetCoordinator), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(AssistantContinuationStore), typeof(AssistantContinuationStore), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(AssistantTurnExecutionRegistry), typeof(AssistantTurnExecutionRegistry), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(AssistantOrchestrator), typeof(AssistantOrchestrator), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(AssistantCapabilityRegistry), ServiceLifetime.Scoped);

        // Providers & acquisition
        AssertRegistered(services, typeof(IProviderRegistry), typeof(ProviderRegistry), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IProviderEnablementService), typeof(ProviderEnablementService), ServiceLifetime.Scoped);
        AssertRegistered(services, typeof(ProviderDiscoveryService), typeof(ProviderDiscoveryService), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IProviderDiscovery), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IProviderCoverLookup), typeof(ProviderCoverLookup), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IAcquisitionWorkingRootProvider), typeof(DefaultAcquisitionWorkingRootProvider), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IAudioDurationProbe), typeof(AtlAudioDurationProbe), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IProviderContentDownloader), typeof(ProviderContentDownloader), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(BookFileMutationGate), typeof(BookFileMutationGate), ServiceLifetime.Singleton);
        AssertRegistered(services, typeof(IAcquisitionService), typeof(AcquisitionService), ServiceLifetime.Scoped);

        services.Where(d => d.ServiceType == typeof(IContentProvider))
            .Select(d => (d.ImplementationType, d.Lifetime))
            .Should().Equal(
                (typeof(GutenbergProvider), ServiceLifetime.Singleton),
                (typeof(StandardEbooksProvider), ServiceLifetime.Singleton),
                (typeof(WikisourceProvider), ServiceLifetime.Singleton),
                (typeof(LitteraturbankenProvider), ServiceLifetime.Singleton),
                (typeof(LibriVoxProvider), ServiceLifetime.Singleton));
    }

    [Fact]
    public void Host_registrations_before_AddNostosProduct_win_over_TryAdd_fallbacks()
    {
        var services = new ServiceCollection();
        var hostEmbedding = new StubEmbeddingProvider();
        var hostOwner = new StubAssistantOwner();
        var hostPhaseAvailability = new StubMigrationPhaseAvailability();
        var hostWorkingRoot = new StubAcquisitionWorkingRootProvider();

        services.AddSingleton<IEmbeddingProvider>(hostEmbedding);
        services.AddScoped<IAssistantOwner>(_ => hostOwner);
        services.AddSingleton<IMigrationPhaseAvailability>(hostPhaseAvailability);
        services.AddSingleton<IAcquisitionWorkingRootProvider>(hostWorkingRoot);

        services.AddNostosProduct(new ConfigurationBuilder().Build());

        services.Count(d => d.ServiceType == typeof(IEmbeddingProvider)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IAssistantOwner)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IMigrationPhaseAvailability)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IAcquisitionWorkingRootProvider)).Should().Be(1);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        provider.GetRequiredService<IEmbeddingProvider>().Should().BeSameAs(hostEmbedding);
        scope.ServiceProvider.GetRequiredService<IAssistantOwner>().Should().BeSameAs(hostOwner);
        provider.GetRequiredService<IMigrationPhaseAvailability>().Should().BeSameAs(hostPhaseAvailability);
        provider.GetRequiredService<IAcquisitionWorkingRootProvider>().Should().BeSameAs(hostWorkingRoot);
    }

    [Fact]
    public void AddNostosProduct_configures_all_named_http_clients()
    {
        var services = new ServiceCollection();
        services.AddNostosProduct(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var readwise = factory.CreateClient(ReadwiseApiClient.HttpClientName);
        readwise.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        readwise.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("Nostos/1.0");

        using var bookLookup = factory.CreateClient(BookLookupService.HttpClientName);
        bookLookup.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        bookLookup.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("Nostos/1.0");

        using var downloader = factory.CreateClient(ProviderContentDownloader.HttpClientName);
        downloader.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        downloader.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("Nostos/1.0");

        using var gutenberg = factory.CreateClient(GutenbergProvider.HttpClientName);
        gutenberg.BaseAddress.Should().Be(new Uri(GutenbergCatalog.BaseUrl));
        gutenberg.Timeout.Should().Be(TimeSpan.FromSeconds(20));

        using var gutenbergSnapshot = factory.CreateClient(GutenbergProvider.SnapshotHttpClientName);
        gutenbergSnapshot.BaseAddress.Should().Be(new Uri(GutenbergCatalog.BaseUrl));
        gutenbergSnapshot.Timeout.Should().Be(Timeout.InfiniteTimeSpan);

        using var standardEbooks = factory.CreateClient(StandardEbooksProvider.HttpClientName);
        standardEbooks.BaseAddress.Should().Be(new Uri(StandardEbooksCatalog.BaseUrl));
        standardEbooks.Timeout.Should().Be(TimeSpan.FromSeconds(20));
        standardEbooks.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("Nostos");
        standardEbooks.DefaultRequestHeaders.Accept.ToString().Should().Contain(StandardEbooksProvider.OpdsAccept);

        using var standardEbooksSnapshot = factory.CreateClient(StandardEbooksProvider.SnapshotHttpClientName);
        standardEbooksSnapshot.BaseAddress.Should().Be(new Uri(StandardEbooksCatalog.BaseUrl));
        standardEbooksSnapshot.Timeout.Should().Be(TimeSpan.FromMinutes(2));

        using var wikisource = factory.CreateClient(WikisourceProvider.HttpClientName);
        wikisource.BaseAddress.Should().Be(new Uri(WikisourceCatalog.BaseUrl));
        wikisource.Timeout.Should().Be(TimeSpan.FromSeconds(20));

        using var litteraturbanken = factory.CreateClient(LitteraturbankenProvider.HttpClientName);
        litteraturbanken.BaseAddress.Should().Be(new Uri(LitteraturbankenProvider.BaseUrl));
        litteraturbanken.Timeout.Should().Be(TimeSpan.FromSeconds(20));
        litteraturbanken.DefaultRequestHeaders.Accept.ToString().Should().Contain("application/json");

        using var librivox = factory.CreateClient(LibriVoxProvider.HttpClientName);
        librivox.BaseAddress.Should().Be(new Uri(LibriVoxCatalog.BaseUrl));
        librivox.Timeout.Should().Be(TimeSpan.FromSeconds(20));

        using var librivoxSnapshot = factory.CreateClient(LibriVoxProvider.SnapshotHttpClientName);
        librivoxSnapshot.BaseAddress.Should().Be(new Uri(LibriVoxCatalog.BaseUrl));
        librivoxSnapshot.Timeout.Should().Be(TimeSpan.FromMinutes(2));
    }

    private static void AssertRegistered(
        IServiceCollection services,
        Type serviceType,
        ServiceLifetime lifetime)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == serviceType);
        descriptor.Should().NotBeNull($"{serviceType.Name} should be registered");
        descriptor!.Lifetime.Should().Be(lifetime, $"{serviceType.Name} should be {lifetime}");
    }

    private static void AssertRegistered(
        IServiceCollection services,
        Type serviceType,
        Type implementationType,
        ServiceLifetime lifetime)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == serviceType);
        descriptor.Should().NotBeNull($"{serviceType.Name} should be registered");
        descriptor!.ImplementationType.Should().Be(implementationType);
        descriptor.Lifetime.Should().Be(lifetime, $"{serviceType.Name} should be {lifetime}");
    }

    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public Task<string?> GetActiveModelAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("stub");

        public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(new EmbeddingBatch("stub", [], null));
    }

    private sealed class StubAssistantOwner : IAssistantOwner
    {
        public string Key => "stub-owner";
    }

    private sealed class StubMigrationPhaseAvailability : IMigrationPhaseAvailability
    {
        public bool IsAvailable(MigrationDirection direction) => true;
    }

    private sealed class StubAcquisitionWorkingRootProvider : IAcquisitionWorkingRootProvider
    {
        public string ResolveWorkingRoot(string contentRootPath, AcquisitionOptions options) =>
            Path.GetTempPath();
    }
}

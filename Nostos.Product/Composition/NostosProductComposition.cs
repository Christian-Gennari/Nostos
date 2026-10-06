using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Providers.LibriVox;
using Nostos.Backend.Providers.StandardEbooks;
using Nostos.Backend.Providers.Wikisource;
using Nostos.Backend.Serialization;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;

namespace Nostos.Product.Composition;

/// <summary>
/// Values normalized while the reusable Nostos product is registered.
/// A host can use these same instances when it wires transport/provider details.
/// </summary>
public sealed record NostosProductDescriptor(
    AssistantOptions Assistant,
    SpeechOptions Speech,
    OpdsOptions Opds);

/// <summary>
/// Optional host policy names applied to product endpoints.
///
/// The product understands only the purpose of a policy. It does not know
/// whether a host implements that policy with Clerk, ASP.NET rate limiting,
/// another provider, or no hosted policy at all.
/// </summary>
/// <param name="MapMigrationTransferEndpoints">
/// Whether <see cref="NostosProductComposition.MapNostosProductEndpoints"/>
/// creates the SelfHosted migration transfer group
/// (<c>/api/portability/migration</c>). Defaults to <c>true</c>, the SelfHosted
/// behaviour. A host that does not register the SelfHosted migration services
/// must set this to <c>false</c>: the group's handlers require them, and
/// mapping the group without them fails endpoint creation.
/// </param>
public sealed record NostosProductEndpointPolicies(
    string? ExpensiveMutationRateLimitPolicy = null,
    string? ProviderFetchRateLimitPolicy = null,
    string? LargeTransferRateLimitPolicy = null,
    string? PortableExportAuthorizationPolicy = null,
    string? OpdsAuthorizationPolicy = null,
    string? MigrationAuthorizationPolicy = null,
    bool MapMigrationTransferEndpoints = true)
{
    public static NostosProductEndpointPolicies None { get; } = new();
}

public static class NostosProductComposition
{
    /// <summary>
    /// Registers provider-neutral Nostos product behavior.
    ///
    /// Persistence, durable asset storage, LLM/STT transports, optional access
    /// and usage policies, AI-provider settings persistence and acquisition job
    /// execution are host responsibilities. Product services consume only the
    /// provider-neutral contracts supplied by their host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SelfHosted migration engine is NOT started by this registration: no
    /// job worker, transfer cleanup worker, transfer storage, file staging or
    /// phase handler is added here. The SelfHosted executable adds them
    /// separately through
    /// <see cref="Nostos.Backend.Services.Portability.Migration.MigrationEngineRegistration.AddSelfHostedMigrationEngine"/>
    /// and its own transfer/staging registrations. A host that supplies its own
    /// migration adapter omits those calls.
    /// </para>
    /// <para>
    /// Replaceable seams. The product registers a replaceable
    /// <see cref="IMigrationJobStore"/> fallback and a replaceable
    /// <see cref="IPortableImportPreparer"/>, both with <c>TryAdd</c>: a host can
    /// register its own implementations before this method and they win.
    /// <see cref="IPortableArchiveSource"/> and
    /// <see cref="IPortableImportStaging"/> are always host-supplied. The
    /// prepared-import engine is available to every host as
    /// <see cref="IPortableImportPreparer"/>, and the verifier as
    /// <see cref="IPortableLibraryVerifier"/>.
    /// </para>
    /// <para>
    /// Discovery is a replaceable seam too. The product registers the live
    /// provider fan-out (<see cref="ProviderDiscoveryService"/>) as the default
    /// <see cref="IProviderDiscovery"/> with <c>TryAdd</c>, so SelfHosted keeps
    /// the current live search with no catalog mirror or scheduled polling. A
    /// host can register a catalog-backed implementation before this method and
    /// it wins; acquisition always re-resolves a selected item against its live
    /// provider before downloading.
    /// </para>
    /// <para>
    /// When mapping endpoints, a host without the SelfHosted migration services
    /// passes <see cref="NostosProductEndpointPolicies"/> with
    /// <see cref="NostosProductEndpointPolicies.MapMigrationTransferEndpoints"/>
    /// set to <c>false</c>; otherwise the migration transfer group's handlers
    /// cannot be created.
    /// </para>
    /// </remarks>
    public static NostosProductDescriptor AddNostosProduct(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var assistant =
            configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>()
            ?? new AssistantOptions();
        services.AddSingleton(assistant);

        var speech =
            configuration.GetSection(SpeechOptions.SectionName).Get<SpeechOptions>()
            ?? new SpeechOptions();
        services.AddSingleton(speech);

        services.AddSingleton(
            configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
            ?? new EmbeddingOptions());

        var opds =
            configuration.GetSection(OpdsOptions.SectionName).Get<OpdsOptions>()
            ?? new OpdsOptions();
        opds.PageSize = OpdsOptions.NormalizePageSize(opds.PageSize);
        if (!OpdsOptions.TryNormalizePublicBaseUrl(
                opds.PublicBaseUrl,
                out var publicBaseUrl,
                out var publicBaseUrlError))
        {
            throw new InvalidOperationException(
                $"OPDS is enabled but 'Opds:PublicBaseUrl' is invalid ({publicBaseUrlError}). " +
                "Fix the URL, or unset it to derive the origin from each request.");
        }

        opds.PublicBaseUrl = publicBaseUrl;
        services.AddSingleton(opds);

        var bookText = configuration.GetSection(BookTextOptions.SectionName).Get<BookTextOptions>()
            ?? new BookTextOptions();
        services.AddSingleton(bookText);
        services.AddSingleton<IBookTextExtractor, PdfBookTextExtractor>();
        services.AddSingleton<IBookTextExtractor, EpubBookTextExtractor>();
        services.TryAddScoped<IBookTextIndex, NoOpBookTextIndex>();
        services.TryAddScoped<IBookDerivedArtifactStorage, NoOpBookTextArtifactStorage>();
        services.TryAddScoped<IBookTextIngestionScheduler, NoOpBookTextIngestionScheduler>();
        services.TryAddScoped<IBookTextLifecycle, BookTextLifecycle>();
        services.AddScoped<BookTextIngestionEngine>();
        // Embeddings are optional: with no host-supplied provider or vector
        // store these fallbacks keep the embedding pass a no-op.
        services.TryAddSingleton<IEmbeddingProvider, NoOpEmbeddingProvider>();
        services.TryAddScoped<IBookTextEmbeddingIndex, NoOpBookTextEmbeddingIndex>();
        services.AddScoped<BookTextEmbeddingEngine>();
        services.AddScoped<IBookTextSearchService, BookTextSearchService>();
        services.AddScoped<BookTextBackfillService>();

        services.AddSingleton(LibraryReceiptRetentionOptions.Normalize(
            configuration.GetSection("LibraryReceiptRetention").Get<LibraryReceiptRetentionOptions>()
            ?? new LibraryReceiptRetentionOptions()));

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
        });

        services.AddHttpClient();
        services.AddHttpClient(BookLookupService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos-Rebirth)");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        });

        services.AddScoped<BookLookupService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<LibraryReceiptRetentionService>();
        services.AddScoped<MediaMetadataService>();
        services.AddScoped<NoteProcessorService>();
        services.AddScoped<INoteService, NoteService>();
        services.AddScoped<KoreaderNoteImportService>();
        services.AddScoped<HighlightImportService>();
        services.AddScoped<IKnowledgeRetrievalService, KnowledgeRetrievalService>();

        services.AddScoped<IPortableArchiveService, PortableArchiveService>();
        services.TryAddScoped<IPortableArchiveExporter, DefaultPortableArchiveExporter>();
        services.TryAddScoped<IMigrationJobStore, EfMigrationJobStore>();
        // Migration job creation is refused until a host wires a real phase
        // handler. A host with no engine (for example one that maps product
        // endpoints with MapMigrationTransferEndpoints = false) reports no
        // availability, so the capability endpoint can never advertise a
        // feature this host cannot execute. The SelfHosted engine replaces
        // this with its real Slice 9/10 handlers.
        services.TryAddSingleton<IMigrationPhaseAvailability>(MigrationPhaseAvailabilityNone.Instance);
        services.TryAddScoped<ILibraryDestinationRevisionProvider, LibraryStateDestinationRevisionProvider>();
        services.AddScoped<PortableArchiveReader>();
        // The public prepared-import seam resolves through the reader's optional
        // dependencies so it works in a host that supplies neither logging nor a
        // time provider.
        services.TryAddScoped<IPortableImportPreparer>(sp => new PortableArchiveReader(
            sp.GetService<ILogger<PortableArchiveReader>>(),
            sp.GetService<TimeProvider>()));

        services.AddScoped<IBookRepository, BookRepository>();
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddScoped<ITopicRepository, TopicRepository>();
        services.AddScoped<IWritingRepository, WritingRepository>();

        services.AddSingleton<IThoughtProcessor, ThoughtProcessor>();
        services.AddSingleton<AssistantPlanStore>();
        services.AddSingleton<AssistantContinuationStore>();
        services.AddSingleton<AssistantTurnExecutionRegistry>();
        services.AddSingleton<IAssistantSettingsService, AssistantSettingsService>();
        services.AddScoped<AssistantOrchestrator>();

        services.AddScoped(sp => new AssistantCapabilityRegistry(AssistantCapabilities.Build(
            sp.GetRequiredService<INoteService>(),
            sp.GetRequiredService<ILibraryService>(),
            sp.GetRequiredService<ITopicRepository>(),
            sp.GetRequiredService<IKnowledgeRetrievalService>(),
            sp.GetRequiredService<IBookTextSearchService>())));

        services.Configure<AcquisitionOptions>(
            configuration.GetSection(AcquisitionOptions.SectionName));

        services.AddHttpClient(ProviderContentDownloader.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos-Rebirth)");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });

        services.Configure<ProviderDiscoveryOptions>(
            configuration.GetSection(ProviderDiscoveryOptions.SectionName));

        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<ProviderDiscoveryService>();
        // Replaceable discovery seam: SelfHosted keeps the live provider
        // fan-out as its default, while a host can register its own
        // IProviderDiscovery (for example a catalog-backed implementation)
        // before this method and it wins.
        services.TryAddSingleton<IProviderDiscovery>(sp =>
            sp.GetRequiredService<ProviderDiscoveryService>());
        services.TryAddSingleton<
            IAcquisitionWorkingRootProvider,
            DefaultAcquisitionWorkingRootProvider>();
        services.AddSingleton<ITranscodeLimiter, TranscodeLimiter>();
        services.AddSingleton<IProviderContentDownloader, ProviderContentDownloader>();
        services.AddScoped<IAcquisitionService, AcquisitionService>();

        services.AddHttpClient(GutenbergProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(GutenbergCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos-Rebirth)");
        });
        services.AddSingleton<IContentProvider, GutenbergProvider>();

        // Standard Ebooks approved Nostos for OPDS access by whitelisting this
        // project User-Agent. There is deliberately no shared feed credential:
        // SelfHosted and Cloud use the same provider identity, and no secret is
        // embedded in the client or public repository.
        services.AddHttpClient(StandardEbooksProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(StandardEbooksCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                StandardEbooksProvider.ApprovedUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd(
                StandardEbooksProvider.OpdsAccept);
        });
        services.AddSingleton<IContentProvider, StandardEbooksProvider>();

        services.AddHttpClient(WikisourceProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(WikisourceCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos-Rebirth)");
        });
        services.AddSingleton<IContentProvider, WikisourceProvider>();

        services.Configure<MediaToolOptions>(
            configuration.GetSection(MediaToolOptions.SectionName));
        services.AddSingleton<IMediaProcessRunner, MediaProcessRunner>();
        services.AddSingleton<LibriVoxM4bAssembler>();

        services.AddHttpClient(LibriVoxProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(LibriVoxCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos-Rebirth)");
        });
        services.AddSingleton<IContentProvider, LibriVoxProvider>();

        return new NostosProductDescriptor(assistant, speech, opds);
    }

    /// <summary>
    /// Maps the shared Nostos product API. Host-specific routes and access
    /// policies are composed by the executable around this product surface.
    /// </summary>
    /// <remarks>
    /// The SelfHosted migration transfer group is mapped by default. A host that
    /// does not register the SelfHosted migration services sets
    /// <see cref="NostosProductEndpointPolicies.MapMigrationTransferEndpoints"/>
    /// to <c>false</c> in <paramref name="policies"/> to omit it.
    /// </remarks>
    public static IEndpointRouteBuilder MapNostosProductEndpoints(
        this IEndpointRouteBuilder routes,
        OpdsOptions opds,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;

        routes.MapBooksEndpoints(policies);
        routes.MapProviderEndpoints(policies);
        routes.MapImportEndpoints();
        routes.MapNotesEndpoints();
        routes.MapNoteProcessingEndpoints();
        routes.MapCollectionsEndpoints();
        routes.MapTopicsEndpoints();
        routes.MapWritingsEndpoints();
        routes.MapTranscriptionEndpoints();
        routes.MapAssistantEndpoints();
        routes.MapAiProviderSettingsEndpoints();
        routes.MapAssistantSettingsEndpoints();
        routes.MapDeploymentCapabilitiesEndpoints();
        routes.MapPortabilityEndpoints(policies);
        routes.MapMigrationEndpoints(policies);
        routes.MapOpdsEndpoints(
            opds,
            policies.OpdsAuthorizationPolicy,
            policies.LargeTransferRateLimitPolicy);

        return routes;
    }
}

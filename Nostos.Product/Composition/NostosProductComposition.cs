using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Discovery;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Product.Storage;

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
/// <param name="McpRateLimitPolicy">
/// Optional host rate-limit policy for the MCP endpoint (<c>/mcp</c>). The
/// endpoint itself is mapped only when the host called
/// <see cref="Nostos.Backend.Integrations.Mcp.NostosMcp.AddNostosMcp"/> and registers
/// <see cref="Nostos.Backend.Integrations.Mcp.NostosMcp.AuthorizationPolicy"/>.
/// </param>
/// <param name="ManagedBackupsAuthorizationPolicy">
/// Optional host authorization policy for managed-backup history. Hosts with
/// a default policy weaker than their product-access fallback should provide
/// their customer product-access policy here.
/// </param>
public sealed record NostosProductEndpointPolicies(
    string? ExpensiveMutationRateLimitPolicy = null,
    string? ProviderFetchRateLimitPolicy = null,
    string? LargeTransferRateLimitPolicy = null,
    string? PortableExportAuthorizationPolicy = null,
    string? OpdsAuthorizationPolicy = null,
    string? MigrationAuthorizationPolicy = null,
    bool MapMigrationTransferEndpoints = true,
    string? ManagedBackupsAuthorizationPolicy = null,
    bool MapResumableBookUploadEndpoints = false,
    string? McpRateLimitPolicy = null,
    /// <summary>
    /// Whether the Calibre import routes are mapped. A host that registers an
    /// <c>ICalibreLibrarySource</c> (a folder or an uploaded-library source)
    /// sets this; a host with no Calibre source leaves it off.
    /// </summary>
    bool MapCalibreImportEndpoints = false,
    /// <summary>
    /// Whether the resumable Calibre zip upload routes are mapped. Requires
    /// the host to register <c>CalibreUploadService</c> and its store.
    /// </summary>
    bool MapCalibreUploadEndpoints = false,
    /// <summary>
    /// Authorization policy for the signed-in Carrel pairing and devices API
    /// (<c>/api/carrel</c>). A host with accounts passes its customer policy;
    /// SelfHosted has one local owner and passes null.
    /// </summary>
    string? CarrelManagementAuthorizationPolicy = null,
    /// <summary>
    /// Rate-limit policy for the device routes (<c>/carrel/v1</c>), which are
    /// reachable without a signed-in person. A host with accounts passes a
    /// policy that partitions by client address; SelfHosted passes null.
    /// </summary>
    string? CarrelProtocolRateLimitPolicy = null,
    /// <summary>
    /// Rate-limit policy for the signed-in pairing and devices routes
    /// (<c>/api/carrel</c>). Approving a code is guessable by anyone with an
    /// account, so a host with accounts passes a policy that partitions by
    /// account; SelfHosted passes null.
    /// </summary>
    string? CarrelManagementRateLimitPolicy = null)
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
    /// Product scratch placement is a replaceable seam too: the product
    /// registers <see cref="IProductScratchStorage"/> at the process temp path
    /// with <c>TryAdd</c>, and a host that knows where its durable volume is
    /// registers its own before this method and it wins.
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
        var descriptor = services.AddNostosProductOptions(configuration);

        // Where the product stages whole publications, archives and provider
        // snapshots (issue #1596). The process temp path is the fallback for a
        // host that has not said where its durable volume is; a host that has
        // (SelfHosted, from its storage paths) registers its own before this
        // method, or replaces this registration after it, and wins.
        services.TryAddSingleton<IProductScratchStorage>(ProductScratchStorage.TempPath);

        services.AddNostosProductHttpClients();
        services.AddNostosBookTextServices();
        services.AddNostosAlignmentServices();
        services.AddNostosLibraryServices();
        services.AddNostosPortabilityServices();
        services.AddNostosAssistantServices();
        services.AddNostosProviderServices();

        return descriptor;
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

        // Every write names the library revisions it committed in a response
        // header, so the tab that made it can recognise its own announcements.
        var api = routes.MapGroup(string.Empty);
        api.AddEndpointFilter(NameCommittedRevisionsAsync);

        api.MapBooksEndpoints(policies);
        if (policies.MapResumableBookUploadEndpoints)
            api.MapBookUploadEndpoints(policies);
        api.MapAlignmentEndpoints(policies);
        api.MapProviderEndpoints(policies);
        api.MapProviderSettingsEndpoints();
        api.MapImportEndpoints();
        if (policies.MapCalibreImportEndpoints)
            api.MapCalibreEndpoints(
                policies.MapCalibreUploadEndpoints,
                policies.LargeTransferRateLimitPolicy);
        api.MapLibraryChangesEndpoints();
        api.MapNotesEndpoints();
        api.MapNoteProcessingEndpoints();
        api.MapCollectionsEndpoints();
        api.MapReadingListEndpoints();
        api.MapTopicsEndpoints();
        api.MapWritingsEndpoints();
        api.MapTranscriptionEndpoints();
        api.MapReadAloudEndpoints();
        api.MapAssistantEndpoints();
        api.MapAiProviderSettingsEndpoints();
        api.MapDeploymentCapabilitiesEndpoints();
        // Settings → Connected apps: only where the host keeps a record of them.
        api.MapMcpConnectedAppsEndpoints();
        // Carrel Sync Protocol v1 (WP8): /carrel/v1 for paired devices, and the
        // signed-in pairing/device routes. Both map only on a host that
        // registered the feature.
        routes.MapCarrelProtocolEndpoints(policies.CarrelProtocolRateLimitPolicy);
        api.MapCarrelManagementEndpoints(
            policies.CarrelManagementAuthorizationPolicy,
            policies.CarrelManagementRateLimitPolicy);
        api.MapManagedBackupEndpoints(
            policies.ManagedBackupsAuthorizationPolicy,
            policies.LargeTransferRateLimitPolicy);
        api.MapPortabilityEndpoints(policies);
        api.MapMigrationEndpoints(policies);
        api.MapOpdsEndpoints(
            opds,
            policies.OpdsAuthorizationPolicy,
            policies.LargeTransferRateLimitPolicy);

        // Outside the revision filter: MCP is JSON-RPC, and its writes reach the
        // open app through the library change feed, not a response header.
        routes.MapNostosMcp(policies.McpRateLimitPolicy);

        return routes;
    }

    private static async ValueTask<object?> NameCommittedRevisionsAsync(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method))
        {
            return await next(invocation);
        }

        var receipt = LibraryWriteReceipt.Begin();
        http.Response.OnStarting(() =>
        {
            if (receipt.HeaderValue is { } revisions)
                http.Response.Headers[LibraryWriteReceipt.HeaderName] = revisions;
            return Task.CompletedTask;
        });

        return await next(invocation);
    }
}

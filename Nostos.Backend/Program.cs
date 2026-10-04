using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Health;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Providers.LibriVox;
using Nostos.Backend.Serialization;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Backend.Services.BookText;
using Nostos.Product.BookText;
using Nostos.Backend.Workers;
using Nostos.Backend.Middleware;
using Nostos.Product.Composition;
using Nostos.Product.Services.Ai;

var builder = WebApplication.CreateBuilder(args);

var deployment = DeploymentDescriptor.For(DeploymentMode.SelfHosted);
builder.Services.AddSingleton(deployment);

// Preserve SelfHosted's same-volume staging behavior without exposing the
// local filesystem contract to Nostos.Product.
builder.Services.AddSingleton<
    IAcquisitionWorkingRootProvider,
    SelfHostedAcquisitionWorkingRootProvider>();

// SelfHosted owns the concrete local book-text persistence boundary. Register
// these before AddNostosProduct so its TryAdd no-op fallbacks are never selected.
builder.Services.AddScoped<SqliteBookTextIndex>();
builder.Services.AddScoped<IBookTextIndex>(sp => sp.GetRequiredService<SqliteBookTextIndex>());
builder.Services.AddScoped<IBookTextEmbeddingIndex>(sp => sp.GetRequiredService<SqliteBookTextIndex>());
builder.Services.AddScoped<IBookDerivedArtifactStorage, FileBookTextArtifactStorage>();
builder.Services.AddScoped<IBookTextIngestionScheduler, BookTextIngestionScheduler>();

// Passage embeddings (issue #683): BYOK and optional. Registered here for the
// same reason — it must win over the product's no-op provider. While the
// surface is unconfigured the provider reports no active model, so the
// book-text embedding pass does nothing and retrieval stays lexical.
var embeddingOptions =
    builder.Configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
    ?? new EmbeddingOptions();
builder.Services.AddHttpClient(OpenAiCompatibleEmbeddingProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, embeddingOptions.RequestTimeoutSeconds));
});
builder.Services.AddSingleton<IEmbeddingProvider, OpenAiCompatibleEmbeddingProvider>();

var product = builder.Services.AddNostosProduct(builder.Configuration);
var assistantOptions = product.Assistant;
var speechOptions = product.Speech;
var opdsOptions = product.Opds;

builder.Services.Configure<BackupSettings>(builder.Configuration.GetSection("BackupSettings"));

// Where locally stored book files live. Configurable so a test host (or a
// deployment with a dedicated media volume) can point it somewhere else —
// `Storage/books` under the content root stays the default.
builder.Services.Configure<FileStorageOptions>(
    builder.Configuration.GetSection(FileStorageOptions.SectionName));

// Transfer storage for library migration jobs (issue #679, Slice 3). The root
// is validated before startup and created when missing; every transfer service
// resolves paths through this one resolver, and admission accounting measures
// the volume that actually holds it.
builder.Services.Configure<TransferStorageOptions>(
    builder.Configuration.GetSection(TransferStorageOptions.SectionName));
var fileStorageOptions =
    builder.Configuration.GetSection(FileStorageOptions.SectionName).Get<FileStorageOptions>()
    ?? new FileStorageOptions();
var transferStorageOptions =
    builder.Configuration.GetSection(TransferStorageOptions.SectionName).Get<TransferStorageOptions>()
    ?? new TransferStorageOptions();
TransferStorageOptions.Validate(transferStorageOptions);
var transferRootPath = TransferStorageOptions.ResolveRoot(
    builder.Environment.ContentRootPath,
    FileStorageOptions.ResolveBooksRoot(builder.Environment.ContentRootPath, fileStorageOptions),
    transferStorageOptions);
TransferPathResolver.EnsureRootDirectory(transferRootPath);
builder.Services.AddSingleton(new TransferPathResolver(transferRootPath));
builder.Services.AddSingleton<ITransferVolume>(new DriveInfoTransferVolume(transferRootPath));
builder.Services.AddScoped<ITransferStorageCapacity, TransferStorageCapacity>();
Nostos.Backend.Services.Portability.Migration.MigrationEngineRegistration.AddSelfHostedMigrationEngine(builder.Services);

// --- MCP (Model Context Protocol) Streamable HTTP foundation (Task 9A) ---
// Opt-in and disabled by default. When enabled, the bearer token is resolved
// ONLY from the configured environment variable at startup; a missing token
// fails startup closed so the server never runs unauthenticated. The
// validated options instance is registered as a singleton so the maintenance
// guard, the authentication middleware, and the endpoint mapping always
// agree on the normalized route. The token itself is captured in the local
// `mcpApiKey` variable, never stored in configuration, logging, or responses.
var mcpOptions = builder.Configuration.GetSection("Mcp").Get<McpOptions>() ?? new McpOptions();
string mcpApiKey = string.Empty;
if (mcpOptions.Enabled)
{
    if (!McpOptions.TryNormalizePath(mcpOptions.Path, out var normalizedPath, out var pathError))
    {
        throw new InvalidOperationException(
            $"MCP is enabled but 'Mcp:Path' is invalid ({pathError}). Disable MCP (Mcp:Enabled=false) or fix the path.");
    }

    mcpOptions.Path = normalizedPath;

    if (string.IsNullOrWhiteSpace(mcpOptions.ApiKeyEnvironmentVariable))
    {
        throw new InvalidOperationException(
            "MCP is enabled but 'Mcp:ApiKeyEnvironmentVariable' is empty. Configure an environment variable name.");
    }

    var resolvedToken = Environment.GetEnvironmentVariable(mcpOptions.ApiKeyEnvironmentVariable);
    if (!string.IsNullOrWhiteSpace(resolvedToken))
    {
        mcpApiKey = resolvedToken.Trim();
    }
    else
    {
        throw new InvalidOperationException(
            $"MCP is enabled but environment variable '{mcpOptions.ApiKeyEnvironmentVariable}' is not set or empty. " +
            "Set the token before starting Nostos, or disable MCP (Mcp:Enabled=false).");
    }

    builder.Services
        .AddMcpServer(server => server.ServerInfo = new Implementation
        {
            Name = "nostos",
            Version = "1.0.0",
        })
        .WithHttpTransport()
        .WithToolsFromAssembly();
}

builder.Services.AddSingleton(mcpOptions);

// --- SPEECH-TO-TEXT (issue #262 §2/§3) ---
builder.Services.AddHttpClient(NineRouterSttProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddSingleton<ISTtProvider, NineRouterSttProvider>();

// --- ASSISTANT LLM BRIDGE (issue #261 §3, §7) ---
builder.Services.AddHttpClient(NineRouterLlmProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, assistantOptions.RequestTimeoutSeconds));
});
builder.Services.AddSingleton<ILlmProvider, NineRouterLlmProvider>();

builder.Services.AddSingleton<IAiAccessPolicy, AllowAllAiAccessPolicy>();
builder.Services.AddSingleton<IAiUsageAccountingService>(NoOpAiUsageAccountingService.Instance);

// --- AI PROVIDER SETTINGS ---
builder.Services.AddNostosSelfHostedDataProtection();

builder.Services.AddHttpClient(AiProviderSettingsService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<AiProviderSettingsService>();
builder.Services.AddSingleton<IAiProviderConfigResolver>(
    sp => sp.GetRequiredService<AiProviderSettingsService>());
builder.Services.AddSingleton<IAiProviderSettingsService>(
    sp => sp.GetRequiredService<AiProviderSettingsService>());


// Single upload cap for Kestrel + multipart forms (audiobooks can be GB-sized).
// One declaration only: a second ConfigureKestrel/Configure<FormOptions> call
// would silently overwrite the first, leaving dead config behind.
const long maxUploadSize = 4L * 1024 * 1024 * 1024; // 4GB in bytes
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadSize;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadSize;
});

// --- REVERSE PROXY / FORWARDED HEADERS ---
// Nostos is normally reached through a reverse proxy that terminates TLS
// (Tailscale `serve`, nginx, ...), so the request Kestrel actually receives is
// plain HTTP addressed to an internal name. Without honouring these headers
// every absolute URL the app generates is wrong: the OPDS feed advertised
// `http://` acquisition links on an `https://` catalogue, and an e-reader that
// refuses or cannot follow the downgraded link simply never gets the book.
//
// The trust list stays at its default (loopback only), which is where a local
// proxy connects from. Widening it — or clearing both lists, which means
// "trust every caller" — would let any client forge the host used in generated
// URLs, so the default is the safe one and the port of the proxy must stay on
// the loopback interface.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
});

builder.Services.AddNostosPersistence(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddScoped<IDatabaseBootstrapService, DatabaseBootstrapService>();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Services Dependency Injection
builder.Services.AddSingleton<FileStorageService>();
builder.Services.AddSingleton<IFileStorageService>(
    sp => sp.GetRequiredService<FileStorageService>());
builder.Services.AddSingleton<IBookAssetStorage>(
    sp => sp.GetRequiredService<FileStorageService>());
builder.Services.AddNostosSelfHostedHealthChecks();

builder.Services.AddSingleton<BackupSettingsProvider>();
builder.Services.AddSingleton(sp => new LibraryMaintenanceMarker(
    PersistenceRegistration.ResolveDatabasePath(
        builder.Configuration[PersistenceRegistration.DatabasePathConfigurationKey],
        builder.Environment.ContentRootPath)));
builder.Services.AddSingleton(sp => new LibraryMaintenanceCoordinator(
    builder.Configuration.GetSection(LibraryMaintenanceOptions.SectionName).Get<LibraryMaintenanceOptions>(),
    marker: sp.GetRequiredService<LibraryMaintenanceMarker>()));
builder.Services.AddSingleton<ILibraryMaintenanceCoordinator>(sp => sp.GetRequiredService<LibraryMaintenanceCoordinator>());
builder.Services.AddScoped<IBackupService, BackupService>();

// One instance serves as the job store, the hosted worker that drains it, and
// the IAcquisitionJobManager the endpoints talk to.
builder.Services.AddSingleton<AcquisitionJobManager>();
builder.Services.AddSingleton<IAcquisitionJobManager>(sp => sp.GetRequiredService<AcquisitionJobManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AcquisitionJobManager>());
builder.Services.AddHostedService<AcquisitionReconciliationWorker>();
builder.Services.AddHostedService<TopicCleanupWorker>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddHostedService<LibraryReceiptRetentionWorker>();
builder.Services.AddHostedService<BookTextIngestionWorker>();

var app = builder.Build();

// A stale maintenance marker is not an orphaned process lease. Cutover journals
// must be reconciled before this point by Slice 3; unresolved journals fail closed.
app.Services.GetRequiredService<LibraryMaintenanceCoordinator>().InitializeAfterRecovery();

// --- DATABASE BOOTSTRAP / MIGRATION ---
// A truly empty SQLite database (brand-new or zero tables) is bootstrapped
// to the complete current schema with an accurate EF migration-history
// baseline, in one transaction (see DatabaseBootstrapService). Any existing
// database goes through the ordinary EF migration path and is never
// rebaselined; a partial or unknown schema fails closed here.
using (var scope = app.Services.CreateScope())
{
    var bootstrap = scope.ServiceProvider.GetRequiredService<IDatabaseBootstrapService>();
    await bootstrap.EnsureReadyAsync();

    // Book-text search state is intentionally derived/disposable and therefore
    // not part of the portable relational EF model. Ensure its local FTS schema
    // explicitly before the background backfill/ingestion worker starts.
    await scope.ServiceProvider
        .GetRequiredService<IBookTextIndex>()
        .EnsureSchemaAsync();
}

// ------------------------------------

// --- FORWARDED HEADERS (reverse proxy) ---
// First in the pipeline: everything downstream — static files, the SPA
// fallback, and above all the absolute URLs the OPDS feed builds — needs to see
// the scheme and host the client actually used, not the internal ones the proxy
// forwarded to. The trust list is left at its default (loopback only); see the
// registration above for why.
app.UseForwardedHeaders();

// --- OPDS EXPORT (access model) ---
// Stated in the operator's own logs, once, so that exposing the catalogue is a
// decision on the record rather than a silent consequence of mapping a route.
app.Logger.LogInformation(
    opdsOptions.Enabled
        ? "OPDS export enabled at /opds/ — page size {PageSize}, external base URL {PublicBaseUrl}. "
            + "It is UNAUTHENTICATED: keep Nostos on a private network (LAN/Tailscale) or set Opds:Enabled=false."
        : "OPDS export disabled (Opds:Enabled=false): /opds/ is not mapped.",
    opdsOptions.PageSize,
    opdsOptions.PublicBaseUrl ?? "derived from each request"
);

// --- GLOBAL ERROR HANDLING ---
app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        context.Response.ContentType = "application/problem+json";
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var logger = context
            .RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalExceptionHandler");

        var error = exceptionFeature?.Error;
        var statusCode = error is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;

        if (error is not null && statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                "Unhandled {ExceptionType} on {Method} {Path}. Exception messages and payloads are suppressed by default.",
                error.GetType().Name,
                context.Request.Method,
                context.Request.Path
            );
        }

        context.Response.StatusCode = statusCode;
        await Results.Problem(
            statusCode: statusCode,
            type: statusCode == StatusCodes.Status400BadRequest
                ? "https://tools.ietf.org/html/rfc9110#section-15.5.1"
                : "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            title: statusCode == StatusCodes.Status400BadRequest
                ? "The request could not be processed."
                : "An unexpected error occurred."
        ).ExecuteAsync(context);
    });
});

app.UseStatusCodePages();

// Routing exposes the restore control metadata before admission. Library request
// scopes and complete streaming responses drain through the same gate as workers.
app.UseRouting();
app.UseMiddleware<LibraryMaintenanceMiddleware>();

// --- MCP AUTHENTICATION GATE ---
// Registered after the maintenance guard (so maintenance cannot be bypassed
// by authenticating) and before static files and endpoint routing (so the
// configured MCP route cannot be reached without the exact bearer token).
// When MCP is disabled nothing is registered and the route stays unmapped.
if (mcpOptions.Enabled)
{
    app.UseMiddleware<McpAuthenticationMiddleware>(mcpApiKey);
}

// -----------------------------------

app.MapOpenApi();

// Cheap process liveness deliberately has no dependency checks. Readiness is
// separate and probes only the shared infrastructure required to serve real
// traffic; its response never includes connection strings, provider errors or
// customer identifiers.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = NostosHealthResponseWriter.WriteAsync,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains(NostosHealthCheckTags.Readiness),
    ResponseWriter = NostosHealthResponseWriter.WriteAsync,
}).AllowAnonymous();

// --- SERVE ANGULAR FRONTEND ---
app.UseDefaultFiles();
app.UseStaticFiles();

// ------------------------------

// Map the public product API with the SelfHosted host's provider-neutral policy.
app.MapNostosProductEndpoints(opdsOptions, NostosProductEndpointPolicies.None);
app.MapBackupEndpoints();

// --- MCP STREAMABLE HTTP ENDPOINT ---
if (mcpOptions.Enabled)
{
    app.MapMcp(mcpOptions.Path);
}

// --- HANDLE ANGULAR ROUTING ---
// The SPA shell is served ONLY for client-side routes. The API, OPDS and MCP
// namespaces are never answered by index.html: unknown /api paths, wrong
// methods on API routes, and the MCP route family (the configured path
// when enabled, plus the conventional /mcp namespace in every
// configuration) resolve as ordinary 404/405 responses, so probes and
// misdirected clients never receive the Angular application shell. The
// fallback is deliberately method-constrained (GET/HEAD only), mirroring
// the static-file layer: every other verb on an unmapped path stays a 405
// instead of being answered with the shell.
//
// `/opds` is in that list for the same reason: an OPDS client pointed at a
// catalogue that is switched off (Opds:Enabled=false) must get a 404 it can
// report, not a page of HTML it cannot parse. This is what the frontend's
// ngsw `navigationUrls` already assumed about the backend.
RequestDelegate serveClientRoute = async context =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return;
    }

    var path = context.Request.Path;
    var isMcpPath = mcpOptions.Enabled && mcpOptions.Path.Length > 0 &&
                    path.StartsWithSegments(mcpOptions.Path);
    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/opds") ||
        path.StartsWithSegments("/mcp") || isMcpPath)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var indexHtml = Path.Combine(app.Environment.WebRootPath, "index.html");
    if (!File.Exists(indexHtml))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(indexHtml);
};

// The fallback endpoint carries the same GET/HEAD method constraint as the
// static-file layer: the routing method matcher keeps answering every other
// verb on unmapped paths with 405 before this delegate ever runs, so mapped
// POST routes are never shadowed.
app.MapFallback(serveClientRoute)
    .WithMetadata(new HttpMethodMetadata(new[] { "GET", "HEAD" }))
    .AllowAnonymous();

app.Run();
return 0;

public partial class Program;

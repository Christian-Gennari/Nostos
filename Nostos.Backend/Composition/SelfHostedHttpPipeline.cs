using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Health;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Middleware;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Calibre;
using Nostos.Backend.Services.Carrel;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Services.Mcp;
using Nostos.Backend.Services.Mcp.OAuth;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Product.BookText;
using Nostos.Product.Composition;

namespace Nostos.Backend.Composition;

/// <summary>
/// Configures the SelfHosted Kestrel/HTTP infrastructure, startup
/// reconciliation &amp; database bootstrap, middleware ordering, endpoints,
/// global ProblemDetails handler, and Angular SPA fallback routing.
/// </summary>
public static class SelfHostedHttpPipeline
{
    public static WebApplicationBuilder ConfigureSelfHostedHttpHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

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

        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddApiResponseCompression();

        return builder;
    }

    /// <summary>
    /// Registers SelfHosted MCP (Bearer tokens + OAuth) and Carrel Sync integrations.
    /// </summary>
    public static IServiceCollection AddSelfHostedIntegrations(this IServiceCollection services)
    {
        // MCP (issue #1467). /mcp is mapped because the access tokens below register
        // the policy that decides who may call it: a Bearer token made in Settings →
        // Connected apps (issue #1469). The token scheme is not the default, so the
        // rest of the API keeps its access model: SelfHosted has no login. With
        // Mcp:PublicBaseUrl set, the same scheme also takes OAuth access tokens from
        // claude.ai and ChatGPT, approved by the owner in Settings (issue #1470).
        services.AddNostosMcp();
        services.AddSelfHostedMcpAccessTokens();

        // Carrel Sync Protocol v1 (WP8): a Carrel writing device pairs with this
        // server, syncs its Markdown as Writing Studio drafts and downloads the
        // library for offline quoting. SelfHosted has one local owner; a host with
        // accounts registers its own owner provider.
        services.AddSingleton<ICarrelOwnerProvider, SelfHostedCarrelOwnerProvider>();
        services.AddNostosCarrelSync();

        return services;
    }

    /// <summary>
    /// Validates mounted external-media/Calibre roots, reconciles any incomplete
    /// activation journal before database access, bootstraps/migrates SQLite, and
    /// ensures the derived FTS book-text schema exists before workers or traffic.
    /// </summary>
    public static async Task InitializeSelfHostedHostAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A mistyped ExternalMedia:Root must stop the server with its own message,
        // not surface later as a failed capability request.
        app.Services.GetRequiredService<ExternalMediaRoot>();
        // The same for Calibre:LibraryRoot: a relative path is a configuration error,
        // not something to discover when the first import fails.
        app.Services.GetRequiredService<CalibreFolderSource>();

        // Repair the matched DB/media generation before bootstrap, workers, or traffic.
        // Corrupt/unsupported/inconsistent journals refuse host startup with a safe error.
        await app.Services.GetRequiredService<SelfHostedActivationRecoveryStartupService>()
            .ReconcileIncompleteAsync();

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
    }

    public static WebApplication UseSelfHostedHttpPipeline(
        this WebApplication app,
        OpdsOptions opdsOptions)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(opdsOptions);

        // --- FORWARDED HEADERS (reverse proxy) ---
        // First in the pipeline: everything downstream — static files, the SPA
        // fallback, and above all the absolute URLs the OPDS feed builds — needs to see
        // the scheme and host the client actually used, not the internal ones the proxy
        // forwarded to. The trust list is left at its default (loopback only); see the
        // registration above for why.
        app.UseForwardedHeaders();

        // --- PUBLIC HOST ALLOWLIST (issue #1470) ---
        // Right after the forwarded headers, before anything can answer: a request
        // that came through the public address (Mcp:PublicBaseUrl, or Tailscale
        // Funnel) reaches only /mcp, the OAuth endpoints and their metadata. The
        // tunnel should restrict paths too; this is the backstop if it does not.
        app.UseMcpPublicHostAllowlist();
        var mcpOAuth = app.Services.GetRequiredService<McpOAuthSettings>();
        if (mcpOAuth.Problem is not null)
            app.Logger.LogWarning("{Problem}", mcpOAuth.Problem);
        else if (mcpOAuth.Enabled)
            app.Logger.LogInformation(
                "MCP OAuth enabled for hosted AI apps at {Resource}. Requests on {Host} reach only /mcp, /oauth/* and /.well-known/oauth-*.",
                mcpOAuth.Resource, mcpOAuth.Host);

        // Forwarded headers first: behind a TLS proxy it needs the real scheme. Ahead of
        // the exception handler so an error's problem+json is compressed like the rest.
        app.UseResponseCompression();

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
        app.UseExceptionHandler(ConfigureGlobalExceptionHandler);
        app.UseStatusCodePages();

        // Routing exposes the restore control metadata before admission. Library request
        // scopes and complete streaming responses drain through the same gate as workers.
        app.UseRouting();
        app.UseMiddleware<LibraryMaintenanceMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

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
        app.UseMiddleware<PrecompressedFrontendFilesMiddleware>();
        app.UseStaticFiles(Nostos.Product.Http.NostosFrontendStaticFiles.CreateOptions());

        // Map the public product API with the SelfHosted host's provider-neutral policy.
        app.MapNostosProductEndpoints(
            opdsOptions,
            NostosProductEndpointPolicies.None with
            {
                MapResumableBookUploadEndpoints = true,
                MapCalibreImportEndpoints = true,
                MapCalibreUploadEndpoints = true,
            });
        app.MapBackupEndpoints();
        app.MapExternalMediaEndpoints();
        app.MapMcpAccessTokenEndpoints();
        app.MapMcpOAuthEndpoints();

        // The fallback endpoint carries the same GET/HEAD method constraint as the
        // static-file layer: the routing method matcher keeps answering every other
        // verb on unmapped paths with 405 before this delegate ever runs, so mapped
        // POST routes are never shadowed.
        app.MapFallback(ServeClientRoute)
            .WithMetadata(new HttpMethodMetadata(["GET", "HEAD"]))
            .AllowAnonymous();

        return app;
    }

    private static void ConfigureGlobalExceptionHandler(IApplicationBuilder exceptionApp)
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
    }

    // --- HANDLE ANGULAR ROUTING ---
    // The SPA shell is served ONLY for client-side routes. The API, OPDS and MCP
    // namespaces are never answered by index.html: unknown /api paths and wrong
    // methods on API routes resolve as ordinary 404/405 responses, so probes and
    // misdirected clients never receive the Angular application shell. The
    // fallback is deliberately method-constrained (GET/HEAD only), mirroring
    // the static-file layer: every other verb on an unmapped path stays a 405
    // instead of being answered with the shell.
    //
    // `/opds` is in that list for the same reason: an OPDS client pointed at a
    // catalogue that is switched off (Opds:Enabled=false) must get a 404 it can
    // report, not a page of HTML it cannot parse. This is what the frontend's
    // ngsw `navigationUrls` already assumed about the backend.
    private static async Task ServeClientRoute(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/opds") ||
            path.StartsWithSegments(NostosMcp.Path) || path.StartsWithSegments("/oauth") ||
            path.StartsWithSegments("/.well-known"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var indexHtml = Path.Combine(env.WebRootPath, "index.html");
        if (!File.Exists(indexHtml))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (PrecompressedFrontendFiles.TrySelect(
                context, env.WebRootFileProvider, "/index.html", out var sibling, out var encoding))
        {
            indexHtml = sibling.PhysicalPath!;
            context.Response.Headers.ContentEncoding = encoding;
        }

        context.Response.ContentType = "text/html";
        // The entry point names the hashed bundles: always revalidate it.
        context.Response.Headers.CacheControl = Nostos.Product.Http.NostosFrontendStaticFiles.EntryFileCacheControl;
        await context.Response.SendFileAsync(indexHtml);
    }
}

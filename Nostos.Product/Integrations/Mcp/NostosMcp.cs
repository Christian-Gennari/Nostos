using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The Nostos MCP server (issue #1467, epic #1465): one Streamable HTTP
/// endpoint that lets the user's own AI app (Claude, ChatGPT, Gemini, a local
/// agent) search, quote and cite their library and save to it.
///
/// A host opts in twice: it registers the server with
/// <see cref="AddNostosMcp"/>, and it registers the
/// <see cref="AuthorizationPolicy"/> policy that decides who may call it.
/// <c>MapNostosProductEndpoints</c> maps <see cref="Path"/> only when both are
/// there, so a host that has not chosen an authentication never answers on
/// <c>/mcp</c>. SelfHosted uses access tokens made in Settings (issue #1469).
///
/// What a caller may do comes from <see cref="McpScopes"/>: the host puts
/// <c>nostos:read</c> / <c>nostos:write</c> in a <c>scope</c> claim, and a
/// write tool called without <c>nostos:write</c> answers with a tool error.
/// </summary>
public static class NostosMcp
{
    /// <summary>The route the MCP endpoint is mapped on.</summary>
    public const string Path = "/mcp";

    /// <summary>
    /// The authorization policy a host registers to switch MCP on. It decides
    /// who may call <c>/mcp</c>; the product never authenticates by itself.
    /// </summary>
    public const string AuthorizationPolicy = "NostosMcp";

    /// <summary>
    /// The claim a host puts the caller's OAuth scopes in: one or more
    /// space-separated strings, as in an OAuth access token. See <see cref="McpScopes"/>.
    /// </summary>
    public const string ScopeClaimType = "scope";

    /// <summary>Needed for every tool.</summary>
    public const string ReadScope = "nostos:read";

    /// <summary>Also needed for the tools that change something: save, create, add or edit.</summary>
    public const string WriteScope = "nostos:write";

    /// <summary>The client id MCP captures carry, so their receipts are their own.</summary>
    public const string ClientId = "nostos-mcp";

    /// <summary>
    /// <c>CaptureSource</c> of a note saved over MCP: where it came from, shown
    /// nowhere yet but kept with the note and in every archive.
    /// </summary>
    public const string CaptureSource = "mcp";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>What every connected app is told before it calls a tool.</summary>
    public const string Instructions =
        """
        Nostos is the user's own library: the books they read (EPUB, PDF, audiobooks and paper books), the highlights, notes and passages they kept from them, the topics that connect those notes, their To read list, and their drafts in the Writing Studio.

        Rules:
        - Quote only words a Nostos tool returned. Never quote a book from memory: use `search` or `find_quote` first, and say so when Nostos has no evidence.
        - Cite every quotation and every claim about what a book or note says. Each result carries a `citation` with the book, author, year, a location when Nostos knows it, and a `url` that opens it in Nostos.
        - When you write a draft for the Writing Studio, put quotations in as the `citation.markdown` link (a `nostos:cite/` link). The Studio shows it as a live citation and exports it as a footnote. Replace the link text only with the exact words you quote.
        - `search` looks across notes, highlights, passages, topics and the text of the user's books. `fetch` reads one result in full.
        - Save a note only when the user asks you to keep something. Keep their own words; do not rewrite them unless asked.
        - Add a book or change a book's details only when the user asks. `update_book` keeps a detail the book already has unless the user asked to change it (`replace: true`).
        - Books on paper have notes but no searchable text. A book that is still being indexed may return no passages yet.
        """;

    /// <summary>Registers the MCP server and its tools. A host calls it to offer MCP.</summary>
    public static IServiceCollection AddNostosMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<McpCitations>();
        services.AddSingleton<McpRegistered>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "nostos",
                    Title = "Nostos",
                    Version = typeof(NostosMcp).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?.Split('+')[0] ?? "1.0.0",
                };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport()
            .WithTools<NostosMcpTools>(Json)
            // Writes need the nostos:write scope; see McpScopes.
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpScopes.Enforce));
        return services;
    }

    /// <summary>
    /// Maps <see cref="Path"/> when the host called <see cref="AddNostosMcp"/>
    /// and registered <see cref="AuthorizationPolicy"/>; returns whether it did.
    /// </summary>
    public static bool MapNostosMcp(this IEndpointRouteBuilder endpoints, string? rateLimitPolicy = null)
    {
        if (endpoints.ServiceProvider.GetService<McpRegistered>() is null)
            return false;

        var authorization = endpoints.ServiceProvider
            .GetService<IOptions<AuthorizationOptions>>()?.Value;
        if (authorization?.GetPolicy(AuthorizationPolicy) is null)
            return false;

        // Build the tools (reflection, JSON schemas) now rather than on the
        // first call: clients give the opening server/discover probe only a
        // few seconds, and a cold first request can take longer than that.
        _ = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ModelContextProtocol.Server.McpServerOptions>>().Value.ToolCollection?.Count;

        var mcp = endpoints.MapMcp(Path).RequireAuthorization(AuthorizationPolicy);
        // Every authenticated request passes the host's gates first (see
        // IMcpRequestGate): authorization has run by the time the endpoint's
        // delegate does, so the gates see the signed-in caller.
        mcp.Add(endpoint =>
        {
            var inner = endpoint.RequestDelegate;
            if (inner is null)
                return;
            endpoint.RequestDelegate = async context =>
            {
                foreach (var gate in context.RequestServices.GetServices<IMcpRequestGate>())
                {
                    if (!await gate.AllowAsync(context.User, context, context.RequestAborted))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Nostos\", error=\"invalid_token\"";
                        return;
                    }
                }

                await inner(context);
            };
        });
        if (rateLimitPolicy is not null)
            mcp.RequireRateLimiting(rateLimitPolicy);
        return true;
    }
}

/// <summary>Marks a host that called <see cref="NostosMcp.AddNostosMcp"/>.</summary>
internal sealed class McpRegistered;

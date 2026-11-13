using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// What every MCP tool class shares (epic #1465). A tool runs one or two Ask
/// Nostos capabilities through <see cref="AssistantCapabilityRegistry"/>, so
/// validation, limits and writes are exactly Ask Nostos's, and only shapes the
/// result for an outside model: one result list, ids it can pass back, and an
/// <see cref="McpCitation"/> on everything it may quote.
///
/// Services come from the HTTP request's own scope, never the MCP server's: on
/// Cloud that scope is the one bound to the signed-in account's library.
/// </summary>
public abstract class NostosMcpTool(IHttpContextAccessor http)
{
    /// <summary>A search answers at most this many results in total.</summary>
    internal const int MaxSearchResults = 24;

    /// <summary>A book's notes answer at most this many per page.</summary>
    internal const int MaxNotesPage = 100;

    protected IServiceProvider Services =>
        http.HttpContext?.RequestServices
        ?? throw new McpException("Nostos can only answer over HTTP.");

    /// <summary>Citation and app-link helpers for this request.</summary>
    private protected McpCitations Citations => Services.GetRequiredService<McpCitations>();

    /// <summary>
    /// One capability's typed result. A refused call is the tool's error, with
    /// the capability's message renamed to the tools this client actually has.
    /// </summary>
    protected async Task<T> RunAsync<T>(string capability, object args, CancellationToken ct, AssistantToolContext? context = null)
    {
        var registry = Services.GetRequiredService<AssistantCapabilityRegistry>();
        var element = JsonSerializer.SerializeToElement(args, NostosMcp.Json);
        var result = await registry.InvokeAsync(AssistantSurface.Mcp, capability, element, context ?? new AssistantToolContext(), ct);
        if (!result.Success)
            throw new McpException(McpToolNames.Rename(result.ErrorMessage ?? result.ErrorCode ?? "Nostos could not do that."));

        if (typeof(T) == typeof(JsonElement))
            return (T)(object)(result.Data ?? JsonSerializer.SerializeToElement(new { }));

        if (result.Data is not { } data)
            throw new McpException("Nostos returned nothing.");

        return data.Deserialize<T>(NostosMcp.Json)
            ?? throw new McpException("Nostos returned nothing.");
    }

    /// <summary>A capability's result as it came, failures included, so a tool can read what a refusal carries.</summary>
    protected Task<AssistantToolResult> InvokeAsync(string capability, object args, AssistantToolContext context, CancellationToken ct) =>
        Services.GetRequiredService<AssistantCapabilityRegistry>().InvokeAsync(
            AssistantSurface.Mcp,
            capability,
            JsonSerializer.SerializeToElement(args, NostosMcp.Json),
            context,
            ct);

    /// <summary>The capability's payload read as <typeparamref name="T"/>, or null when it sent none.</summary>
    protected static T? Data<T>(AssistantToolResult result) where T : class =>
        result.Data is { } data ? data.Deserialize<T>(NostosMcp.Json) : null;

    /// <summary>A book as <c>list_books</c> and <c>add_book</c> summarise it.</summary>
    private protected static McpBookSummary Summary(BookDto book, McpCitations citations) =>
        new(
            book.Id,
            book.Title,
            McpText.Blank(book.Author),
            McpCitations.YearOf(book.PublishedDate),
            book.Type,
            book.ProgressPercent,
            citations.BookUrl(book.Id));
}

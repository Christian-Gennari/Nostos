using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The library change feed: <c>GET /api/library/changes</c>, Server-Sent Events
/// that say "the library changed" so open screens can re-read instead of waiting
/// for a reload.
///
/// The signal is the singleton library revision
/// (<see cref="Data.Models.LibraryState.StateVersion"/>). <see cref="NostosDbContext"/>
/// advances it on every save that touches portable user-owned state, whatever
/// made the save — the UI, MCP, Ask Nostos, an import or a restore — so the feed
/// cannot miss a write path, and no caller has to remember to publish anything.
///
/// The stream re-reads the revision through the request's own
/// <see cref="IDbContextFactory{TContext}"/>. A hosted composition that resolves
/// the database per account therefore scopes the feed per account with no extra
/// state: there is no process-wide broadcaster for one account's events to
/// reach another through.
///
/// The event carries only the revision. It names no entity, so the client
/// re-reads what it shows; the revision is opaque and only compared for change.
/// </summary>
public static class LibraryChangesEndpoints
{
    public const string Route = "/api/library/changes";

    /// <summary>How often the stream re-reads the revision.</summary>
    public const string PollMillisecondsKey = "Library:ChangesPollMilliseconds";

    /// <summary>How long the stream may go quiet before it writes a comment line.</summary>
    public const string HeartbeatSecondsKey = "Library:ChangesHeartbeatSeconds";

    /// <summary>
    /// How long one connection lives. The client's EventSource reconnects on its
    /// own after the server ends a stream, so a bounded lifetime costs one cheap
    /// reconnect and guarantees a forgotten tab cannot hold a proxy connection
    /// forever.
    /// </summary>
    public const string MaxLifetimeSecondsKey = "Library:ChangesMaxLifetimeSeconds";

    private const int DefaultPollMilliseconds = 1000;
    private const int DefaultHeartbeatSeconds = 15;
    private const int DefaultMaxLifetimeSeconds = 600;

    /// <summary>The reconnect delay the client is told to use after the stream ends.</summary>
    private const int RetryMilliseconds = 2000;

    public static IEndpointRouteBuilder MapLibraryChangesEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(
            Route,
            (HttpContext http, IDbContextFactory<NostosDbContext> contexts, IConfiguration configuration) =>
                StreamAsync(http, contexts, configuration));

        return routes;
    }

    private static async Task StreamAsync(
        HttpContext http,
        IDbContextFactory<NostosDbContext> contexts,
        IConfiguration configuration)
    {
        var ct = http.RequestAborted;
        var response = http.Response;

        // Same proxy contract as the import stream: an event-stream content type,
        // no intermediary caching, and nginx's opt-out from response buffering.
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var poll = TimeSpan.FromMilliseconds(Math.Clamp(
            configuration.GetValue(PollMillisecondsKey, DefaultPollMilliseconds), 50, 60_000));
        var heartbeat = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue(HeartbeatSecondsKey, DefaultHeartbeatSeconds), 1, 300));
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue(MaxLifetimeSecondsKey, DefaultMaxLifetimeSeconds), 1, 3600));

        var openedAt = DateTime.UtcNow;
        var lastWriteAt = DateTime.UtcNow;
        string? announced = null;

        try
        {
            await WriteAsync(response, $"retry: {RetryMilliseconds}\n\n", ct);

            while (!ct.IsCancellationRequested && DateTime.UtcNow - openedAt < lifetime)
            {
                var revision = await ReadRevisionAsync(contexts, ct);

                // The first frame always carries the current revision, so a client
                // that reconnects after a gap compares it with the last one it saw
                // and re-reads when it missed a change.
                if (revision != announced)
                {
                    announced = revision;
                    await WriteAsync(
                        response,
                        $"event: library\ndata: {{\"stateVersion\":\"{revision}\"}}\n\n",
                        ct);
                    lastWriteAt = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastWriteAt >= heartbeat)
                {
                    await WriteAsync(response, ": ping\n\n", ct);
                    lastWriteAt = DateTime.UtcNow;
                }

                await Task.Delay(poll, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The reader went away. Not an error: it is how every SSE stream ends.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The library could not be read (for example, a hosted library that is
            // being replaced). End the stream; the client reconnects after the
            // retry delay and resumes from the revision it receives then.
        }

        await TryCompleteAsync(response);
    }

    /// <summary>
    /// The current revision, or "0" before the library's first portable save
    /// created the singleton row. Values are only compared, never parsed.
    /// </summary>
    private static async Task<string> ReadRevisionAsync(
        IDbContextFactory<NostosDbContext> contexts,
        CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var revision = await db.LibraryStates
            .AsNoTracking()
            .Select(state => state.StateVersion)
            .FirstOrDefaultAsync(ct);

        // The revision is a number by construction (LibraryRevision fails closed
        // on anything else); keep the frame single-line JSON even if it were not.
        return string.IsNullOrEmpty(revision) || revision.Any(c => !char.IsAsciiDigit(c))
            ? "0"
            : revision;
    }

    private static async Task WriteAsync(HttpResponse response, string frame, CancellationToken ct)
    {
        await response.WriteAsync(frame, ct);
        await response.Body.FlushAsync(ct);
    }

    private static async Task TryCompleteAsync(HttpResponse response)
    {
        try
        {
            await response.CompleteAsync();
        }
        catch (Exception)
        {
            // Completing a response whose reader has already gone is expected to fail.
        }
    }
}

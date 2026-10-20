using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Middleware;
using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The library change feed: <c>GET /api/library/changes</c>, Server-Sent Events
/// that say "the library changed" so open screens can re-read instead of waiting
/// for a reload.
///
/// The signal is the singleton library revision
/// (<see cref="Data.Models.LibraryState.StateVersion"/>). <see cref="NostosDbContext"/>
/// advances it on every save that touches portable user-owned state, whatever
/// made the save — the UI, Ask Nostos, an import or a restore — so the feed
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

    /// <summary>
    /// How often the stream re-reads the revision without being woken. A write
    /// in this process wakes it at once (<see cref="LibraryChangeSignal"/>); the
    /// re-read only catches what the process cannot see (another instance, a
    /// restored or activated database), so it can be slow.
    /// </summary>
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

    private const int DefaultPollMilliseconds = 60_000;
    private const int DefaultHeartbeatSeconds = 15;
    private const int DefaultMaxLifetimeSeconds = 600;

    /// <summary>The reconnect delay the client is told to use after the stream ends.</summary>
    private const int RetryMilliseconds = 2000;

    public static IEndpointRouteBuilder MapLibraryChangesEndpoints(this IEndpointRouteBuilder routes)
    {
        // The stream lives for minutes, so it must not hold the host's request-
        // wide maintenance lease: an open tab would keep a restore or an
        // activation from draining. It takes a short lease around each read
        // instead, and ends when maintenance begins (the browser reconnects).
        routes.MapGet(
                Route,
                (HttpContext http, IDbContextFactory<NostosDbContext> contexts, IConfiguration configuration) =>
                    StreamAsync(http, contexts, configuration))
            .WithMetadata(new LibraryMaintenanceMemorySafe());

        return routes;
    }

    /// <summary>Ends the stream when the host has library maintenance under way.</summary>
    private sealed class MaintenanceStarted : Exception;

    /// <summary>
    /// Runs one database read under a short maintenance lease, or throws
    /// <see cref="MaintenanceStarted"/> when maintenance is already requested.
    /// Hosts without the barrier read directly.
    /// </summary>
    private static async Task<T> UnderLeaseAsync<T>(
        IMigrationMaintenanceGate? gate,
        Func<Task<T>> read,
        CancellationToken ct)
    {
        if (gate is null)
            return await read();
        if (gate.IsMaintenanceRequested)
            throw new MaintenanceStarted();

        await using var lease = await gate.EnterAsync(ct);
        return await read();
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
            configuration.GetValue(PollMillisecondsKey, DefaultPollMilliseconds), 50, 600_000));
        var heartbeat = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue(HeartbeatSecondsKey, DefaultHeartbeatSeconds), 1, 300));
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue(MaxLifetimeSecondsKey, DefaultMaxLifetimeSeconds), 1, 3600));

        var openedAt = DateTime.UtcNow;
        var lastWriteAt = DateTime.UtcNow;
        var gate = http.RequestServices.GetService<IMigrationMaintenanceGate>();

        try
        {
            await WriteAsync(response, $"retry: {RetryMilliseconds}\n\n", ct);

            // Wait first, then read: a change that commits while the revision is
            // being read still completes this wait, so it cannot be missed.
            string key = "";
            Task<LibraryChange> wake = Task.FromResult(default(LibraryChange));
            var announced = await UnderLeaseAsync(gate, async () =>
            {
                await using var db = await contexts.CreateDbContextAsync(ct);
                key = LibraryChangeSignal.KeyFor(db);
                wake = LibraryChangeSignal.NextAsync(key);
                return await ReadRevisionAsync(db, ct);
            }, ct);

            // The first frame always carries the current revision, so a client
            // that reconnects after a gap compares it with the last one it saw
            // and re-reads when it missed a change.
            var lastReadAt = DateTime.UtcNow;
            await WriteRevisionAsync(response, announced, progressOnly: false, ct);
            lastWriteAt = DateTime.UtcNow;

            // True while every change since the last announcement only moved a
            // reading position; null when nothing is pending.
            bool? pendingPositionOnly = null;

            while (!ct.IsCancellationRequested)
            {
                var remaining = lifetime - (DateTime.UtcNow - openedAt);
                if (remaining <= TimeSpan.Zero)
                    break;

                if (DateTime.UtcNow - lastWriteAt >= heartbeat)
                {
                    await WriteAsync(response, ": ping\n\n", ct);
                    lastWriteAt = DateTime.UtcNow;
                }

                if (DateTime.UtcNow - lastReadAt < poll)
                {
                    // Sleep until a change, the next heartbeat, the slow re-read
                    // or the end of the stream, whichever comes first. Nothing is
                    // read while the library is quiet.
                    var now = DateTime.UtcNow;
                    var wait = Min(remaining, Min(poll - (now - lastReadAt), heartbeat - (now - lastWriteAt)));
                    using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var first = await Task.WhenAny(wake, Task.Delay(Max(wait, TimeSpan.Zero), timer.Token));
                    timer.Cancel();
                    ct.ThrowIfCancellationRequested();

                    if (first != wake)
                        continue;

                    var change = await wake;
                    wake = LibraryChangeSignal.NextAsync(key);
                    pendingPositionOnly = (pendingPositionOnly ?? true) && change.ProgressOnly;
                }

                // Woken by a change, or the slow re-read is due.
                var revision = await UnderLeaseAsync(gate, () => ReadRevisionAsync(contexts, ct), ct);
                lastReadAt = DateTime.UtcNow;
                if (revision != announced)
                {
                    announced = revision;
                    // A change found by the re-read alone is of unknown kind.
                    await WriteRevisionAsync(response, revision, pendingPositionOnly == true, ct);
                    lastWriteAt = DateTime.UtcNow;
                    pendingPositionOnly = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The reader went away. Not an error: it is how every SSE stream ends.
        }
        catch (MaintenanceStarted)
        {
            // A restore or an activation is under way. End the stream; the client
            // reconnects after the retry delay and, once maintenance is over,
            // compares the revision it then receives with the last one it saw.
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
        return await ReadRevisionAsync(db, ct);
    }

    private static async Task<string> ReadRevisionAsync(NostosDbContext db, CancellationToken ct)
    {
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

    /// <summary>
    /// One "library changed" frame. <c>progressOnly</c> is added only when every
    /// change it covers moved a reading position and nothing else, so a screen
    /// may pick it up lazily; its absence means "re-read now".
    /// </summary>
    private static Task WriteRevisionAsync(
        HttpResponse response,
        string revision,
        bool progressOnly,
        CancellationToken ct) =>
        WriteAsync(
            response,
            progressOnly
                ? $"event: library\ndata: {{\"stateVersion\":\"{revision}\",\"progressOnly\":true}}\n\n"
                : $"event: library\ndata: {{\"stateVersion\":\"{revision}\"}}\n\n",
            ct);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

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

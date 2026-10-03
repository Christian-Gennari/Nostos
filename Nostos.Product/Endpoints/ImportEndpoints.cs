using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The background-import feed: what is being imported right now, and what a
/// restart interrupted.
///
/// A dedicated surface rather than a field on the library query, because the two
/// answer different questions. The library is a result set — sorted, filtered,
/// paged, and legitimately empty when the user asks for something nothing
/// matches. An import in flight is none of those things: it must stay visible
/// whatever the sort or filter is, and watching it must not mean refetching the
/// library every second.
///
/// Two shapes over one source of truth:
///   - <c>GET /api/imports/active</c> — the current list, used to (re)sync. Never
///     assume an event was buffered; a client that reconnects asks again.
///   - <c>GET /api/imports/stream</c> — Server-Sent Events, so progress arrives
///     when it changes instead of on a timer.
/// </summary>
public static class ImportEndpoints
{
    /// <summary>
    /// How long the stream may go quiet before it writes a comment line.
    ///
    /// An idle connection is indistinguishable from a dead one to every hop in
    /// between, and a slow transcode can leave the feed silent for minutes; a
    /// proxy that has given up on the socket turns "still working" into a frozen
    /// bar. Configurable so a test can drive it in a second.
    /// </summary>
    public const string HeartbeatSecondsKey = "Imports:StreamHeartbeatSeconds";

    private const int DefaultHeartbeatSeconds = 15;

    /// <summary>How often the stream re-reads the job store while work is in flight.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the stream stays open once nothing is in flight. The client is
    /// expected to close its own connection when the last import finishes; this
    /// is only a backstop, so a client that forgot cannot hold a proxy
    /// connection open forever.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private const int MaxReconciledEntries = 50;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/imports");

        // The current list. Pollable on purpose: the SSE stream is the live path,
        // but a client that reconnects must re-read rather than replay, and this
        // is also what a page load uses to decide whether it needs a stream at
        // all.
        group.MapGet(
            "/active",
            async (
                IAcquisitionJobManager jobs,
                IDbContextFactory<NostosDbContext> contexts,
                CancellationToken ct) =>
                Results.Ok(await ReadActiveAsync(jobs, contexts, ct)));

        // The live feed. Writes the response itself: Server-Sent Events is a
        // long-lived body, not a value that can be returned.
        group.MapGet(
            "/stream",
            (
                HttpContext http,
                IAcquisitionJobManager jobs,
                IDbContextFactory<NostosDbContext> contexts,
                IConfiguration configuration,
                IOptions<JsonOptions> jsonOptions) =>
                StreamAsync(http, jobs, contexts, configuration, jsonOptions.Value.SerializerOptions));

        return routes;
    }

    /// <summary>
    /// Everything the UI should show: the jobs in flight, enriched with the book
    /// each one already created, plus the interrupted imports a restart left
    /// behind.
    ///
    /// The reconciled half is what makes a page loaded AFTER a restart honest.
    /// Jobs live in memory only, so the process that was running the import is
    /// gone and nothing would otherwise be reported; the book row survives, and
    /// the reconciliation worker has already marked it Failed with a known
    /// message. Without this the import would simply vanish from the UI and the
    /// user would never learn it did not finish.
    /// </summary>
    private static async Task<IReadOnlyList<ImportActivityDto>> ReadActiveAsync(
        IAcquisitionJobManager jobs,
        IDbContextFactory<NostosDbContext> contexts,
        CancellationToken ct)
    {
        var active = jobs.ListActive();

        await using var db = await contexts.CreateDbContextAsync(ct);

        var entries = new List<ImportActivityDto>(active.Count);

        var jobBookIds = active
            .Where(job => job.BookId.HasValue)
            .Select(job => job.BookId!.Value)
            .Distinct()
            .ToList();

        // The restart MESSAGE is the marker, and it is not bounded by a time
        // window: an interrupted import can be an old book. A 30-day lookback on
        // `CreatedAt` was exactly wrong — the row that prompted it was created 35
        // days earlier, so the feed silently dropped the very import a restart had
        // just killed. (There is no `UpdatedAt` on a book to measure "recently
        // reconciled" by, and inventing one would be a schema change for a display
        // filter.) The count cap and Dismiss are what keep the list bounded.
        var interrupted = await db.Books
            .Where(book => book.Status == BookStatus.Failed
                && book.StatusMessage == AcquisitionJobMessages.InterruptedByRestart)
            .OrderByDescending(book => book.CreatedAt)
            .Take(MaxReconciledEntries)
            .Select(book => book.Id)
            .ToListAsync(ct);

        var bookIds = jobBookIds.Concat(interrupted).Distinct().ToList();

        var books = bookIds.Count == 0
            ? new List<BookLookup>()
            : (await db.Books
                .Where(book => bookIds.Contains(book.Id))
                .Select(book => new BookLookup(
                    book.Id,
                    book.Title,
                    book.Author,
                    book.FileDetails.CoverFileName != null,
                    book.CreatedAt))
                .ToListAsync(ct));

        var byId = books.ToDictionary(book => book.Id);

        // Retry is only offered where the source is known: provider + external id
        // is exactly what an acquire request needs, and it is stored on the
        // acquisition row the import created.
        var acquisitions = bookIds.Count == 0
            ? new List<AcquisitionLookup>()
            : (await db.BookAcquisitions
                .Where(acquisition => bookIds.Contains(acquisition.BookId))
                .Select(acquisition => new AcquisitionLookup(
                    acquisition.BookId,
                    acquisition.ProviderId,
                    acquisition.ExternalId,
                    acquisition.AssetId))
                .ToListAsync(ct));

        var acquisitionByBookId = acquisitions
            .GroupBy(acquisition => acquisition.BookId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var job in active)
        {
            entries.Add(FromJob(job, Lookup(byId, job.BookId), Lookup(acquisitionByBookId, job.BookId)));
        }

        foreach (var bookId in interrupted)
        {
            // A job in flight for the same book already owns the row; a second
            // entry would show the same import twice.
            if (entries.Any(entry => entry.BookId == bookId))
                continue;

            entries.Add(FromInterruptedBook(bookId, Lookup(byId, bookId), Lookup(acquisitionByBookId, bookId)));
        }

        return entries;
    }

    private static BookLookup? Lookup(IReadOnlyDictionary<Guid, BookLookup> map, Guid? id) =>
        id.HasValue && map.TryGetValue(id.Value, out var found) ? found : null;

    private static AcquisitionLookup? Lookup(IReadOnlyDictionary<Guid, AcquisitionLookup> map, Guid? id) =>
        id.HasValue && map.TryGetValue(id.Value, out var found) ? found : null;

    private static ImportActivityDto FromJob(
        AcquisitionJobStatus job,
        BookLookup? book,
        AcquisitionLookup? acquisition) =>
        new(
            Id: job.JobId,
            Source: "job",
            State: job.State.ToString().ToLowerInvariant(),
            Stage: job.Stage,
            // The cap lives on the status record, so every consumer of the job
            // store inherits it rather than each endpoint re-implementing it.
            Percent: job.ReportedPercent,
            Detail: job.Detail,
            ProviderId: job.ProviderId,
            ExternalId: job.ExternalId,
            AssetId: job.AssetId,
            BookId: job.BookId,
            Title: book?.Title,
            Author: book?.Author,
            CoverUrl: CoverUrl(job.BookId, book),
            ErrorCode: job.ErrorCode,
            Message: job.Message,
            CreatedAt: job.CreatedAt,
            UpdatedAt: job.UpdatedAt,
            CanRetry: !string.IsNullOrWhiteSpace(job.ProviderId)
                && !string.IsNullOrWhiteSpace(job.ExternalId));

    private static ImportActivityDto FromInterruptedBook(
        Guid bookId,
        BookLookup? book,
        AcquisitionLookup? acquisition) =>
        new(
            Id: bookId.ToString("D"),
            Source: "reconciled",
            State: "failed",
            Stage: "failed",
            // Never 100: the file never landed.
            Percent: 0,
            Detail: null,
            ProviderId: acquisition?.ProviderId,
            ExternalId: acquisition?.ExternalId,
            AssetId: acquisition?.AssetId,
            BookId: bookId,
            Title: book?.Title,
            Author: book?.Author,
            CoverUrl: CoverUrl(bookId, book),
            ErrorCode: "import_interrupted",
            Message: AcquisitionJobMessages.InterruptedByRestart,
            CreatedAt: book?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt: book?.CreatedAt ?? DateTime.UtcNow,
            CanRetry: acquisition is not null
                && !string.IsNullOrWhiteSpace(acquisition.ProviderId)
                && !string.IsNullOrWhiteSpace(acquisition.ExternalId));

    private static string? CoverUrl(Guid? bookId, BookLookup? book) =>
        bookId.HasValue && book is { HasCover: true }
            ? $"/api/books/{bookId.Value}/cover"
            : null;

    /// <summary>
    /// The Server-Sent Events body.
    ///
    /// Written straight to the response rather than returned as a value: the
    /// response stays open for the life of the import, which no result object can
    /// express.
    /// </summary>
    private static async Task StreamAsync(
        HttpContext http,
        IAcquisitionJobManager jobs,
        IDbContextFactory<NostosDbContext> contexts,
        IConfiguration configuration,
        JsonSerializerOptions serializerOptions)
    {
        var ct = http.RequestAborted;
        var response = http.Response;

        // These three headers are the difference between a live feed and a frozen
        // one behind a reverse proxy:
        //   - text/event-stream is what the client's EventSource accepts at all;
        //   - no-cache stops an intermediary serving a stale copy of a live feed;
        //   - X-Accel-Buffering: no is nginx's documented opt-out from response
        //     buffering. WITHOUT IT the proxy holds the frames until its buffer
        //     fills or the stream ends, so the UI sits at 0% and then jumps to
        //     done — which is exactly the bug this endpoint exists to fix.
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        // Kestrel would otherwise coalesce small writes; a progress frame is a
        // few hundred bytes, so it would sit in the buffer until something else
        // flushed it.
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var heartbeat = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue(HeartbeatSecondsKey, DefaultHeartbeatSeconds), 1, 300));

        var announced = new HashSet<string>(StringComparer.Ordinal);
        // Null, not empty: an EMPTY import list must still be the first frame, so a
        // client that connects while nothing is running gets an answer instead of
        // silence until the first heartbeat.
        string? lastSnapshot = null;
        var lastWriteAt = DateTime.UtcNow;
        var idleSince = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var entries = await ReadActiveAsync(jobs, contexts, ct);
                var all = jobs.List();

                // A job that finished since the last tick is announced ONCE, as a
                // terminal event: the entry then leaves the in-flight list, and
                // without this the client would just watch it disappear with no
                // outcome to react to.
                foreach (var job in all.Where(status => status.IsFinished))
                {
                    if (!announced.Add(job.JobId))
                        continue;

                    await WriteEventAsync(
                        response,
                        job.State == AcquisitionJobState.Succeeded ? "done" : "failed",
                        [Budget(job, entries)],
                        serializerOptions,
                        ct);

                    lastWriteAt = DateTime.UtcNow;
                }

                // An interrupted import is terminal from its first appearance, so
                // it is announced the same way — once — and then stays in every
                // snapshot so a reconnect still sees it.
                foreach (var entry in entries.Where(entry => entry.Source == "reconciled"))
                {
                    if (!announced.Add(entry.Id))
                        continue;

                    await WriteEventAsync(response, "failed", [entry], serializerOptions, ct);
                    lastWriteAt = DateTime.UtcNow;
                }

                var snapshot = string.Join(
                    '|',
                    entries.Select(entry =>
                        $"{entry.Id}:{entry.State}:{entry.Percent}:{entry.Detail}:{entry.Message}"));

                if (snapshot != lastSnapshot)
                {
                    lastSnapshot = snapshot;
                    await WriteEventAsync(response, "progress", entries, serializerOptions, ct);
                    lastWriteAt = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastWriteAt >= heartbeat)
                {
                    // A bare comment line: the client ignores it, every proxy in
                    // between sees traffic and keeps the connection.
                    await WriteAsync(response, ": ping\n\n", ct);
                    lastWriteAt = DateTime.UtcNow;
                }

                // "live" means work that can still change. A reconciled entry is
                // terminal and will never move again, so it must not hold the
                // connection open on its own.
                var live = entries.Any(entry => entry.State is "queued" or "running");

                if (live)
                    idleSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - idleSince >= IdleTimeout)
                    break;

                var cadence = live ? PollInterval : heartbeat;

                try
                {
                    await Task.Delay(cadence, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The reader went away (closed tab, navigation, proxy timeout). Not an
            // error: it is how every SSE stream ends.
        }

        await TryCompleteAsync(response);
    }

    /// <summary>
    /// Finds the enriched form of a just-finished job's book, when the snapshot
    /// happens to hold it, so a terminal event carries the title the UI needs to
    /// name what finished.
    /// </summary>
    private static ImportActivityDto Budget(AcquisitionJobStatus job, IReadOnlyList<ImportActivityDto> entries) =>
        entries.FirstOrDefault(entry => entry.Id == job.JobId)
        ?? new ImportActivityDto(
            Id: job.JobId,
            Source: "job",
            State: job.State.ToString().ToLowerInvariant(),
            Stage: job.Stage,
            Percent: job.ReportedPercent,
            Detail: job.Detail,
            ProviderId: job.ProviderId,
            ExternalId: job.ExternalId,
            AssetId: job.AssetId,
            BookId: job.BookId,
            Title: null,
            Author: null,
            CoverUrl: null,
            ErrorCode: job.ErrorCode,
            Message: job.Message,
            CreatedAt: job.CreatedAt,
            UpdatedAt: job.UpdatedAt,
            CanRetry: !string.IsNullOrWhiteSpace(job.ProviderId)
                && !string.IsNullOrWhiteSpace(job.ExternalId));

    private static Task WriteEventAsync(
        HttpResponse response,
        string name,
        IReadOnlyList<ImportActivityDto> payload,
        JsonSerializerOptions serializerOptions,
        CancellationToken ct)
    {
        // Single-line JSON by construction: a data: field ends at the newline, so
        // anything pretty-printed would be truncated by the client's parser.
        var json = JsonSerializer.Serialize(payload, serializerOptions);

        return WriteAsync(response, $"event: {name}\ndata: {json}\n\n", ct);
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
            // Completing a response whose reader has already gone is expected to
            // fail; there is nothing left to do about it.
        }
    }

    private sealed record BookLookup(Guid Id, string Title, string? Author, bool HasCover, DateTime CreatedAt);

    private sealed record AcquisitionLookup(Guid BookId, string ProviderId, string ExternalId, string AssetId);
}

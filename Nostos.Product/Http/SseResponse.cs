using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Nostos.Product.Http;

/// <summary>
/// The Server-Sent Events response contract: the proxy-safe headers, frame
/// framing and flush, and the completion that tolerates a reader who has
/// already gone.
///
/// Both streaming routes (<c>GET /api/imports/stream</c> and
/// <c>GET /api/library/changes</c>) had their own copy of this setup. It is one
/// helper now because the headers are load-bearing and must not drift:
///
/// <list type="bullet">
/// <item><c>text/event-stream</c> is what a client's EventSource accepts at all;</item>
/// <item><c>no-cache</c> stops an intermediary serving a stale copy of a live feed;</item>
/// <item><c>X-Accel-Buffering: no</c> is nginx's documented opt-out from response
/// buffering. Without it the proxy holds frames until its buffer fills or the
/// stream ends, so the UI sits still and then jumps — the exact bug these
/// streams exist to fix.</item>
/// </list>
///
/// The helper owns the frame bytes and the flush; each route keeps its own
/// cadence (what to send, and when), because an import feed and a revision feed
/// answer different questions.
/// </summary>
public sealed class SseResponse
{
    private readonly HttpResponse _response;

    private SseResponse(HttpResponse response) => _response = response;

    /// <summary>
    /// Sets the SSE headers and disables response buffering. Call once, before
    /// the first frame.
    /// </summary>
    public static SseResponse Begin(HttpContext http)
    {
        var response = http.Response;

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        // Kestrel would otherwise coalesce small writes; a progress frame is a
        // few hundred bytes, so it would sit in the buffer until something else
        // flushed it.
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        return new SseResponse(response);
    }

    /// <summary>The response this helper writes to, for routes that need its headers.</summary>
    public HttpResponse Response => _response;

    /// <summary>
    /// One raw frame, written and flushed so it leaves the process now rather
    /// than when a buffer fills.
    /// </summary>
    public async Task WriteFrameAsync(string frame, CancellationToken ct)
    {
        await _response.WriteAsync(frame, ct);
        await _response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// One named event: <c>event: name</c> then <c>data: payload</c>.
    /// <paramref name="data"/> must be single-line (a <c>data:</c> field ends at
    /// the newline, so pretty-printed JSON would be truncated by the client's
    /// parser).
    /// </summary>
    public Task WriteEventAsync(string name, string data, CancellationToken ct) =>
        WriteFrameAsync($"event: {name}\ndata: {data}\n\n", ct);

    /// <summary>
    /// A heartbeat the client ignores but every proxy in between sees as
    /// traffic, which is what keeps an idle connection from being dropped.
    /// </summary>
    public Task WriteCommentAsync(string comment, CancellationToken ct) =>
        WriteFrameAsync($": {comment}\n\n", ct);

    /// <summary>
    /// The reconnect delay handed to an EventSource. It applies to the
    /// reconnect that follows this stream ending.
    /// </summary>
    public Task WriteRetryAsync(int milliseconds, CancellationToken ct) =>
        WriteFrameAsync($"retry: {milliseconds}\n\n", ct);

    /// <summary>
    /// Completes the response. A reader that has already gone makes this fail,
    /// which is expected for every stream that ends; there is nothing left to do
    /// about it.
    /// </summary>
    public async Task CompleteAsync()
    {
        try
        {
            await _response.CompleteAsync();
        }
        catch (Exception)
        {
            // Completing a response whose reader has already gone is expected to
            // fail; there is nothing left to do about it.
        }
    }
}

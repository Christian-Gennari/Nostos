using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>
/// The two pages the public host ever shows a person (issue #1470): "approve
/// this in Nostos" and "this app cannot connect". Self-contained: inline
/// styles and script under a nonce CSP, no app bundle, no fonts, nothing
/// fetched but the status poll. Every value from the app is HTML-encoded.
/// </summary>
public static class McpOAuthPages
{
    public static IResult Waiting(string clientName, string clientHost, string userCode, string requestId)
    {
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var name = WebUtility.HtmlEncode(clientName);
        var host = WebUtility.HtmlEncode(clientHost);
        var code = WebUtility.HtmlEncode(userCode);
        var statusUrl = WebUtility.HtmlEncode($"{McpOAuthPaths.AuthorizeStatus}?request={Uri.EscapeDataString(requestId)}");
        var body = $$"""
            <main>
              <p class="eyebrow">Nostos</p>
              <h1>{{name}} wants to connect to your Nostos</h1>
              <p class="from">The request comes from <strong>{{host}}</strong>.</p>
              <ol>
                <li>Open Nostos on your own network.</li>
                <li>Go to <strong>Settings → Connected apps</strong>.</li>
                <li>Approve the request with this code:</li>
              </ol>
              <p class="code" aria-label="Code {{string.Join(' ', userCode.ToCharArray())}}">{{code}}</p>
              <p class="status" id="status" role="status">Waiting for your approval…</p>
              <p class="small">This page goes on by itself once you approve. The request ends after 10 minutes. If you did not start this, deny it in Settings or close this page.</p>
            </main>
            <script nonce="{{nonce}}">
            (function () {
              var url = document.body.getAttribute('data-status');
              var status = document.getElementById('status');
              function poll() {
                fetch(url, { headers: { 'Accept': 'application/json' }, cache: 'no-store' })
                  .then(function (r) { return r.json(); })
                  .then(function (s) {
                    if (s.status === 'done' && s.redirect) { status.textContent = 'Approved. Going back to the app…'; window.location.replace(s.redirect); return; }
                    if (s.status === 'unknown') { status.textContent = 'This request has ended. Start again from the app.'; return; }
                    setTimeout(poll, 2000);
                  })
                  .catch(function () { setTimeout(poll, 4000); });
              }
              setTimeout(poll, 2000);
            })();
            </script>
            """;
        return Page("Connect to Nostos", body, nonce, statusUrl, StatusCodes.Status200OK);
    }

    public static IResult Error(string message)
    {
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var body = $"""
            <main>
              <p class="eyebrow">Nostos</p>
              <h1>This app cannot connect</h1>
              <p>{WebUtility.HtmlEncode(message)}</p>
            </main>
            """;
        return Page("Cannot connect to Nostos", body, nonce, null, StatusCodes.Status400BadRequest);
    }

    private static IResult Page(string title, string main, string nonce, string? statusUrl, int statusCode)
    {
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="referrer" content="no-referrer">
            <title>{{title}}</title>
            <style nonce="{{nonce}}">
              :root { color-scheme: light dark; --bg: #f6f3ec; --fg: #1f2a24; --muted: #5b655f; --tile: #2f4a3a; --paper: #fbf8f1; }
              @media (prefers-color-scheme: dark) { :root { --bg: #151a17; --fg: #e8e4da; --muted: #a3aaa5; --tile: #9cc0a8; --paper: #1d2420; } }
              body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--fg); font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
              main { max-width: 30rem; margin: 1.5rem; padding: 2rem; background: var(--paper); border-radius: 12px; box-shadow: 0 1px 3px rgba(0,0,0,.12); }
              .eyebrow { margin: 0 0 .5rem; color: var(--tile); font-weight: 600; letter-spacing: .04em; text-transform: uppercase; font-size: .8rem; }
              h1 { margin: 0 0 .75rem; font-size: 1.35rem; line-height: 1.3; }
              ol { padding-left: 1.25rem; }
              .from, .small, .status { color: var(--muted); }
              .small { font-size: .875rem; }
              .code { margin: .5rem 0 1rem; font: 700 2.5rem/1 ui-monospace, "SF Mono", Menlo, monospace; letter-spacing: .3em; color: var(--tile); }
            </style>
            </head>
            <body{{(statusUrl is null ? "" : $" data-status=\"{statusUrl}\"")}}>
            {{main}}
            </body>
            </html>
            """;
        return new HtmlResult(html, nonce, statusCode);
    }

    private sealed class HtmlResult(string html, string nonce, int statusCode) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            var response = context.Response;
            response.StatusCode = statusCode;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.ContentSecurityPolicy =
                $"default-src 'none'; style-src 'nonce-{nonce}'; script-src 'nonce-{nonce}'; connect-src 'self'; "
                + "img-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            return response.WriteAsync(html, Encoding.UTF8);
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Nostos.Backend.Tests.Support;

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentBag<HttpRequestMessage> _recordedRequests = new();
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private Func<HttpRequestMessage, HttpResponseMessage>? _fallbackHandler;

    public IReadOnlyList<HttpRequestMessage> RecordedRequests => _recordedRequests.ToArray();

    public IReadOnlyList<string> RecordedRequestPaths => _recordedRequests
        .Select(r => r.RequestUri?.PathAndQuery ?? r.RequestUri?.ToString() ?? string.Empty)
        .ToArray();

    public StubHttpMessageHandler Register(string pathAndQuery, HttpResponseMessage response)
    {
        _routes[pathAndQuery] = _ => CloneResponse(response);
        return this;
    }

    public StubHttpMessageHandler Register(string pathAndQuery, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _routes[pathAndQuery] = handler;
        return this;
    }

    public StubHttpMessageHandler RegisterXml(string pathAndQuery, string xmlContent, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return Register(pathAndQuery, _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(xmlContent, System.Text.Encoding.UTF8, "application/atom+xml")
        });
    }

    public StubHttpMessageHandler RegisterHtml(string pathAndQuery, string htmlContent, HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable)
    {
        return Register(pathAndQuery, _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(htmlContent, System.Text.Encoding.UTF8, "text/html")
        });
    }

    public StubHttpMessageHandler SetFallback(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _fallbackHandler = handler;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _recordedRequests.Add(request);

        var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;

        if (_routes.TryGetValue(pathAndQuery, out var handler))
        {
            return Task.FromResult(handler(request));
        }

        if (_fallbackHandler is not null)
        {
            return Task.FromResult(_fallbackHandler(request));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            RequestMessage = request
        });
    }

    private static HttpResponseMessage CloneResponse(HttpResponseMessage src)
    {
        var clone = new HttpResponseMessage(src.StatusCode);
        if (src.Content != null)
        {
            var ms = new MemoryStream();
            src.Content.CopyToAsync(ms).GetAwaiter().GetResult();
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            foreach (var h in src.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }
        foreach (var h in src.Headers)
        {
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        return clone;
    }
}

public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly StubHttpMessageHandler _handler;
    private readonly Uri _baseAddress;
    private readonly ConcurrentDictionary<string, StubHttpMessageHandler> _handlersByName = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<string> _requestedClientNames = new();

    public StubHttpMessageHandler Handler => _handler;

    /// <summary>Every client name a host asked for, so tests can pin the named-client contract.</summary>
    public IReadOnlyList<string> RequestedClientNames => _requestedClientNames.ToArray();

    public StubHttpClientFactory(StubHttpMessageHandler? handler = null, Uri? baseAddress = null)
    {
        _handler = handler ?? new StubHttpMessageHandler();
        _baseAddress = baseAddress ?? new Uri("https://www.gutenberg.org");
    }

    /// <summary>Routes one named client to its own handler; other names keep the shared one.</summary>
    public StubHttpClientFactory RegisterHandler(string clientName, StubHttpMessageHandler handler)
    {
        _handlersByName[clientName] = handler;
        return this;
    }

    public HttpClient CreateClient(string name)
    {
        _requestedClientNames.Add(name);
        var handler = _handlersByName.TryGetValue(name, out var registered) ? registered : _handler;
        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = _baseAddress
        };
    }
}

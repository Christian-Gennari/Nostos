using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers;

/// <summary>
/// Shared HTTP request, response and stream-read wrapper for content providers.
///
/// Everything a provider does over HTTP goes through here: creating the client
/// for one operation (so a singleton never holds a DNS-pinned client), applying
/// the product identity headers in one place, checking the status with one
/// message shape, and turning malformed XML/JSON and connectivity, stream I/O or
/// client-timeout failures into <see cref="ProviderException"/>. Caller
/// cancellation is always propagated as <see cref="OperationCanceledException"/>.
/// </summary>
internal static class ProviderHttp
{
    /// <summary>
    /// The product identity sent on every provider request. Product composition
    /// configures it on each named client; providers also apply it when they
    /// create the client, so a host-supplied <see cref="IHttpClientFactory"/>
    /// cannot silently turn provider access anonymous.
    /// </summary>
    public const string ProductUserAgent =
        "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos)";

    public const string JsonMediaType = "application/json";

    /// <summary>
    /// Creates the named client for one operation and applies the headers this
    /// request needs. Replacing rather than appending means a client that already
    /// carries the configured headers (product composition) cannot end up with a
    /// duplicated <c>Accept</c>.
    /// </summary>
    public static HttpClient Client(
        IHttpClientFactory httpClientFactory,
        string clientName,
        string? userAgent = null,
        string? accept = null)
    {
        var client = httpClientFactory.CreateClient(clientName);

        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? ProductUserAgent);

        if (accept is not null)
        {
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        }

        return client;
    }

    /// <summary>
    /// The catalogue's ordinary "no such item" answer. Each provider decides
    /// whether that is an empty result or a failure in its own context.
    /// </summary>
    public static bool IsMissing(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>
    /// Rejects a non-success status as an unavailable source, naming the status
    /// and what answered. <paramref name="source"/> is the provider's own wording
    /// ("the catalogue", "WS Export"), so the message stays provider-specific.
    /// </summary>
    public static void EnsureSuccess(
        HttpResponseMessage response,
        string providerId,
        string source)
    {
        if (!response.IsSuccessStatusCode)
            throw ProviderException.UnavailableFor(
                providerId,
                $"{source} answered HTTP {(int)response.StatusCode}");
    }

    public static Task<HttpResponseMessage> GetHeadersAsync(
        HttpClient client,
        string providerId,
        string requestUri,
        CancellationToken ct) =>
        ExecuteAsync(
            providerId,
            ct,
            token => client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, token));

    public static Task<HttpResponseMessage> SendHeadersAsync(
        HttpClient client,
        string providerId,
        HttpRequestMessage request,
        CancellationToken ct) =>
        ExecuteAsync(
            providerId,
            ct,
            token => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token));

    /// <summary>
    /// Parses an Atom/XML body. A body that is not XML (a maintenance page, an
    /// HTML error document) is a source problem, never an empty catalogue.
    /// </summary>
    public static Task<XDocument> ReadXmlAsync(
        HttpResponseMessage response,
        string providerId,
        CancellationToken ct) =>
        ExecuteAsync(providerId, ct, async token =>
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                return await XDocument.LoadAsync(stream, LoadOptions.None, token);
            }
            catch (XmlException ex)
            {
                throw ProviderException.InvalidResponse(providerId, ex.Message);
            }
        });

    /// <summary>Parses a JSON body the caller disposes with <c>using</c>.</summary>
    public static Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string providerId,
        CancellationToken ct) =>
        ExecuteAsync(providerId, ct, async token =>
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                return await JsonDocument.ParseAsync(stream, cancellationToken: token);
            }
            catch (JsonException ex)
            {
                throw ProviderException.InvalidResponse(providerId, ex.Message);
            }
        });

    /// <summary>
    /// Parses a JSON body into a model. Returns null for an empty body so the
    /// provider can say what was missing in its own wording.
    /// </summary>
    public static Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response,
        string providerId,
        JsonSerializerOptions options,
        CancellationToken ct) =>
        ExecuteAsync(providerId, ct, async token =>
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                return await JsonSerializer.DeserializeAsync<T>(stream, options, token);
            }
            catch (JsonException ex)
            {
                throw ProviderException.InvalidResponse(providerId, ex.Message);
            }
        });

    public static async Task<T> ExecuteAsync<T>(
        string providerId,
        CancellationToken ct,
        Func<CancellationToken, Task<T>> action)
    {
        try
        {
            return await action(ct);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderException.UnavailableFor(providerId, ex.Message);
        }
        catch (IOException ex)
        {
            throw ProviderException.UnavailableFor(providerId, ex.Message);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as an OperationCanceledException (typically
            // TaskCanceledException) unrelated to the caller's token.
            throw ProviderException.UnavailableFor(providerId, ex.Message);
        }
    }
}

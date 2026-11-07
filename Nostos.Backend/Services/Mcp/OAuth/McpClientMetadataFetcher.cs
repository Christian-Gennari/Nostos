using Nostos.Backend.Data.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>What a client said about itself in its metadata document.</summary>
public sealed record McpClientMetadata(string ClientId, string ClientName, IReadOnlyList<string> RedirectUris)
{
    /// <summary>The host of the client id, which the owner sees next to the name.</summary>
    public string ClientHost => new Uri(ClientId).Host;

    /// <summary>
    /// Whether <paramref name="redirectUri"/> is one the document lists:
    /// exactly, or for a loopback address the same address with any port
    /// (RFC 8252 §7.3, for apps on the owner's own computer).
    /// </summary>
    public bool AllowsRedirect(string redirectUri)
    {
        foreach (var listed in RedirectUris)
        {
            if (string.Equals(listed, redirectUri, StringComparison.Ordinal))
                return true;
            if (McpClientMetadataFetcher.IsLoopbackRedirect(listed)
                && Uri.TryCreate(redirectUri, UriKind.Absolute, out var given)
                && McpClientMetadataFetcher.IsLoopbackRedirect(redirectUri)
                && Uri.TryCreate(listed, UriKind.Absolute, out var known)
                && string.Equals(given.Host, known.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(given.PathAndQuery, known.PathAndQuery, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A fetch either found the client or says, in words for the owner, why not.</summary>
public sealed record McpClientMetadataResult(McpClientMetadata? Metadata, string? Error)
{
    public static McpClientMetadataResult Fail(string error) => new(null, error);
}

public interface IMcpClientMetadataFetcher
{
    Task<McpClientMetadataResult> FetchAsync(string clientId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads an OAuth client's metadata document (CIMD, issue #1470): the client
/// id is an https URL, and the JSON at that URL names the client and its
/// redirect URIs. Nothing else registers clients.
///
/// The fetch is the one request this server makes to an address a stranger
/// chose, so it is boxed in: https only, also after a redirect (at most
/// three); every address the host resolves to must be public
/// (<see cref="McpNetworkAddressFilter"/>), checked before the request and
/// again when the socket connects, so a DNS answer that changes in between
/// cannot point it inward; no proxy; 5 seconds for the whole fetch; at most
/// 16 KiB. The document must name itself (<c>client_id</c> equal to the URL),
/// list https or loopback redirect URIs, and give a <c>client_name</c>. A good
/// document is kept for 10 minutes.
/// </summary>
public sealed class McpClientMetadataFetcher : IMcpClientMetadataFetcher, IDisposable
{
    public const int MaxDocumentBytes = 16 * 1024;
    public const int MaxRedirects = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private const int MaxCached = 256;
    private const int MaxRedirectUris = 20;

    /// <summary>Resolves a host name to its addresses.</summary>
    public delegate Task<IPAddress[]> Resolver(string host, CancellationToken cancellationToken);

    private readonly HttpClient _http;
    private readonly Resolver _resolve;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (McpClientMetadata Metadata, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);

    public McpClientMetadataFetcher() : this(null, null, null) { }

    /// <param name="transport">Tests only: a stand-in for the network. Production connects through <see cref="ConnectSafelyAsync"/>.</param>
    /// <param name="resolve">Tests only: a stand-in DNS.</param>
    public McpClientMetadataFetcher(HttpMessageHandler? transport, Resolver? resolve, TimeProvider? clock)
    {
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _clock = clock ?? TimeProvider.System;
        transport ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = ConnectSafelyAsync,
        };
        _http = new HttpClient(transport) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Nostos-MCP-OAuth/1");
    }

    public void Dispose() => _http.Dispose();

    public async Task<McpClientMetadataResult> FetchAsync(string clientId, CancellationToken cancellationToken)
    {
        if (ValidateClientId(clientId) is { } problem)
            return McpClientMetadataResult.Fail(problem);

        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(clientId, out var cached) && cached.Until > now)
            return new McpClientMetadataResult(cached.Metadata, null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        McpClientMetadataResult result;
        try
        {
            result = await FetchUncachedAsync(clientId, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = McpClientMetadataResult.Fail("The app's details took longer than 5 seconds to load.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException)
        {
            // SocketException: the name does not resolve (the pre-request
            // lookup calls DNS directly). IOException: the connection broke
            // while the body was read.
            result = McpClientMetadataResult.Fail("The app's details could not be loaded from its address.");
        }

        if (result.Metadata is { } metadata)
        {
            if (_cache.Count >= MaxCached)
                _cache.Clear();
            _cache[clientId] = (metadata, now + CacheFor);
        }

        return result;
    }

    /// <summary>Null when <paramref name="clientId"/> can be a metadata document URL, else why not.</summary>
    public static string? ValidateClientId(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > McpOAuthGrantModel.MaxClientIdLength)
            return "The app did not identify itself with a client id.";
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Only apps that publish their details at an https address can connect.";
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath == "/")
            return "The app's client id is not a valid metadata document address.";
        return null;
    }

    /// <summary>http://127.0.0.1, http://localhost or http://[::1] with any port.</summary>
    public static bool IsLoopbackRedirect(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttp
        && string.IsNullOrEmpty(parsed.UserInfo)
        && (parsed.Host is "127.0.0.1" or "[::1]" || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private async Task<McpClientMetadataResult> FetchUncachedAsync(string clientId, CancellationToken cancellationToken)
    {
        var url = new Uri(clientId);
        for (var hop = 0; ; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttps)
                return McpClientMetadataResult.Fail("The app's details are not served over https.");
            if (!await IsPublicHostAsync(url.IdnHost, cancellationToken))
                return McpClientMetadataResult.Fail("The app's address points into a private network.");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (hop >= MaxRedirects)
                    return McpClientMetadataResult.Fail("The app's address redirects too many times.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
                return McpClientMetadataResult.Fail($"The app's address answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
                return McpClientMetadataResult.Fail("The app's details are larger than 16 KiB.");

            var body = await ReadLimitedAsync(response.Content, cancellationToken);
            return body is null
                ? McpClientMetadataResult.Fail("The app's details are larger than 16 KiB.")
                : Parse(clientId, body);
        }
    }

    private async Task<bool> IsPublicHostAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await _resolve(host, cancellationToken);
        return addresses.Length > 0 && addresses.All(McpNetworkAddressFilter.IsPublic);
    }

    private async ValueTask<Stream> ConnectSafelyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await _resolve(host, cancellationToken);
        if (addresses.Length == 0 || !addresses.All(McpNetworkAddressFilter.IsPublic))
            throw new HttpRequestException("Refused to connect to a non-public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxDocumentBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
                break;
            total += read;
        }

        return total > MaxDocumentBytes ? null : buffer[..total];
    }

    /// <summary>Checks a document's contents against the URL it came from.</summary>
    public static McpClientMetadataResult Parse(string clientId, byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return McpClientMetadataResult.Fail("The app's details are not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return McpClientMetadataResult.Fail("The app's details are not a JSON object.");

            if (!root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), clientId, StringComparison.Ordinal))
                return McpClientMetadataResult.Fail("The app's details name a different client id than the address they came from.");

            if (root.TryGetProperty("token_endpoint_auth_method", out var auth)
                && (auth.ValueKind != JsonValueKind.String || auth.GetString() != "none"))
                return McpClientMetadataResult.Fail("Nostos only connects apps that sign in without a client secret (token_endpoint_auth_method none).");

            if (!root.TryGetProperty("client_name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameElement.GetString()))
                return McpClientMetadataResult.Fail("The app's details do not give its name.");
            var name = nameElement.GetString()!.Trim();
            if (name.Length > McpOAuthGrantModel.MaxClientNameLength)
                name = name[..McpOAuthGrantModel.MaxClientNameLength];

            if (!root.TryGetProperty("redirect_uris", out var urisElement) || urisElement.ValueKind != JsonValueKind.Array)
                return McpClientMetadataResult.Fail("The app's details list no redirect URIs.");
            var uris = new List<string>();
            foreach (var item in urisElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !IsAcceptableRedirect(item.GetString()!))
                    return McpClientMetadataResult.Fail("The app's details list a redirect URI that is neither https nor this computer.");
                uris.Add(item.GetString()!);
            }

            if (uris.Count is 0 or > MaxRedirectUris)
                return McpClientMetadataResult.Fail("The app's details list no redirect URIs, or too many.");

            return new McpClientMetadataResult(new McpClientMetadata(clientId, name, uris), null);
        }
    }

    private static bool IsAcceptableRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.UserInfo))
            return false;
        return parsed.Scheme == Uri.UriSchemeHttps || IsLoopbackRedirect(uri);
    }
}

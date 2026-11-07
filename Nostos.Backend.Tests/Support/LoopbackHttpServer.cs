using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// A raw-socket HTTP/1.1 server on 127.0.0.1 for provider failure-path tests.
/// Unlike a stubbed <see cref="HttpMessageHandler"/>, a real
/// <see cref="HttpClient"/> talks to it over TCP, so timeouts, refused
/// connections, stalled bodies and dropped connections behave as they do
/// against a misconfigured or failing AI endpoint.
/// </summary>
public sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly Func<LoopbackRequest, NetworkStream, CancellationToken, Task> _respond;
    private int _requests;

    private LoopbackHttpServer(Func<LoopbackRequest, NetworkStream, CancellationToken, Task> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The OpenAI-style base URL, e.g. http://127.0.0.1:1234/v1.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>The last request line and body the server read.</summary>
    public LoopbackRequest? LastRequest { get; private set; }

    public static LoopbackHttpServer Start(
        Func<LoopbackRequest, NetworkStream, CancellationToken, Task> respond) => new(respond);

    /// <summary>Answers every request with one complete response.</summary>
    public static LoopbackHttpServer Responding(int status, string body, string contentType = "application/json") =>
        new((_, stream, ct) => WriteResponseAsync(stream, status, body, contentType, ct));

    /// <summary>A port with nothing listening on it: connections are refused.</summary>
    public static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static async Task WriteResponseAsync(
        NetworkStream stream, int status, string body, string contentType, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                   $"Content-Type: {contentType}\r\n" +
                   $"Content-Length: {bytes.Length}\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Writes a 200 head that promises <paramref name="length"/> bytes, then only <paramref name="partial"/>.</summary>
    public static async Task WritePartialAsync(
        NetworkStream stream, int length, string partial, CancellationToken ct)
    {
        var head = "HTTP/1.1 200 OK\r\n" +
                   "Content-Type: application/json\r\n" +
                   $"Content-Length: {length}\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head + partial), ct);
        await stream.FlushAsync(ct);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, _stop.Token);
                Interlocked.Increment(ref _requests);
                LastRequest = request;
                await _respond(request, stream, _stop.Token);
            }
            catch
            {
                // A test that drops or stalls the connection ends here.
            }
        }
    }

    private static async Task<LoopbackRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) break;
            buffer.Add(one[0]);
            var count = buffer.Count;
            if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n'
                && buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
                break;
        }

        var head = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = head.Split("\r\n");
        var length = lines
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();
        var authorization = lines
            .FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            ?["Authorization:".Length..].Trim();
        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), ct);
            if (read == 0) break;
            offset += read;
        }

        return new LoopbackRequest(lines[0], authorization, Encoding.UTF8.GetString(body, 0, offset));
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch
        {
            // Stopping the listener ends the accept loop.
        }

        _stop.Dispose();
    }
}

/// <summary>What <see cref="LoopbackHttpServer"/> read from one request.</summary>
public sealed record LoopbackRequest(string RequestLine, string? Authorization, string Body);

/// <summary>Real <see cref="HttpClient"/>s with a fixed timeout, as Program.cs registers them.</summary>
public sealed class TimeoutHttpClientFactory(TimeSpan timeout) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new() { Timeout = timeout };
}

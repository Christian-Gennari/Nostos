using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Nostos.Backend.Services;

namespace Nostos.Product.Alignment;

/// <summary>
/// A short-lived, loopback-only HTTP byte-range resource for ffmpeg.
/// The caller supplies an already scoped storage opener. This component
/// neither resolves identities nor knows anything about tenants.
/// </summary>
public sealed class LoopbackRangeServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16384;
    private const int MaxConnections = 8;

    private readonly TcpListener listener;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim slots = new(MaxConnections);
    private readonly Func<StorageByteRange, CancellationToken,
        Task<StoredAssetRead?>> open;
    private readonly long length;
    private readonly string resourcePath;
    private readonly Task acceptLoop;
    private readonly object sync = new();
    private readonly HashSet<Task> connections = [];

    private LoopbackRangeServer(
        TcpListener listener,
        long length,
        Func<StorageByteRange, CancellationToken,
            Task<StoredAssetRead?>> open)
    {
        this.listener = listener;
        this.length = length;
        this.open = open;

        resourcePath = "/" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant();

        acceptLoop = AcceptAsync();
    }

    public Uri Url { get; private set; } = null!;

    public static Task<LoopbackRangeServer> StartAsync(
        long length,
        Func<StorageByteRange, CancellationToken,
            Task<StoredAssetRead?>> open,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentNullException.ThrowIfNull(open);
        ct.ThrowIfCancellationRequested();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(MaxConnections);

        try
        {
            var server = new LoopbackRangeServer(listener, length, open);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            server.Url = new Uri(
                $"http://127.0.0.1:{port}{server.resourcePath}");

            return Task.FromResult(server);
        }
        catch
        {
            listener.Stop();
            throw;
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(
                    shutdown.Token);

                try
                {
                    await slots.WaitAsync(shutdown.Token);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                var task = HandleAsync(client);

                lock (sync)
                    connections.Add(task);

                _ = task.ContinueWith(completed =>
                {
                    lock (sync)
                        connections.Remove(completed);

                    // Observe unexpected faults from detached handlers.
                    _ = completed.Exception;
                }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
            when (shutdown.IsCancellationRequested)
        {
        }
        catch (SocketException)
            when (shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
            when (shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();

                // A stalled or malicious local client must not retain a
                // connection slot for the lifetime of the ffmpeg run.
                using var requestDeadline =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        shutdown.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(30));

                var ct = requestDeadline.Token;

                var header = await ReadHeaderAsync(stream, ct);
                if (header is null)
                {
                    await WriteStatusAsync(stream, 400, ct);
                    return;
                }

                var lines = header.Split("\r\n");
                var request = lines[0].Split(' ', 3);

                if (request.Length != 3 ||
                    request[2] != "HTTP/1.1" &&
                    request[2] != "HTTP/1.0")
                {
                    await WriteStatusAsync(stream, 400, ct);
                    return;
                }

                if (request[1] != resourcePath)
                {
                    await WriteStatusAsync(stream, 404, ct);
                    return;
                }

                if (request[0] is not ("GET" or "HEAD"))
                {
                    await WriteStatusAsync(stream, 405, ct);
                    return;
                }

                string? rangeHeader = null;
                var rangeCount = 0;

                foreach (var line in lines.Skip(1))
                {
                    if (line.Length == 0)
                        break;

                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        await WriteStatusAsync(stream, 400, ct);
                        return;
                    }

                    if (line[..colon].Equals(
                        "Range", StringComparison.OrdinalIgnoreCase))
                    {
                        rangeCount++;
                        rangeHeader = line[(colon + 1)..].Trim();
                    }
                }

                if (rangeCount > 1 ||
                    !TryParseRange(rangeHeader, length, out var range))
                {
                    await WriteHeadersAsync(
                        stream,
                        416,
                        0,
                        $"Content-Range: bytes */{length}\r\n",
                        ct);
                    return;
                }

                var partial = rangeHeader is not null;
                var contentLength = range.Length;

                var extra = partial
                    ? $"Content-Range: bytes {range.Start}-{range.EndInclusive}/{length}\r\n"
                    : "";

                if (request[0] == "HEAD")
                {
                    await WriteHeadersAsync(
                        stream,
                        partial ? 206 : 200,
                        contentLength,
                        extra,
                        ct);
                    return;
                }

                await using var asset = await open(range, ct);

                if (asset is null)
                {
                    await WriteStatusAsync(stream, 404, ct);
                    return;
                }

                // Do not turn a provider's incorrect range response into
                // valid-looking HTTP bytes from the wrong offset.
                if (asset.Info.Length != length ||
                    asset.Range is not { } delivered ||
                    delivered != range)
                {
                    await WriteStatusAsync(stream, 502, ct);
                    return;
                }

                await WriteHeadersAsync(
                    stream,
                    partial ? 206 : 200,
                    contentLength,
                    extra,
                    ct);

                var remaining = contentLength;
                var buffer = new byte[64 * 1024];

                while (remaining > 0)
                {
                    var read = await asset.Content.ReadAsync(
                        buffer.AsMemory(
                            0,
                            (int)Math.Min(buffer.Length, remaining)),
                        ct);

                    if (read == 0)
                        throw new EndOfStreamException(
                            "Storage returned a truncated byte range.");

                    await stream.WriteAsync(
                        buffer.AsMemory(0, read), ct);

                    remaining -= read;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // Client disconnects and truncated storage reads terminate
                // only this request. ffmpeg observes the failed transfer.
            }
            catch (SocketException)
            {
            }
            finally
            {
                slots.Release();
            }
        }
    }

    private static async Task<string?> ReadHeaderAsync(
        Stream stream,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];

        while (buffer.Length < MaxHeaderBytes)
        {
            if (await stream.ReadAsync(single, ct) == 0)
                return null;

            buffer.WriteByte(single[0]);

            if (buffer.Length >= 4)
            {
                var data = buffer.GetBuffer();
                var end = (int)buffer.Length;

                if (data[end - 4] == '\r' &&
                    data[end - 3] == '\n' &&
                    data[end - 2] == '\r' &&
                    data[end - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(
                        data, 0, end);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Parses one RFC 7233 byte range. Multiple ranges are deliberately
    /// unsupported. An absent header selects the complete resource.
    /// </summary>
    public static bool TryParseRange(
        string? header,
        long length,
        out StorageByteRange range)
    {
        range = default;

        if (length <= 0)
            return false;

        if (header is null)
        {
            range = new StorageByteRange(0, length - 1);
            return true;
        }

        if (!header.StartsWith(
            "bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var specification = header[6..];

        if (specification.Contains(','))
            return false;

        var dash = specification.IndexOf('-');

        if (dash < 0 ||
            specification.IndexOf('-', dash + 1) >= 0)
            return false;

        var first = specification[..dash];
        var last = specification[(dash + 1)..];

        if (first.Length == 0)
        {
            if (!long.TryParse(
                    last,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var suffix) ||
                suffix <= 0)
                return false;

            range = new StorageByteRange(
                Math.Max(0, length - suffix),
                length - 1);
            return true;
        }

        if (!long.TryParse(
                first,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var start) ||
            start >= length)
            return false;

        long end;

        if (last.Length == 0)
        {
            end = length - 1;
        }
        else if (!long.TryParse(
                     last,
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out end) ||
                 end < start)
        {
            return false;
        }

        range = new StorageByteRange(
            start, Math.Min(end, length - 1));

        return true;
    }

    private static Task WriteStatusAsync(
        Stream stream,
        int status,
        CancellationToken ct) =>
        WriteHeadersAsync(stream, status, 0, "", ct);

    private static Task WriteHeadersAsync(
        Stream stream,
        int status,
        long contentLength,
        string extra,
        CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK",
            206 => "Partial Content",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            416 => "Range Not Satisfiable",
            502 => "Bad Gateway",
            _ => "Error",
        };

        var response =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Length: {contentLength}\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Accept-Ranges: bytes\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n" +
            extra +
            "\r\n";

        return stream.WriteAsync(
            Encoding.ASCII.GetBytes(response),
            ct).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (!shutdown.IsCancellationRequested)
        {
            shutdown.Cancel();
            listener.Stop();
        }

        try
        {
            await acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }

        Task[] pending;

        lock (sync)
            pending = [.. connections];

        try
        {
            await Task.WhenAll(pending)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
        catch (IOException)
        {
        }

        // The shutdown token remains alive for any storage handlers that
        // have not finished; all newly accepted work has stopped.
        // Do not dispose synchronization objects while handlers use them.
    }
}

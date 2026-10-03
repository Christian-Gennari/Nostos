using System.Buffers;
using System.Net;
using Microsoft.Extensions.Options;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// Streams a provider asset to a file, enforcing the provider's policy.
///
/// Streams rather than buffers: an audiobook part is tens of megabytes and the
/// assembled result hundreds, so nothing here ever holds a payload in memory.
/// Cover artwork is the one exception and has its own bounded entry point.
/// </summary>
public interface IProviderContentDownloader
{
    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>.
    ///
    /// <paramref name="totalBudgetBytes"/> is how much of an acquisition's total
    /// allowance is still unspent; the per-part cap and that budget are both
    /// enforced while streaming, so a lying <c>Content-Length</c> cannot exceed
    /// either. Returns the number of bytes written.
    /// </summary>
    Task<long> DownloadAsync(
        Uri url,
        string destinationPath,
        IProviderDownloadPolicy policy,
        long totalBudgetBytes,
        IProgress<long>? progress,
        CancellationToken ct);

    /// <summary>
    /// Fetches a small resource into memory — cover artwork, essentially. The
    /// same host and scheme policy applies; <paramref name="maxBytes"/> is
    /// enforced while reading, so a source cannot answer a cover request with
    /// something enormous.
    /// </summary>
    Task<byte[]> DownloadBytesAsync(Uri url, IProviderDownloadPolicy policy, long maxBytes, CancellationToken ct);
}

/// <summary>
/// A failed download. <see cref="Retryable"/> distinguishes "the source is
/// briefly unhappy" (429/503/timeouts) from "this will never work" (404, a host
/// outside the policy, an oversized payload) so retries are not wasted on the
/// latter.
/// </summary>
public sealed class ProviderDownloadException(string code, string message, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;

    public const string HostNotAllowed = "download_host_not_allowed";
    public const string InsecureScheme = "download_insecure_scheme";
    public const string NotFound = "download_not_found";
    public const string TooLarge = "download_too_large";
    public const string Empty = "download_empty";
    public const string Failed = "download_failed";
    public const string RedirectLimit = "download_redirect_limit";
}

public sealed class ProviderContentDownloader(
    IHttpClientFactory httpClientFactory,
    IOptions<AcquisitionOptions> options,
    ILogger<ProviderContentDownloader> logger) : IProviderContentDownloader
{
    /// <summary>
    /// Named client for provider content. Registered with auto-redirect OFF:
    /// redirects are followed here instead, one hop at a time, so every hop is
    /// checked against the provider's host policy. Letting HttpClient follow
    /// them would mean the policy only ever saw the first URL.
    /// </summary>
    public const string HttpClientName = "provider-content";

    private const int BufferSize = 131072;
    private readonly AcquisitionOptions _options = options.Value;

    public Task<long> DownloadAsync(
        Uri url,
        string destinationPath,
        IProviderDownloadPolicy policy,
        long totalBudgetBytes,
        IProgress<long>? progress,
        CancellationToken ct) =>
        WithRetriesAsync(
            url,
            async token =>
            {
                using var response = await OpenAsync(url, policy, token);
                return await StreamToFileAsync(response, destinationPath, policy, totalBudgetBytes, progress, token);
            },
            ct);

    public Task<byte[]> DownloadBytesAsync(
        Uri url,
        IProviderDownloadPolicy policy,
        long maxBytes,
        CancellationToken ct) =>
        WithRetriesAsync(
            url,
            async token =>
            {
                using var response = await OpenAsync(url, policy, token);

                var declared = response.Content.Headers.ContentLength;
                if (declared is > 0 && declared.Value > maxBytes)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.TooLarge,
                        $"The source offers {declared.Value} bytes, above the {maxBytes} byte limit.");

                await using var source = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();

                var chunk = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(chunk.AsMemory(0, BufferSize), token)) > 0)
                    {
                        if (buffer.Length + read > maxBytes)
                            throw new ProviderDownloadException(
                                ProviderDownloadException.TooLarge,
                                $"The source sent more than the {maxBytes} byte limit.");

                        buffer.Write(chunk, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(chunk);
                }

                if (buffer.Length == 0)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.Empty, "The source returned an empty response.", retryable: true);

                return buffer.ToArray();
            },
            ct);

    private async Task<T> WithRetriesAsync<T>(Uri url, Func<CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        var attempts = _options.ClampDownloadAttempts();
        var delay = TimeSpan.FromSeconds(2);

        for (var number = 1; ; number++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds));

            try
            {
                return await attempt(deadline.Token);
            }
            catch (ProviderDownloadException ex) when (ex.Retryable && number < attempts)
            {
                logger.LogWarning(
                    "Download attempt {Attempt}/{Attempts} for {Url} failed ({Code}); retrying in {Delay}s",
                    number, attempts, url, ex.Code, delay.TotalSeconds);

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    /// <summary>
    /// Opens the URL, following redirects one hop at a time. Every hop —
    /// including the ones the source itself hands back — is re-validated before
    /// it is used, so a redirect cannot walk out of the allowed hosts.
    /// </summary>
    private async Task<HttpResponseMessage> OpenAsync(Uri url, IProviderDownloadPolicy policy, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var current = url;

        for (var hop = 0; ; hop++)
        {
            EnsureAllowed(current, policy);

            var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch
            {
                request.Dispose();
                throw;
            }

            request.Dispose();

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();

                if (hop >= _options.MaxRedirects)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.RedirectLimit,
                        $"More than {_options.MaxRedirects} redirects while fetching {url}.");

                if (location is null)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.Failed, $"A redirect from {current} had no Location.");

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new ProviderDownloadException(
                    ProviderDownloadException.NotFound, $"The source has no file at {current} (HTTP {status}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                var statusCode = (int)status;
                response.Dispose();

                var retryable = status is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.RequestTimeout
                    || statusCode >= 500;

                throw new ProviderDownloadException(
                    ProviderDownloadException.Failed,
                    $"The source answered HTTP {statusCode} for {current}.",
                    retryable);
            }

            return response;
        }
    }

    private static async Task<long> StreamToFileAsync(
        HttpResponseMessage response,
        string destinationPath,
        IProviderDownloadPolicy policy,
        long totalBudgetBytes,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        var cap = Math.Min(policy.MaxBytesPerPart, totalBudgetBytes);
        var declaredLength = response.Content.Headers.ContentLength;

        // Fail before transferring anything when the source already admits the
        // payload is too big.
        if (declaredLength is > 0 && declaredLength.Value > cap)
            throw new ProviderDownloadException(
                ProviderDownloadException.TooLarge,
                $"The source offers {declaredLength.Value} bytes, above the {cap} byte limit.");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            long written = 0;
            int read;

            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                written += read;

                // Both caps are checked while streaming, because
                // Content-Length is the source's claim rather than a fact.
                if (written > policy.MaxBytesPerPart)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.TooLarge,
                        $"The source sent more than the {policy.MaxBytesPerPart} byte per-part limit.");

                if (written > totalBudgetBytes)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.TooLarge,
                        $"This acquisition has passed its {policy.MaxTotalBytes} byte total limit.");

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report(written);
            }

            await destination.FlushAsync(ct);

            if (written == 0)
                throw new ProviderDownloadException(
                    ProviderDownloadException.Empty, "The source returned an empty file.", retryable: true);

            return written;
        }
        catch
        {
            // Never leave a truncated part behind: a later step would happily
            // treat it as a complete track.
            TryDelete(destinationPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureAllowed(Uri url, IProviderDownloadPolicy policy)
    {
        if (!ProviderHostPolicy.IsAllowedScheme(url))
            throw new ProviderDownloadException(
                ProviderDownloadException.InsecureScheme,
                $"Refusing to download over '{url.Scheme}': only https is accepted.");

        if (!ProviderHostPolicy.IsAllowed(url.Host, policy.AllowedHosts))
            throw new ProviderDownloadException(
                ProviderDownloadException.HostNotAllowed,
                $"Refusing to download from '{url.Host}': not an allowed host for this provider.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: the whole staging directory is removed on failure.
        }
    }
}

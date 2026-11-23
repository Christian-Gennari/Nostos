using System.Buffers;
using System.Net;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Services;

/// <summary>
/// What fetching a cover URL produced: the bytes and the extension storage
/// should use, or why it could not be used.
/// </summary>
/// <param name="Bytes">The image, ready to store; null when it could not be fetched.</param>
/// <param name="FileExtension">The stored extension the decoded format needs; null on failure.</param>
/// <param name="Failure">A short, user-facing reason; null on success.</param>
public sealed record CoverDownloadResult(byte[]? Bytes, string? FileExtension, string? Failure)
{
    public bool Succeeded => Bytes is not null;

    public static CoverDownloadResult Failed(string failure) => new(null, null, failure);
}

/// <summary>
/// The one way a cover is fetched from a URL, in its two shapes: a provider's
/// own cover (acquisition) and a URL the user's app supplied (the MCP book
/// tools).
///
/// Both are the acquisition path on purpose: https only, bounded while
/// reading, and decoded through <see cref="CoverImageProcessor"/> so an HTML
/// error page is never stored as a cover. A failure is a reason, never an
/// exception: cover art must not cost the user the book.
///
/// The user-supplied shape has a narrower boundary than a provider, because
/// the host it names is whatever the caller wrote: the address is checked
/// before any request, redirects stay on the host the URL named, and the
/// connection itself refuses a non-public address (see
/// <see cref="PublicAddressPolicy"/>), which is what closes DNS rebinding.
/// </summary>
public static class CoverDownload
{
    /// <summary>Cover artwork is small enough to hold in memory, unlike a book file.</summary>
    public const long MaxCoverBytes = 12L * 1024 * 1024;

    /// <summary>
    /// Named client for a user-supplied cover URL. Registered with automatic
    /// redirects off — each hop is followed and checked here — and with a
    /// connect callback (<see cref="PublicAddressPolicy.ConnectAsync"/>) that
    /// resolves the host itself and connects only to a public address.
    /// </summary>
    public const string HttpClientName = "cover-url";

    private const int BufferSize = 131072;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan UserUrlTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fetches a cover from a provider's URL, through the provider's policy and
    /// its acquisition client. Unchanged by design: a provider's hosts are
    /// allowlisted, so it is not the user-supplied path below.
    /// </summary>
    public static async Task<CoverDownloadResult> TryAsync(
        IProviderContentDownloader downloader,
        Uri url,
        IProviderDownloadPolicy policy,
        ILogger logger,
        CancellationToken ct)
    {
        if (!ProviderHostPolicy.IsAllowedScheme(url))
            return CoverDownloadResult.Failed("the URL must start with https");

        return await AttemptAsync(
            () => downloader.DownloadBytesAsync(url, policy, MaxCoverBytes, ct),
            url,
            logger,
            ct);
    }

    /// <summary>
    /// Fetches a cover from a URL the user supplied. Refuses an address that is
    /// not public before making any request, and fetches through the guarded
    /// client so the connected address is vetted too.
    /// </summary>
    public static async Task<CoverDownloadResult> TryUserUrlAsync(
        IHttpClientFactory httpClientFactory,
        Uri url,
        ILogger logger,
        CancellationToken ct)
    {
        if (!ProviderHostPolicy.IsAllowedScheme(url))
            return CoverDownloadResult.Failed("the URL must start with https");

        if (!await PublicAddressPolicy.IsSafeToFetchAsync(url.DnsSafeHost, ct))
        {
            logger.LogWarning(
                "Cover art at {Url} was refused before it was fetched: {Reason}.",
                url,
                PublicAddressPolicy.RefusalReason);
            return CoverDownloadResult.Failed(PublicAddressPolicy.RefusalReason);
        }

        return await AttemptAsync(
            () => FetchUserUrlAsync(httpClientFactory, url, ct),
            url,
            logger,
            ct);
    }

    private static async Task<CoverDownloadResult> AttemptAsync(
        Func<Task<byte[]>> fetch,
        Uri url,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var bytes = await fetch();

            using var probe = new MemoryStream(bytes);
            var info = await CoverImageProcessor.InspectAsync(probe, ct);
            if (info is null)
            {
                logger.LogWarning("Cover art at {Url} was not a recognisable image; skipping it.", url);
                return CoverDownloadResult.Failed("the URL did not return a PNG or JPEG image");
            }

            // Storage accepts PNG and JPEG only (BookAssetFormats.CoverExtensions).
            // A WebP or GIF decodes here but could not be stored, so it is
            // refused now rather than failing after the download.
            var extension = info.Format switch
            {
                "png" => ".png",
                "jpeg" => ".jpg",
                _ => null,
            };
            if (extension is null)
            {
                logger.LogWarning("Cover art at {Url} is a {Format}, which cannot be stored; skipping it.", url, info.Format);
                return CoverDownloadResult.Failed($"the image is {info.Format.ToUpperInvariant()}; a cover must be a PNG or JPEG");
            }

            return new CoverDownloadResult(bytes, extension, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Could not fetch cover art at {Url}; continuing without it. Exception type {ExceptionType}; details suppressed.",
                url,
                ex.GetType().Name);
            return CoverDownloadResult.Failed(Reason(ex));
        }
    }

    /// <summary>
    /// The user path's fetch. It repeats the provider downloader's rules — one
    /// https file, one bounded read, redirects followed here so each hop can be
    /// checked against the host the URL named — on the client whose connections
    /// are address-checked.
    /// </summary>
    private static async Task<byte[]> FetchUserUrlAsync(
        IHttpClientFactory httpClientFactory,
        Uri url,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var policy = new CoverUrlDownloadPolicy(url.Host);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(UserUrlTimeout);

        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                EnsureAllowed(current, policy);

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await SendAsync(client, request, deadline.Token);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;

                    if (hop >= MaxRedirects)
                        throw new ProviderDownloadException(
                            ProviderDownloadException.RedirectLimit,
                            $"More than {MaxRedirects} redirects while fetching {url}.");

                    if (location is null)
                        throw new ProviderDownloadException(
                            ProviderDownloadException.Failed, $"A redirect from {current} had no Location.");

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.NotFound, $"The source has no image at {current}.");

                if (!response.IsSuccessStatusCode)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.Failed,
                        $"The source answered HTTP {(int)response.StatusCode} for {current}.");

                return await ReadBoundedAsync(response, current, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ProviderDownloadException(
                ProviderDownloadException.Timeout,
                $"The cover did not finish downloading within {UserUrlTimeout.TotalSeconds:0} seconds.",
                retryable: true);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ProviderDownloadException(
                ProviderDownloadException.Failed,
                $"the cover could not be fetched from {request.RequestUri?.Host}: {ex.Message}");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        Uri url,
        CancellationToken ct)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared is > 0 && declared.Value > MaxCoverBytes)
            throw new ProviderDownloadException(
                ProviderDownloadException.TooLarge,
                $"The image at {url} offers {declared.Value} bytes, above the {MaxCoverBytes} byte limit.");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(chunk.AsMemory(0, BufferSize), ct)) > 0)
            {
                if (buffer.Length + read > MaxCoverBytes)
                    throw new ProviderDownloadException(
                        ProviderDownloadException.TooLarge,
                        $"The image at {url} is larger than the {MaxCoverBytes} byte limit.");

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (buffer.Length == 0)
            throw new ProviderDownloadException(
                ProviderDownloadException.Empty, "The source returned an empty response.");

        return buffer.ToArray();
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
                $"Refusing to download from '{url.Host}': not the host the cover URL named.");
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static string Reason(Exception ex) => ex switch
    {
        ProviderDownloadException { Code: ProviderDownloadException.InsecureScheme } =>
            "the URL must start with https",
        ProviderDownloadException { Code: ProviderDownloadException.TooLarge } =>
            $"the image is larger than {MaxCoverBytes / (1024 * 1024)} MB",
        ProviderDownloadException { Code: ProviderDownloadException.NotFound } =>
            "the URL returned no image",
        ProviderDownloadException { Code: ProviderDownloadException.Empty } =>
            "the URL returned an empty response",
        ProviderDownloadException { Code: ProviderDownloadException.HostNotAllowed } =>
            "the URL redirected to a host that is not allowed",
        ProviderDownloadException { Code: ProviderDownloadException.RedirectLimit } =>
            "the URL redirected too many times",
        ProviderDownloadException { Code: ProviderDownloadException.Timeout } =>
            "the download timed out",
        ProviderDownloadException download => download.Message.TrimEnd('.'),
        _ => "the cover could not be downloaded",
    };
}

/// <summary>
/// A download policy for one user-supplied cover URL: only the host it named
/// (and its subdomains), and only one small file. A redirect to another host is
/// refused, so the URL cannot turn a cover into an open proxy.
/// </summary>
public sealed class CoverUrlDownloadPolicy(string host) : IProviderDownloadPolicy
{
    public IReadOnlyList<string> AllowedHosts { get; } = [host];
    public long MaxBytesPerPart => CoverDownload.MaxCoverBytes;
    public long MaxTotalBytes => CoverDownload.MaxCoverBytes;
    public int MaxParts => 1;
}

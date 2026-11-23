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
/// The one way a cover is fetched from a URL, shared by acquisition (a
/// provider's cover) and the MCP book tools (a URL the user's app supplied).
///
/// It is the acquisition path on purpose: https only, bounded while reading,
/// and decoded through <see cref="CoverImageProcessor"/> so an HTML error page
/// is never stored as a cover. A failure is a reason, never an exception: cover
/// art must not cost the user the book.
/// </summary>
public static class CoverDownload
{
    /// <summary>Cover artwork is small enough to hold in memory, unlike a book file.</summary>
    public const long MaxCoverBytes = 12L * 1024 * 1024;

    public static async Task<CoverDownloadResult> TryAsync(
        IProviderContentDownloader downloader,
        Uri url,
        IProviderDownloadPolicy policy,
        ILogger logger,
        CancellationToken ct)
    {
        if (!ProviderHostPolicy.IsAllowedScheme(url))
            return CoverDownloadResult.Failed("the URL must start with https");

        try
        {
            var bytes = await downloader.DownloadBytesAsync(url, policy, MaxCoverBytes, ct);

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

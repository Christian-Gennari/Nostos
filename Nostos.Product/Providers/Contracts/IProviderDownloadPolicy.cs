namespace Nostos.Backend.Providers.Contracts;

/// <summary>
/// Where a provider's content may be downloaded from, and the resource bounds
/// applied while fetching it.
///
/// The downloader treats every URL as untrusted input even though providers
/// produce them server-side: a bug in a provider, or a redirect the source
/// hands back, must not be able to turn acquisition into an open proxy.
/// </summary>
public interface IProviderDownloadPolicy
{
    /// <summary>
    /// Allowed hosts. An entry matches a host either exactly or as a subdomain
    /// suffix — entry <c>archive.org</c> allows <c>ia801600.us.archive.org</c>,
    /// which is where archive.org redirects its own download URLs. Every hop of
    /// a redirect chain is re-checked against this list.
    /// </summary>
    IReadOnlyList<string> AllowedHosts { get; }

    /// <summary>Hard cap for a single downloaded part.</summary>
    long MaxBytesPerPart { get; }

    /// <summary>Hard cap for all parts of one acquisition.</summary>
    long MaxTotalBytes { get; }

    /// <summary>Hard cap on the number of parts one acquisition may fetch.</summary>
    int MaxParts { get; }
}

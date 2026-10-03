namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// Decides whether a download host is acceptable.
///
/// Split out from the downloader so the rule can be tested without an HTTP
/// server, because it is the load-bearing part of the SSRF boundary: everything
/// else about a download is a performance question, but this is the part that
/// stops Nostos being used as a proxy for arbitrary URLs.
///
/// Matching is deliberately hierarchical rather than a single-level wildcard.
/// archive.org serves its downloads from multi-level storage hosts — a request
/// for <c>www.archive.org/download/...</c> redirects through <c>archive.org</c>
/// to <c>ia801600.us.archive.org</c> — so a wildcard that only matched one label
/// would reject the very host the redirect lands on.
/// </summary>
public static class ProviderHostPolicy
{
    public static bool IsAllowed(string? host, IReadOnlyList<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        foreach (var entry in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            // Tolerate a leading dot in configuration ("*.archive.org" /
            // ".archive.org" both mean the same thing here).
            var suffix = entry.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
            if (suffix.Length == 0)
                continue;

            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                return true;

            if (host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Only https is accepted. A plaintext download could be rewritten in
    /// transit, and an imported library file is exactly the kind of payload an
    /// attacker would want to substitute.
    /// </summary>
    public static bool IsAllowedScheme(Uri url) => url.Scheme == Uri.UriSchemeHttps;
}

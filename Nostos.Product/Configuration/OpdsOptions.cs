namespace Nostos.Backend.Configuration;

// OPDS 1.2 export configuration (issue #186).
//
// Access policy is supplied by the host. SelfHosted exposes this on its trusted
// network; other hosts can apply their own authentication policy when mapping
// product endpoints. Operators can set Enabled=false when the route should be
// unavailable.
//
public sealed class OpdsOptions
{
    // Section name in appsettings/environment configuration.
    public const string SectionName = "Opds";

    // Entries per acquisition feed page when PageSize is not configured.
    public const int DefaultPageSize = 50;

    // Upper bound for PageSize, so a mis-set value cannot ask the server to
    // materialize an unbounded page in one request.
    public const int MaxPageSize = 500;

    // Whether /opds/ is mapped at all. Enabled by default so existing OPDS
    // readers keep working across an upgrade; setting `Opds:Enabled=false`
    // removes the endpoint entirely (no route, so requests fall through to the
    // ordinary 404 rather than an empty feed).
    public bool Enabled { get; set; } = true;

    // Entries per page of the acquisition feed. Pagination is part of the
    // catalogue contract, not an optional extra: the feed used to serialize the
    // whole file-backed library in one document. Values <= 0 fall back to
    // DefaultPageSize and values above MaxPageSize are clamped.
    public int PageSize { get; set; } = DefaultPageSize;

    // Optional externally visible origin (scheme + host, no trailing slash)
    // used to build the absolute URLs in the feed, e.g.
    // "https://nostos.example.ts.net:5215".
    //
    // Unset is the normal case: the feed then derives scheme and host from the
    // incoming request, which is correct behind a reverse proxy once
    // X-Forwarded-Proto / X-Forwarded-Host are honoured (see Program.cs). Set
    // this when the request itself cannot reveal the externally visible origin
    // — a proxy that forwards neither header, for instance.
    public string? PublicBaseUrl { get; set; }

    // Entries per page, clamped into the supported range.
    public static int NormalizePageSize(int value) =>
        value <= 0 ? DefaultPageSize : Math.Min(value, MaxPageSize);

    // Validates that `value` is an absolute http(s) origin and normalizes it by
    // trimming the trailing slash. Null or blank input is a valid "not
    // configured" and yields a null `normalized`.
    public static bool TryNormalizePublicBaseUrl(
        string? value,
        out string? normalized,
        out string error
    )
    {
        normalized = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim().TrimEnd('/');

        if (
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            error =
                $"the URL '{value}' must be an absolute http(s) origin, e.g. 'https://nostos.example.ts.net:5215'";
            return false;
        }

        normalized = trimmed;
        return true;
    }
}

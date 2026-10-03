namespace Nostos.Backend.Providers.Discovery;

/// <summary>Stable source-level error codes emitted by aggregate discovery.</summary>
public static class ProviderDiscoveryErrorCodes
{
    public const string Timeout = "provider_timeout";
    public const string SearchFailed = "provider_search_failed";
}

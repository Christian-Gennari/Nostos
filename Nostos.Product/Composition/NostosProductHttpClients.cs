using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Providers.LibriVox;
using Nostos.Backend.Providers.Litteraturbanken;
using Nostos.Backend.Providers.StandardEbooks;
using Nostos.Backend.Providers.Wikisource;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Notes.Imports;

namespace Nostos.Product.Composition;

/// <summary>
/// The one place every named client gets its base address, timeout and default
/// headers. Content providers resolve a client from
/// <see cref="IHttpClientFactory"/> for each operation instead of holding one in
/// a singleton, so the factory's handler lifetime rotates pooled connections and
/// a long-running host never pins a DNS entry. Provider-side header application
/// (see <see cref="ProviderHttp.Client"/>) reuses the same identity constants,
/// so the two cannot drift.
/// </summary>
internal static class NostosProductHttpClients
{
    internal static IServiceCollection AddNostosProductHttpClients(
        this IServiceCollection services)
    {
        services.AddHttpClient();

        services.AddHttpClient(ReadwiseApiClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttp.ProductUserAgent);
        });

        services.AddHttpClient(BookLookupService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttp.ProductUserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        });

        services.AddHttpClient(ProviderContentDownloader.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ProviderHttp.ProductUserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });

        services.AddHttpClient(GutenbergProvider.HttpClientName, client =>
            ConfigureProviderClient(
                client,
                GutenbergCatalog.BaseUrl,
                TimeSpan.FromSeconds(20),
                ProviderHttp.ProductUserAgent));
        // The daily RDF snapshot is a 177 MB archive, so it gets its own client:
        // the 20 s search timeout must never apply to a bulk read. The download
        // is bounded by the caller's token and the connect timeout instead.
        services.AddHttpClient(GutenbergProvider.SnapshotHttpClientName, client =>
            ConfigureProviderClient(
                client,
                GutenbergCatalog.BaseUrl,
                Timeout.InfiniteTimeSpan,
                ProviderHttp.ProductUserAgent))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
            });

        // Standard Ebooks approved Nostos for OPDS access by whitelisting this
        // project User-Agent. There is deliberately no shared feed credential:
        // SelfHosted and Cloud use the same provider identity, and no secret is
        // embedded in the client or public repository.
        services.AddHttpClient(StandardEbooksProvider.HttpClientName, client =>
            ConfigureProviderClient(
                client,
                StandardEbooksCatalog.BaseUrl,
                TimeSpan.FromSeconds(20),
                StandardEbooksProvider.ApprovedUserAgent,
                StandardEbooksProvider.OpdsAccept));
        // The full Standard Ebooks catalog is a multi-megabyte OPDS feed, so
        // snapshot reads have their own timeout and never inherit the live
        // search deadline.
        services.AddHttpClient(StandardEbooksProvider.SnapshotHttpClientName, client =>
            ConfigureProviderClient(
                client,
                StandardEbooksCatalog.BaseUrl,
                TimeSpan.FromMinutes(2),
                StandardEbooksProvider.ApprovedUserAgent,
                StandardEbooksProvider.OpdsAccept))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
            });

        services.AddHttpClient(WikisourceProvider.HttpClientName, client =>
            ConfigureProviderClient(
                client,
                WikisourceCatalog.BaseUrl,
                TimeSpan.FromSeconds(20),
                ProviderHttp.ProductUserAgent));

        services.AddHttpClient(LitteraturbankenProvider.HttpClientName, client =>
            ConfigureProviderClient(
                client,
                LitteraturbankenProvider.BaseUrl,
                TimeSpan.FromSeconds(20),
                ProviderHttp.ProductUserAgent,
                ProviderHttp.JsonMediaType));

        services.AddHttpClient(LibriVoxProvider.HttpClientName, client =>
            ConfigureProviderClient(
                client,
                LibriVoxCatalog.BaseUrl,
                TimeSpan.FromSeconds(20),
                ProviderHttp.ProductUserAgent));
        // Bulk snapshot reads page through the whole catalogue, so they get
        // their own client with a longer timeout instead of the discovery
        // client's 20 s.
        services.AddHttpClient(LibriVoxProvider.SnapshotHttpClientName, client =>
            ConfigureProviderClient(
                client,
                LibriVoxCatalog.BaseUrl,
                TimeSpan.FromMinutes(2),
                ProviderHttp.ProductUserAgent));

        return services;
    }

    private static void ConfigureProviderClient(
        HttpClient client,
        string baseAddress,
        TimeSpan timeout,
        string userAgent,
        string? accept = null)
    {
        client.BaseAddress = new Uri(baseAddress);
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        if (accept is not null)
            client.DefaultRequestHeaders.Accept.ParseAdd(accept);
    }
}

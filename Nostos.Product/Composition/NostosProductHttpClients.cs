using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Providers.LibriVox;
using Nostos.Backend.Providers.Litteraturbanken;
using Nostos.Backend.Providers.StandardEbooks;
using Nostos.Backend.Providers.Wikisource;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Notes.Imports;

namespace Nostos.Product.Composition;

internal static class NostosProductHttpClients
{
    private const string NostosUserAgent =
        "Nostos/1.0 (+https://github.com/Christian-Gennari/Nostos)";

    internal static IServiceCollection AddNostosProductHttpClients(
        this IServiceCollection services)
    {
        services.AddHttpClient();

        services.AddHttpClient(ReadwiseApiClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        });

        services.AddHttpClient(BookLookupService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        });

        services.AddHttpClient(ProviderContentDownloader.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });

        services.AddHttpClient(GutenbergProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(GutenbergCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        });
        // The daily RDF snapshot is a 177 MB archive, so it gets its own client:
        // the 20 s search timeout must never apply to a bulk read. The download
        // is bounded by the caller's token and the connect timeout instead.
        services.AddHttpClient(GutenbergProvider.SnapshotHttpClientName, client =>
        {
            client.BaseAddress = new Uri(GutenbergCatalog.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });

        // Standard Ebooks approved Nostos for OPDS access by whitelisting this
        // project User-Agent. There is deliberately no shared feed credential:
        // SelfHosted and Cloud use the same provider identity, and no secret is
        // embedded in the client or public repository.
        services.AddHttpClient(StandardEbooksProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(StandardEbooksCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                StandardEbooksProvider.ApprovedUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd(
                StandardEbooksProvider.OpdsAccept);
        });
        // The full Standard Ebooks catalog is a multi-megabyte OPDS feed, so
        // snapshot reads have their own timeout and never inherit the live
        // search deadline.
        services.AddHttpClient(StandardEbooksProvider.SnapshotHttpClientName, client =>
        {
            client.BaseAddress = new Uri(StandardEbooksCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                StandardEbooksProvider.ApprovedUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd(
                StandardEbooksProvider.OpdsAccept);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });

        services.AddHttpClient(WikisourceProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(WikisourceCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        });

        services.AddHttpClient(LitteraturbankenProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(LitteraturbankenProvider.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddHttpClient(LibriVoxProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(LibriVoxCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        });
        // Bulk snapshot reads page through the whole catalogue, so they get
        // their own client with a longer timeout instead of the discovery
        // client's 20 s.
        services.AddHttpClient(LibriVoxProvider.SnapshotHttpClientName, client =>
        {
            client.BaseAddress = new Uri(LibriVoxCatalog.BaseUrl);
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NostosUserAgent);
        });

        return services;
    }
}

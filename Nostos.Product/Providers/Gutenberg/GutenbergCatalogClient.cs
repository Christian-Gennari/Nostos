using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// The HTTP side of Project Gutenberg's catalogue: the live OPDS/Atom document a
/// search or detail request reads, and the conditional HEAD probe for the daily
/// bulk archive.
///
/// Everything about transport lives here — the named client, the product
/// identity, retry policy for transient gateway answers, status classification
/// and body parsing. What a feed <em>means</em> is <see cref="GutenbergCatalog"/>,
/// which stays a pure function of the document text.
/// </summary>
internal sealed class GutenbergCatalogClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public GutenbergCatalogClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// One catalogue document, or <see langword="null"/> for the catalogue's
    /// ordinary "no such item" answer (404/410).
    ///
    /// <paramref name="retryDelays"/> is the retry policy for a transient
    /// gateway answer: one retry per entry, with the given wait. Null keeps a
    /// single attempt, which is what an interactive search wants — the user can
    /// simply search again.
    /// </summary>
    public async Task<XDocument?> LoadAsync(
        string path,
        IReadOnlyList<TimeSpan>? retryDelays,
        CancellationToken ct)
    {
        var client = ProviderHttp.Client(_httpClientFactory, GutenbergProvider.HttpClientName);

        for (var attempt = 0; ; attempt++)
        {
            using var response = await ProviderHttp.GetHeadersAsync(
                client,
                GutenbergProvider.ProviderIdentifier,
                path,
                ct);

            // The catalogue answers 404 for an unknown ebook; that is an ordinary
            // "no such item", not a source failure.
            if (ProviderHttp.IsMissing(response))
                return null;

            if (response.StatusCode is HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                && retryDelays is not null
                && attempt < retryDelays.Count)
            {
                _logger.LogInformation(
                    "Gutenberg catalogue answered HTTP {Status} for {Path}; retrying.",
                    (int)response.StatusCode, path);
                await Task.Delay(retryDelays[attempt], ct);
                continue;
            }

            ProviderHttp.EnsureSuccess(response, GutenbergProvider.ProviderIdentifier, "the catalogue");

            return await ProviderHttp.ReadXmlAsync(response, GutenbergProvider.ProviderIdentifier, ct);
        }
    }

    /// <summary>
    /// The conditional HEAD probe for the bulk archive. The caller owns the
    /// response and decides whether a body read follows.
    ///
    /// The probe is bounded by the snapshot's own wall-clock budget rather than
    /// the search client's request timeout, because it shares the long-lived
    /// snapshot client.
    /// </summary>
    public async Task<HttpResponseMessage> ProbeSnapshotAsync(
        ProviderSnapshotRequest request,
        TimeSpan maxDuration,
        CancellationToken ct)
    {
        using var probe = new HttpRequestMessage(HttpMethod.Head, GutenbergCatalog.SnapshotPath);
        ApplyValidators(probe, request);

        var snapshotClient = ProviderHttp.Client(
            _httpClientFactory,
            GutenbergProvider.SnapshotHttpClientName);

        return await ProviderHttp.ExecuteAsync(
            GutenbergProvider.ProviderIdentifier,
            ct,
            async token =>
            {
                using var probeTimeout = new CancellationTokenSource(maxDuration);
                using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    token,
                    probeTimeout.Token);
                return await snapshotClient.SendAsync(
                    probe,
                    HttpCompletionOption.ResponseHeadersRead,
                    probeCancellation.Token);
            });
    }

    private static void ApplyValidators(HttpRequestMessage request, ProviderSnapshotRequest snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ETag)
            && EntityTagHeaderValue.TryParse(snapshot.ETag, out var entityTag))
        {
            request.Headers.IfNoneMatch.Add(entityTag);
        }

        if (snapshot.IfModifiedSince is { } ifModifiedSince)
            request.Headers.IfModifiedSince = ifModifiedSince;
    }
}

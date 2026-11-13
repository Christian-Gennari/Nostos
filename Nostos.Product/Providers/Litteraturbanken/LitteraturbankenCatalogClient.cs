using System.Text.Json;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Litteraturbanken;

/// <summary>
/// Everything that goes over HTTP for Litteraturbanken: the catalog page for a
/// query, and the license and provenance documents the rights renderer is built
/// from.
///
/// The catalog is a POST-shaped query expressed as a form-encoded query string,
/// and it has no catalog-wide validator, so there is no conditional read here.
/// The rights documents change rarely, so they are fetched once and cached for
/// the lifetime of the provider; a single gate keeps two concurrent searches
/// from fetching them twice.
/// </summary>
internal sealed class LitteraturbankenCatalogClient
{
    private const string CatalogPath = "/api/query_string/etext,faksimil";
    private const string LicensePath = "/red/etc/license/license.json";
    private const string ProvenancePath = "/red/etc/provenance/provenance.json";
    private const string IncludeFields =
        "lbworkid,titlepath,title,titleid,work_titleid,shorttitle,mediatype," +
        "authors.authorid,authors.full_name,export.type,export.size,license," +
        "provenance.library,provenance.signum,provenance.text2,printed," +
        "sort_date_imprint.plain";
    private const string ExcludeFields = "text,parts,sourcedesc,pages,errata";

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _providerId;
    private readonly SemaphoreSlim _rightsGate = new(1, 1);
    private LitteraturbankenRightsCatalog? _rightsCatalog;

    public LitteraturbankenCatalogClient(IHttpClientFactory httpClientFactory, string providerId)
    {
        _httpClientFactory = httpClientFactory;
        _providerId = providerId;
    }

    /// <summary>One catalog page. A missing <c>data</c> list is a source fault.</summary>
    public async Task<CatalogPage> LoadPageAsync(
        string query,
        int from,
        int to,
        CancellationToken ct,
        string sortField = "sortkey|asc")
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = from.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["to"] = to.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["include"] = IncludeFields,
            ["exclude"] = ExcludeFields,
            ["sort_field"] = sortField,
            ["q"] = query,
        });
        var queryString = await form.ReadAsStringAsync(ct);

        using var response = await ProviderHttp.GetHeadersAsync(
            ProviderHttp.Client(
                _httpClientFactory,
                LitteraturbankenProvider.HttpClientName,
                accept: ProviderHttp.JsonMediaType),
            _providerId,
            CatalogPath + "?" + queryString,
            ct);

        ProviderHttp.EnsureSuccess(response, _providerId, "the public catalog");

        var result = await ProviderHttp.ReadJsonAsync<CatalogPage>(response, _providerId, JsonOptions, ct);

        if (result?.Data is null)
            throw ProviderException.InvalidResponse(_providerId, "the catalog omitted its data list");

        return result;
    }

    /// <summary>
    /// The license and provenance renderer, fetched at most once. Each document
    /// keeps the provider-specific wording of its own failure, so a broken
    /// rights source is not reported as a broken catalog.
    /// </summary>
    public async Task<LitteraturbankenRightsCatalog> GetRightsCatalogAsync(CancellationToken ct)
    {
        if (_rightsCatalog is not null)
            return _rightsCatalog;

        await _rightsGate.WaitAsync(ct);
        try
        {
            if (_rightsCatalog is not null)
                return _rightsCatalog;

            var licenses = await GetJsonAsync<Dictionary<string, string>>(LicensePath, ct);
            var provenance = await GetJsonElementAsync(ProvenancePath, ct);
            _rightsCatalog = LitteraturbankenRightsCatalog.Parse(licenses, provenance);
            return _rightsCatalog;
        }
        finally
        {
            _rightsGate.Release();
        }
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await ProviderHttp.GetHeadersAsync(
            ProviderHttp.Client(
                _httpClientFactory,
                LitteraturbankenProvider.HttpClientName,
                accept: ProviderHttp.JsonMediaType),
            _providerId,
            path,
            ct);

        ProviderHttp.EnsureSuccess(response, _providerId, "rights metadata");

        return await ProviderHttp.ReadJsonAsync<T>(response, _providerId, JsonOptions, ct)
            ?? throw ProviderException.InvalidResponse(_providerId, "rights metadata was empty");
    }

    private async Task<JsonElement> GetJsonElementAsync(string path, CancellationToken ct)
    {
        using var response = await ProviderHttp.GetHeadersAsync(
            ProviderHttp.Client(
                _httpClientFactory,
                LitteraturbankenProvider.HttpClientName,
                accept: ProviderHttp.JsonMediaType),
            _providerId,
            path,
            ct);

        ProviderHttp.EnsureSuccess(response, _providerId, "provenance metadata");

        using var document = await ProviderHttp.ReadJsonAsync(response, _providerId, ct);
        return document.RootElement.Clone();
    }
}

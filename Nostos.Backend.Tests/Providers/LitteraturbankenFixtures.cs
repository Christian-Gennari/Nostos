using System.Net;
using System.Text.Json;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The recorded Litteraturbanken responses and the small catalog server used by
/// both the provider's behaviour tests and the characterization suite. The
/// payloads are the ones the live service answered when they were captured, so a
/// refactor is measured against the same bytes.
/// </summary>
internal static class LitteraturbankenFixtures
{
    public const string RightsLicenseJson = """
        {
          "cc-0": "<text><p>Utgåvan är fri från kända upphovsrättsliga begränsningar. CC0. Hänvisa till Litteraturbanken.</p><a rel=\"license\" href=\"https://creativecommons.org/publicdomain/zero/1.0/deed.sv\">CC0</a></text>",
          "pd": "<text><p>Detta verk är fritt från kända upphovsrättsliga begränsningar. Hänvisa till {{provenance}} och Litteraturbanken.</p><a rel=\"license\" href=\"https://creativecommons.org/publicdomain/mark/1.0/deed.sv\">Public Domain</a></text>",
          "cc-by": "<text><p>Litteraturbanken. För detta verk gäller licensen CC BY.</p><a rel=\"license\" href=\"https://creativecommons.org/licenses/by/4.0/deed.sv\">CC BY</a></text>"
        }
        """;

    public const string ProvenanceJson = """
        {
          "GUB": {
            "fullname": "Göteborgs universitetsbibliotek",
            "text": {
              "etext": "Det exemplar som ligger till grund för Litteraturbankens utgåva tillhör Göteborgs universitetsbibliotek{{signum}}.",
              "faksimilprint": "Det avbildade exemplaret tillhör Göteborgs universitetsbibliotek{{signum}}.",
              "faksimilnoprint": "Det avbildade manuskriptet tillhör Göteborgs universitetsbibliotek{{signum}}."
            }
          }
        }
        """;

    public const string TwoFormatCatalogJson = """
        {
          "hits": 2,
          "distinct_hits": 1,
          "data": [
            {
              "lbworkid": "lb100",
              "title": "Kåtornas folk",
              "shorttitle": "Kåtornas folk",
              "titleid": "KatornasFolk",
              "work_titleid": "KatornasFolk",
              "mediatype": "etext",
              "license": "cc-0",
              "show": true,
              "searchable": true,
              "sort_date_imprint": { "plain": "1916" },
              "authors": [{ "authorid": "NordstromEB", "full_name": "Ester Blenda Nordström" }],
              "provenance": [{ "library": "GUB", "signum": "Geogr. Sv. Lappland" }],
              "export": [{ "type": "epub", "size": 301593 }]
            },
            {
              "lbworkid": "lb100",
              "title": "Kåtornas folk",
              "shorttitle": "Kåtornas folk",
              "titleid": "KatornasFolk",
              "work_titleid": "KatornasFolk",
              "mediatype": "faksimil",
              "license": "pd",
              "printed": true,
              "show": true,
              "searchable": true,
              "sort_date_imprint": { "plain": "1916" },
              "authors": [{ "authorid": "NordstromEB", "full_name": "Ester Blenda Nordström" }],
              "provenance": [{ "library": "GUB", "signum": "Shelf 42" }],
              "export": [{ "type": "pdf", "size": 8000000 }]
            }
          ]
        }
        """;

    public const string RestrictedCatalogJson = """
        {
          "hits": 1,
          "data": [{
            "lbworkid": "lb200",
            "title": "Restricted",
            "titleid": "Restricted",
            "mediatype": "etext",
            "license": "lb-2-20120306",
            "show": true,
            "authors": [{ "authorid": "Author", "full_name": "Author Name" }],
            "export": [{ "type": "epub", "size": 200000 }]
          }]
        }
        """;

    public static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json"),
        };

    public static object CatalogRow(
        string id,
        string title,
        string mediaType = "etext",
        string format = "epub",
        long size = 301593) => new
    {
        lbworkid = id,
        title,
        shorttitle = title,
        titleid = title.Replace(' ', '_'),
        work_titleid = title.Replace(' ', '_'),
        mediatype = mediaType,
        license = mediaType == "faksimil" ? "pd" : "cc-0",
        printed = true,
        searchable = true,
        show = true,
        sort_date_imprint = new { plain = "1916" },
        authors = new[] { new { authorid = "TestAuthor", full_name = "Test Author" } },
        provenance = new[] { new { library = "GUB", signum = "Shelf 1" } },
        export = new[] { new { type = format, size } },
    };

    public static string CatalogJson(int hits, IReadOnlyCollection<object> rows) =>
        JsonSerializer.Serialize(new
        {
            hits,
            distinct_hits = rows.Select(row => JsonSerializer.SerializeToElement(row)
                .GetProperty("lbworkid").GetString()).Distinct().Count(),
            data = rows,
        });

    /// <summary>
    /// Behaves like the live catalog for the snapshot query: honours
    /// <c>sort_field</c> and the <c>lbworkid:&gt;=</c> filter, and rejects
    /// offset paging at 10,000 rows (the real service answers HTTP 500).
    /// </summary>
    public static HttpResponseMessage ServeCatalog(
        HttpRequestMessage request,
        IReadOnlyList<(string Id, object Row)> catalog)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/red/etc/license/license.json")
            return Json(RightsLicenseJson);
        if (path == "/red/etc/provenance/provenance.json")
            return Json(ProvenanceJson);
        if (path != "/api/query_string/etext,faksimil")
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var from = int.Parse(ReadQueryValue(request.RequestUri!, "from"));
        var to = int.Parse(ReadQueryValue(request.RequestUri!, "to"));
        if (from >= 10_000)
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var query = ReadQueryValue(request.RequestUri!, "q");
        IEnumerable<(string Id, object Row)> matches = catalog;
        const string marker = "lbworkid:>=";
        var markerIndex = query.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var after = query[(markerIndex + marker.Length)..].Split(' ')[0];
            matches = matches.Where(entry =>
                string.CompareOrdinal(entry.Id, after) >= 0);
        }

        // Default order scatters the rows of one work, as the live catalog does.
        matches = ReadQueryValue(request.RequestUri!, "sort_field") == "lbworkid|asc"
            ? matches.OrderBy(entry => entry.Id, StringComparer.Ordinal)
            : matches.OrderBy(entry => (entry.Id.GetHashCode() & 0x7fffffff) % 997)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal);

        var all = matches.ToArray();
        var page = all.Skip(from).Take(to - from).Select(entry => entry.Row).ToArray();
        return Json(CatalogJson(all.Length, page));
    }

    public static string ReadQueryValue(Uri uri, string name)
    {
        foreach (var field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator < 0)
                continue;

            if (WebUtility.UrlDecode(field[..separator]) == name)
                return WebUtility.UrlDecode(field[(separator + 1)..]) ?? string.Empty;
        }

        return string.Empty;
    }
}

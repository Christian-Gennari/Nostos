using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace Nostos.Backend.Providers.Litteraturbanken;

/// <summary>
/// Renders the rights statement for one catalog record from Litteraturbanken's
/// own license and provenance documents.
///
/// A license document is an XHTML fragment; a provenance document maps a
/// library code to the attribution text for each media type. The renderer fills
/// the source's <c>{{provenance}}</c> and <c>{{signum}}</c> placeholders, and
/// refuses to produce a statement when any part it needs is missing — a work
/// whose rights cannot be demonstrated is not offered at all.
/// </summary>
internal sealed class LitteraturbankenRightsCatalog
{
    /// <summary>
    /// Only these source license codes are redistributable by Nostos. The set is
    /// the rights filter every search, item and bulk read shares.
    /// </summary>
    internal static readonly string[] AllowedLicenseCodes = ["cc-0", "pd", "cc-by"];

    private static readonly HashSet<string> AllowedLicenses =
        new(AllowedLicenseCodes, StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, LicenseNotice> _licenses;
    private readonly IReadOnlyDictionary<string, ProvenanceNotice> _provenance;

    private LitteraturbankenRightsCatalog(
        IReadOnlyDictionary<string, LicenseNotice> licenses,
        IReadOnlyDictionary<string, ProvenanceNotice> provenance)
    {
        _licenses = licenses;
        _provenance = provenance;
    }

    public static bool AllowsLicense(string? license) =>
        AllowedLicenses.Contains(license ?? string.Empty);

    public static LitteraturbankenRightsCatalog Parse(
        IReadOnlyDictionary<string, string> licenseMarkup,
        JsonElement provenanceRoot)
    {
        var licenses = new Dictionary<string, LicenseNotice>(StringComparer.Ordinal);
        foreach (var code in AllowedLicenseCodes)
        {
            if (!licenseMarkup.TryGetValue(code, out var markup))
                continue;

            try
            {
                var document = XDocument.Parse(markup, LoadOptions.None);
                var statement = NormalizeWhitespace(document.Root?.Value ?? string.Empty);
                var licenseLink = document
                    .Descendants()
                    .FirstOrDefault(element =>
                        element.Name.LocalName == "a"
                        && string.Equals(
                            element.Attribute("rel")?.Value,
                            "license",
                            StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("href")
                    ?.Value;

                if (statement.Length > 0)
                {
                    var safeLink = Uri.TryCreate(licenseLink, UriKind.Absolute, out var uri)
                        && uri.Scheme == Uri.UriSchemeHttps
                        && uri.Host == "creativecommons.org"
                        ? uri.ToString()
                        : null;
                    licenses[code] = new LicenseNotice(statement, safeLink);
                }
            }
            catch (System.Xml.XmlException)
            {
                // A malformed source license is not permission to expose a work.
            }
        }

        var provenance = new Dictionary<string, ProvenanceNotice>(StringComparer.Ordinal);
        if (provenanceRoot.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in provenanceRoot.EnumerateObject())
            {
                provenance[entry.Name] = new ProvenanceNotice(
                    ReadTextMap(entry.Value, "text"),
                    ReadTextMap(entry.Value, "text2"));
            }
        }

        return new LitteraturbankenRightsCatalog(licenses, provenance);
    }

    /// <summary>
    /// The statement for one record, with the license's <c>{{provenance}}</c>
    /// placeholder (or an appended provenance note) resolved. False when the
    /// license is not allowed, its markup is missing, or the provenance text it
    /// needs is not available.
    /// </summary>
    public bool TryBuildStatement(
        CatalogRecord record,
        out string? statement,
        out string? rightsUrl)
    {
        statement = null;
        rightsUrl = null;
        if (record.License is null
            || !_licenses.TryGetValue(record.License, out var notice))
        {
            return false;
        }

        var provenanceText = BuildProvenanceText(record);
        var rendered = notice.Statement;
        if (rendered.Contains("{{provenance}}", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(provenanceText))
                return false;

            rendered = rendered.Replace(
                "{{provenance}}",
                provenanceText,
                StringComparison.Ordinal);
        }
        else if (!string.IsNullOrWhiteSpace(provenanceText))
        {
            rendered += " Provenance: " + provenanceText;
        }

        statement = rendered;
        rightsUrl = notice.RightsUrl;
        return true;
    }

    private string? BuildProvenanceText(CatalogRecord record)
    {
        if (record.Provenance.Count == 0)
            return null;

        var mediaType = record.MediaType == "faksimil"
            ? record.Printed ? "faksimilprint" : "faksimilnoprint"
            : "etext";
        var statements = new List<string>();

        for (var index = 0; index < record.Provenance.Count; index++)
        {
            var item = record.Provenance[index];
            if (string.IsNullOrWhiteSpace(item.Library)
                || !_provenance.TryGetValue(item.Library, out var source))
            {
                return null;
            }

            var sourceText = index > 0 && item.Text2
                ? source.Text2.GetValueOrDefault(mediaType)
                : null;
            if (string.IsNullOrWhiteSpace(sourceText))
                sourceText = source.Text.GetValueOrDefault(mediaType);

            if (string.IsNullOrWhiteSpace(sourceText))
                return null;

            var signum = string.IsNullOrWhiteSpace(item.Signum)
                ? string.Empty
                : " (" + item.Signum.Trim() + ")";
            statements.Add(sourceText.Replace(
                "{{signum}}",
                signum,
                StringComparison.Ordinal));
        }

        return string.Join(" ", statements);
    }

    private static IReadOnlyDictionary<string, string> ReadTextMap(
        JsonElement source,
        string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var map)
            || map.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return map.EnumerateObject()
            .Where(entry => entry.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(
                entry => entry.Name,
                entry => entry.Value.GetString() ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(
            " ",
            WebUtility.HtmlDecode(value)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record LicenseNotice(string Statement, string? RightsUrl);

    private sealed record ProvenanceNotice(
        IReadOnlyDictionary<string, string> Text,
        IReadOnlyDictionary<string, string> Text2);
}

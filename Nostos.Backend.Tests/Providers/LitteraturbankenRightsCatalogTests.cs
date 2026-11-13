using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Providers.Litteraturbanken;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The extracted Litteraturbanken rights renderer: license markup, provenance
/// lookup and the placeholder substitution a statement is built from.
/// </summary>
public sealed class LitteraturbankenRightsCatalogTests
{
    private static readonly LitteraturbankenRightsCatalog Rights = LitteraturbankenFixtures.RightsCatalog();
    private static readonly IReadOnlyList<CatalogRecord> Records =
        LitteraturbankenFixtures.Records(LitteraturbankenFixtures.TwoFormatCatalogJson);

    [Fact]
    public void TryBuildStatement_AppendsProvenanceWhenTheLicenseHasNoPlaceholder()
    {
        var record = Records[0];

        Rights.TryBuildStatement(record, out var statement, out var rightsUrl).Should().BeTrue();

        statement.Should().Be(
            "Utgåvan är fri från kända upphovsrättsliga begränsningar. CC0. Hänvisa till Litteraturbanken.CC0 "
            + "Provenance: Det exemplar som ligger till grund för Litteraturbankens utgåva tillhör "
            + "Göteborgs universitetsbibliotek (Geogr. Sv. Lappland).");
        rightsUrl.Should().Be("https://creativecommons.org/publicdomain/zero/1.0/deed.sv");
    }

    [Fact]
    public void TryBuildStatement_FillsTheProvenancePlaceholder_ForTheFaksimilMediaType()
    {
        var record = Records[1];

        Rights.TryBuildStatement(record, out var statement, out var rightsUrl).Should().BeTrue();

        statement.Should().Be(
            "Detta verk är fritt från kända upphovsrättsliga begränsningar. Hänvisa till "
            + "Det avbildade exemplaret tillhör Göteborgs universitetsbibliotek (Shelf 42). "
            + "och Litteraturbanken.Public Domain");
        rightsUrl.Should().Be("https://creativecommons.org/publicdomain/mark/1.0/deed.sv");
    }

    [Fact]
    public void TryBuildStatement_ForALicenseThatIsNotListed_IsFalse()
    {
        var record = Records[0] with { License = "lb-2-20120306" };

        Rights.TryBuildStatement(record, out var statement, out var rightsUrl).Should().BeFalse();

        statement.Should().BeNull();
        rightsUrl.Should().BeNull();
    }

    [Fact]
    public void TryBuildStatement_ForAPlaceholderWhoseProvenanceIsUnknown_IsFalse()
    {
        // "pd" needs {{provenance}}; the library code is not in the source document.
        var record = Records[1] with
        {
            Provenance = [new CatalogProvenance { Library = "UNKNOWN", Signum = "A 1" }],
        };

        Rights.TryBuildStatement(record, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryBuildStatement_ForALicenseNeedingNoProvenance_RendersWithoutOne()
    {
        var record = Records[0] with { Provenance = [] };

        Rights.TryBuildStatement(record, out var statement, out _).Should().BeTrue();

        statement.Should().Be(
            "Utgåvan är fri från kända upphovsrättsliga begränsningar. CC0. Hänvisa till Litteraturbanken.CC0");
    }

    [Fact]
    public void TryBuildStatement_UsesTheSecondTextStanzaForAdditionalProvenanceRows()
    {
        const string provenance = """
            {
              "GUB": {
                "text": { "etext": "First collection{{signum}}." },
                "text2": { "etext": "Second collection{{signum}}." }
              }
            }
            """;
        using var document = JsonDocument.Parse(provenance);
        var licenses = JsonSerializer.Deserialize<Dictionary<string, string>>(
            LitteraturbankenFixtures.RightsLicenseJson)!;
        var rights = LitteraturbankenRightsCatalog.Parse(licenses, document.RootElement.Clone());
        var record = Records[0] with
        {
            Provenance =
            [
                new CatalogProvenance { Library = "GUB", Signum = "A 1" },
                new CatalogProvenance { Library = "GUB", Signum = "B 2", Text2 = true },
            ],
        };

        rights.TryBuildStatement(record, out var statement, out _).Should().BeTrue();

        statement.Should().Contain("Provenance: First collection (A 1). Second collection (B 2).");
    }

    [Fact]
    public void Parse_KeepsOnlyHttpsCreativeCommonsLicenseLinks()
    {
        var licenses = new Dictionary<string, string>
        {
            ["cc-by"] = """<text><p>CC BY.</p><a rel="license" href="https://example.test/not-cc">CC BY</a></text>""",
        };
        using var provenance = JsonDocument.Parse("{}");
        var rights = LitteraturbankenRightsCatalog.Parse(licenses, provenance.RootElement.Clone());
        var record = Records[0] with { License = "cc-by", Provenance = [] };

        rights.TryBuildStatement(record, out var statement, out var rightsUrl).Should().BeTrue();

        statement.Should().Be("CC BY.CC BY");
        rightsUrl.Should().BeNull();
    }

    [Fact]
    public void Parse_IgnoresMalformedLicenseMarkup()
    {
        var licenses = new Dictionary<string, string>
        {
            ["cc-0"] = "<text><p>broken",
        };
        using var provenance = JsonDocument.Parse("{}");
        var rights = LitteraturbankenRightsCatalog.Parse(licenses, provenance.RootElement.Clone());

        rights.TryBuildStatement(Records[0], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_WithoutAProvenanceObject_StillRendersALicenseThatNeedsNone()
    {
        var licenses = JsonSerializer.Deserialize<Dictionary<string, string>>(
            LitteraturbankenFixtures.RightsLicenseJson)!;
        using var provenance = JsonDocument.Parse("[]");
        var rights = LitteraturbankenRightsCatalog.Parse(licenses, provenance.RootElement.Clone());

        rights.TryBuildStatement(Records[0] with { Provenance = [] }, out var statement, out _).Should().BeTrue();
        statement.Should().NotBeNull();
    }
}

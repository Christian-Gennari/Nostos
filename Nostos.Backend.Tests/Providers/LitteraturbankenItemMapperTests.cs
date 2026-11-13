using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Litteraturbanken;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The extracted Litteraturbanken item mapper: how one work's catalog rows
/// combine into an item, which formats survive the rights and size filters, and
/// which file an acquisition resolves to.
/// </summary>
public sealed class LitteraturbankenItemMapperTests
{
    private static readonly LitteraturbankenRightsCatalog Rights = LitteraturbankenFixtures.RightsCatalog();
    private static readonly IReadOnlyList<CatalogRecord> Records =
        LitteraturbankenFixtures.Records(LitteraturbankenFixtures.TwoFormatCatalogJson);

    private static LitteraturbankenItemMapper CreateMapper() =>
        new(LitteraturbankenProvider.ProviderIdentifier, 128L * 1024 * 1024);

    [Fact]
    public void BuildWork_CombinesEveryFormatOfOneWorkIntoOneItem()
    {
        var work = CreateMapper().BuildWork(Records, Rights);

        work.Should().NotBeNull();
        work!.Item.ProviderId.Should().Be("litteraturbanken");
        work.Item.ExternalId.Should().Be("lb100");
        work.Item.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        work.Item.Metadata.Should().Be(new ProviderMetadata(
            Title: "Kåtornas folk",
            Author: "Ester Blenda Nordström",
            Language: "Swedish",
            Publisher: "Litteraturbanken",
            PublishedDate: "1916"));
        work.Item.Assets.Select(asset => asset.Id).Should().Equal("epub", "pdf");
        work.Item.Assets[0].IsPreferred.Should().BeTrue();
        work.Item.Assets[1].IsPreferred.Should().BeFalse();
        work.Item.Source!.ItemUrl.Should().Be(
            "https://litteraturbanken.se/författare/NordstromEB/titlar/KatornasFolk/info");
        work.Item.Source.RightsStatement.Should().StartWith("EPUB: ").And.Contain(" PDF: ");
        work.Assets.Select(asset => asset.Asset.Id).Should().Equal("epub", "pdf");
    }

    [Fact]
    public void BuildWork_WithoutRows_IsNull()
    {
        CreateMapper().BuildWork([], Rights).Should().BeNull();
    }

    [Fact]
    public void BuildWork_DropsAnAssetWhoseDuplicateRowsDisagreeOnRights()
    {
        var first = Records[0];
        var second = first with
        {
            Provenance = [new CatalogProvenance { Library = "GUB", Signum = "Another shelf" }],
        };

        CreateMapper().BuildWork([first, second], Rights).Should().BeNull(
            "the same asset id with two different provenance signatures cannot be offered");
    }

    [Fact]
    public void BuildWork_DropsARecordThatIsNotRightsAllowed()
    {
        var restricted = Records[0] with { License = "lb-2-20120306" };

        var work = CreateMapper().BuildWork([restricted, Records[1]], Rights);

        work.Should().NotBeNull("the facsimile is still an allowed format");
        work!.Item.Assets.Select(asset => asset.Id).Should().Equal("pdf");
        work.Item.Assets[0].IsPreferred.Should().BeFalse("only the EPUB representation is marked preferred");
    }

    [Fact]
    public void BuildWork_DropsAnExportOverTheFileLimit()
    {
        var oversized = Records[0] with
        {
            Exports = [new CatalogExport { Type = "epub", Size = (128L * 1024 * 1024) + 1 }],
        };

        CreateMapper().BuildWork([oversized], Rights).Should().BeNull();
    }

    [Fact]
    public void BuildWork_DropsAFormatTheSourceDoesNotList()
    {
        var noExport = Records[0] with { Exports = [] };

        CreateMapper().BuildWork([noExport], Rights).Should().BeNull();
    }

    [Fact]
    public void PlanAcquisition_WithoutAnAssetId_PicksThePreferredEpub()
    {
        var work = CreateMapper().BuildWork(Records, Rights)!;

        var plan = CreateMapper().PlanAcquisition(work, assetId: null, externalId: "lb100");

        plan.ProviderId.Should().Be("litteraturbanken");
        plan.ExternalId.Should().Be("lb100");
        plan.Asset.Should().Be(new ProviderAsset(
            "epub",
            ProviderMediaKind.Ebook,
            "EPUB",
            "application/epub+zip",
            301593,
            IsPreferred: true));
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(
            new Uri("https://litteraturbanken.se/txt/epub/NordstromEB_KatornasFolk.epub"));
        plan.Parts[0].FileExtension.Should().Be(".epub");
        plan.Parts[0].ExpectedBytes.Should().Be(301593);
        plan.Parts[0].Label.Should().Be("EPUB");
        plan.Output.Should().Be(new ProviderOutput(".epub", "application/epub+zip", "EPUB"));
        plan.Source!.RightsUrl.Should().Be("https://creativecommons.org/publicdomain/zero/1.0/deed.sv");
    }

    [Fact]
    public void PlanAcquisition_WithAnExplicitPdf_UsesThatRecordsOwnRights()
    {
        var work = CreateMapper().BuildWork(Records, Rights)!;

        var plan = CreateMapper().PlanAcquisition(work, assetId: "pdf", externalId: "lb100");

        plan.Asset.Id.Should().Be("pdf");
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(new Uri("https://litteraturbanken.se/export/faksimil/lb100.pdf"));
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
        plan.Source!.RightsUrl.Should().Be(
            "https://creativecommons.org/publicdomain/mark/1.0/deed.sv");
        plan.Source.RightsStatement.Should().Contain(
            "Det avbildade exemplaret tillhör Göteborgs universitetsbibliotek (Shelf 42).");
    }

    [Fact]
    public void PlanAcquisition_WithANamedAsset_MatchesTheIdCaseInsensitively()
    {
        var work = CreateMapper().BuildWork(Records, Rights)!;

        CreateMapper().PlanAcquisition(work, assetId: "EPUB", externalId: "lb100")
            .Asset.Id.Should().Be("epub");
    }

    [Fact]
    public void PlanAcquisition_ForAnUnknownAsset_IsAssetUnavailable()
    {
        var work = CreateMapper().BuildWork(Records, Rights)!;

        var act = () => CreateMapper().PlanAcquisition(work, assetId: "audiobook", externalId: "lb100");

        var error = act.Should().Throw<ProviderException>().Which;
        error.Code.Should().Be(ProviderException.AssetUnavailable);
        error.Message.Should().Be("litteraturbanken item lb100 does not offer asset 'audiobook'.");
    }
}

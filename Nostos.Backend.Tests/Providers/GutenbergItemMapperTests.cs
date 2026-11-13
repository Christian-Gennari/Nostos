using FluentAssertions;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The extracted Gutenberg mapper: a parsed book becomes the same provider item
/// whether it came from the live feed or the bulk snapshot, and an acquisition
/// request always resolves to exactly one advertised asset.
/// </summary>
public sealed class GutenbergItemMapperTests
{
    private static readonly GutenbergBook Book = new(
        Id: "1342",
        Title: "Pride and Prejudice",
        Author: "Jane Austen",
        Description: "A novel.",
        Language: "English",
        Categories: "England -- Fiction",
        Rights: "Public domain in the USA.",
        Assets:
        [
            new GutenbergAsset(
                "epub3-images",
                "EPUB3 (E-readers incl. Send-to-Kindle)",
                "application/epub+zip",
                new Uri("https://www.gutenberg.org/ebooks/1342.epub3.images"),
                ".epub",
                "EPUB",
                SizeBytes: 24835578,
                IsPreferred: true),
            new GutenbergAsset(
                "pdf",
                "PDF",
                "application/pdf",
                new Uri("https://www.gutenberg.org/ebooks/1342.pdf"),
                ".pdf",
                "PDF",
                SizeBytes: 4123456),
        ]);

    private static readonly GutenbergItemMapper Mapper = new(GutenbergProvider.ProviderIdentifier);

    [Fact]
    public void ToProviderItem_WithAssets_MapsMetadataEveryAssetAndTheDeterministicCover()
    {
        var item = Mapper.ToProviderItem(Book, includeAssets: true);

        item.ProviderId.Should().Be("gutenberg");
        item.ExternalId.Should().Be("1342");
        item.MediaKind.Should().Be(ProviderMediaKind.Ebook);
        item.Metadata.Should().Be(new ProviderMetadata(
            Title: "Pride and Prejudice",
            Author: "Jane Austen",
            Description: "A novel.",
            Language: "English",
            Categories: "England -- Fiction"));
        item.Assets.Select(asset => asset.Id).Should().Equal("epub3-images", "pdf");
        item.Assets[0].Should().Be(new ProviderAsset(
            "epub3-images",
            ProviderMediaKind.Ebook,
            "EPUB3 (E-readers incl. Send-to-Kindle)",
            "application/epub+zip",
            24835578,
            IsPreferred: true));
        item.Assets[1].SourceFormat.Should().Be("application/pdf");
        item.Assets[1].IsPreferred.Should().BeFalse();
        item.Cover.Should().Be(new ProviderCover(
            new Uri("https://www.gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg"),
            "image/jpeg",
            ".jpg"));
        item.Source.Should().Be(new ProviderSourceInfo(
            ItemUrl: "https://www.gutenberg.org/ebooks/1342",
            RightsStatement: "Public domain in the USA.",
            RightsUrl: "https://www.gutenberg.org/policy/terms_of_use.html"));
        item.PartCount.Should().BeNull();
    }

    [Fact]
    public void ToProviderItem_WithoutAssets_KeepsTheSearchShape()
    {
        var item = Mapper.ToProviderItem(Book, includeAssets: false);

        item.Assets.Should().BeEmpty();
        item.Metadata.Title.Should().Be("Pride and Prejudice");
        item.Cover.Should().NotBeNull();
        item.Source!.RightsStatement.Should().Be("Public domain in the USA.");
    }

    [Fact]
    public void PlanAcquisition_WithNoAssetChosen_TakesThePreferredAsset()
    {
        var plan = Mapper.PlanAcquisition(Book, assetId: null);

        plan.ProviderId.Should().Be("gutenberg");
        plan.ExternalId.Should().Be("1342");
        plan.Asset.Id.Should().Be("epub3-images");
        plan.Asset.Kind.Should().Be(ProviderMediaKind.Ebook);
        plan.Parts.Should().ContainSingle();
        plan.Parts[0].Url.Should().Be(new Uri("https://www.gutenberg.org/ebooks/1342.epub3.images"));
        plan.Parts[0].FileExtension.Should().Be(".epub");
        plan.Parts[0].ExpectedBytes.Should().Be(24835578);
        plan.Parts[0].Label.Should().Be("EPUB3 (E-readers incl. Send-to-Kindle)");
        plan.Output.Should().Be(new ProviderOutput(".epub", "application/epub+zip", "EPUB"));
        plan.Cover.Should().NotBeNull();
        plan.Source!.ItemUrl.Should().Be("https://www.gutenberg.org/ebooks/1342");
    }

    [Fact]
    public void PlanAcquisition_WithoutAPreferredAsset_FallsBackToTheFirst()
    {
        var book = Book with { Assets = [Book.Assets[1] with { IsPreferred = false }] };

        var plan = Mapper.PlanAcquisition(book, assetId: null);

        plan.Asset.Id.Should().Be("pdf");
        plan.Output.Should().Be(new ProviderOutput(".pdf", "application/pdf", "PDF"));
    }

    [Fact]
    public void PlanAcquisition_WithANamedAsset_MatchesTheIdCaseInsensitively()
    {
        var plan = Mapper.PlanAcquisition(Book, assetId: "EPUB3-IMAGES");

        plan.Asset.Id.Should().Be("epub3-images");
    }

    [Fact]
    public void PlanAcquisition_ForAnUnknownAsset_IsAssetUnavailable()
    {
        var act = () => Mapper.PlanAcquisition(Book, assetId: "audiobook-mp3");

        var error = act.Should().Throw<ProviderException>().Which;
        error.Code.Should().Be(ProviderException.AssetUnavailable);
        error.Message.Should().Be("gutenberg item 1342 does not offer asset 'audiobook-mp3'.");
    }

    [Fact]
    public void PlanAcquisition_ForABookWithNoAssets_NamesTheEbookDefaultInTheFailure()
    {
        var act = () => Mapper.PlanAcquisition(Book with { Assets = [] }, assetId: null);

        var error = act.Should().Throw<ProviderException>().Which;
        error.Code.Should().Be(ProviderException.AssetUnavailable);
        error.Message.Should().Be("gutenberg item 1342 does not offer asset 'ebook'.");
    }
}

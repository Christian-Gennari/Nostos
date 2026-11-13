using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Litteraturbanken;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The extracted Litteraturbanken catalog vocabulary: query construction, Lucene
/// escaping, id guards, row parsing, the rights filter and the paging cursor.
/// </summary>
public sealed class LitteraturbankenCatalogTests
{
    [Fact]
    public void BuildSearchQuery_IsTheBoundedFieldQueryTheSourceAccepts()
    {
        var query = LitteraturbankenCatalog.BuildSearchQuery("Kåtornas folk");

        query.Should().Be(
            "@type=cross_fields @default_operator=AND @fields=autocomplete.scandinavian "
            + "Kåtornas folk AND searchable:true AND show:true AND "
            + "(license:cc-0 OR license:pd OR license:cc-by)");
    }

    [Fact]
    public void BuildSearchQuery_EscapesEveryLuceneSpecialCharacter()
    {
        var query = LitteraturbankenCatalog.BuildSearchQuery(@"a:b/c + (d)");

        query.Should().Contain(@"autocomplete.scandinavian a\:b\/c \+ \(d\) AND");
    }

    [Fact]
    public void BuildSnapshotQuery_StartsAtTheBeginningOrResumesAfterAWorkId()
    {
        LitteraturbankenCatalog.BuildSnapshotQuery(null).Should().Be(
            "searchable:true AND show:true AND (license:cc-0 OR license:pd OR license:cc-by)");
        LitteraturbankenCatalog.BuildSnapshotQuery("lb1100").Should().Be(
            "searchable:true AND show:true AND (license:cc-0 OR license:pd OR license:cc-by) AND lbworkid:>=lb1100");
    }

    [Fact]
    public void BuildItemQuery_PinsTheWorkIdAndTheRightsFilter()
    {
        LitteraturbankenCatalog.BuildItemQuery("lb100").Should().Be(
            "lbworkid:lb100 AND searchable:true AND show:true AND "
            + "(license:cc-0 OR license:pd OR license:cc-by)");
    }

    [Theory]
    [InlineData("lb100", "lb100")]
    [InlineData("  lb100  ", "lb100")]
    [InlineData("lb200/other", null)]
    [InlineData("LB100", null)]
    [InlineData("lb", null)]
    [InlineData("lb12a", null)]
    [InlineData("100", null)]
    [InlineData("lb1234567890123456789012345678901", null)]
    [InlineData(null, null)]
    public void NormalizeExternalId_AcceptsOnlyAddressableItemIds(string? value, string? expected)
    {
        LitteraturbankenCatalog.NormalizeExternalId(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("lb100", "lb100")]
    [InlineData("lb0889slfmxtqsqq3m", "lb0889slfmxtqsqq3m")]
    [InlineData("lb1-2", null)]
    [InlineData("lb", null)]
    [InlineData("LB100", null)]
    [InlineData("100", null)]
    public void NormalizeWorkKey_WalksPastIdsThatAreNotOfferedAsItems(string? value, string? expected)
    {
        LitteraturbankenCatalog.NormalizeWorkKey(value).Should().Be(expected);
    }

    [Fact]
    public void ToRecord_MapsARowToTheItemFields()
    {
        var records = LitteraturbankenFixtures.Records(LitteraturbankenFixtures.TwoFormatCatalogJson);

        records.Should().HaveCount(2);
        var record = records[0];
        record.ExternalId.Should().Be("lb100");
        record.Title.Should().Be("Kåtornas folk");
        record.ShortTitle.Should().Be("Kåtornas folk");
        record.TitleId.Should().Be("KatornasFolk");
        record.WorkTitleId.Should().Be("KatornasFolk");
        record.MediaType.Should().Be("etext");
        record.License.Should().Be("cc-0");
        record.Printed.Should().BeFalse();
        var author = record.Authors.Should().ContainSingle().Which;
        author.AuthorId.Should().Be("NordstromEB");
        author.FullName.Should().Be("Ester Blenda Nordström");
        var export = record.Exports.Should().ContainSingle().Which;
        export.Type.Should().Be("epub");
        export.Size.Should().Be(301593);
        var provenance = record.Provenance.Should().ContainSingle().Which;
        provenance.Library.Should().Be("GUB");
        provenance.Signum.Should().Be("Geogr. Sv. Lappland");
        record.SortDateImprint!.Plain.Should().Be("1916");
        record.SourceItemUrl!.ToString().Should().Be(
            "https://litteraturbanken.se/författare/NordstromEB/titlar/KatornasFolk/info");
    }

    [Fact]
    public void ToRecord_ForARowThatCannotAddressAnItem_IsNull()
    {
        LitteraturbankenCatalog.ToRecord(new CatalogRow
        {
            ExternalId = "lb100",
            Title = "A title",
            MediaType = "etext",
            Authors = [new CatalogAuthor { AuthorId = "Author" }],
            TitleId = "Title",
        }).Should().NotBeNull();

        LitteraturbankenCatalog.ToRecord(new CatalogRow())
            .Should().BeNull("there is no id, title or media type");
        LitteraturbankenCatalog.ToRecord(new CatalogRow
        {
            ExternalId = "100",
            Title = "A title",
            MediaType = "etext",
            TitleId = "Title",
            Authors = [new CatalogAuthor { AuthorId = "Author" }],
        }).Should().BeNull("the id is not an item id");
        LitteraturbankenCatalog.ToRecord(new CatalogRow
        {
            ExternalId = "lb100",
            Title = "A title",
            MediaType = "etext",
            TitleId = "Title",
        }).Should().BeNull("there is no author id");
        LitteraturbankenCatalog.ToRecord(new CatalogRow
        {
            ExternalId = "lb100",
            Title = "A title",
            MediaType = "etext",
            Authors = [new CatalogAuthor { AuthorId = "Author" }],
        }).Should().BeNull("there is no title id");
    }

    [Fact]
    public void IsRightsAllowed_RequiresAListedLicenseAndAnItemPage()
    {
        var record = LitteraturbankenFixtures.Records(LitteraturbankenFixtures.TwoFormatCatalogJson)[0];

        LitteraturbankenCatalog.IsRightsAllowed(record).Should().BeTrue();
        LitteraturbankenCatalog.IsRightsAllowed(record with { License = "lb-2-20120306" }).Should().BeFalse();
        LitteraturbankenCatalog.IsRightsAllowed(record with { License = null }).Should().BeFalse();
        LitteraturbankenCatalog.IsRightsAllowed(record with { SourceItemUrl = null }).Should().BeFalse();
    }

    [Fact]
    public void Cursor_RoundTripsTheLastWorkAndThePageCount()
    {
        var cursor = SnapshotCursor.Start.Next("lb1100");

        cursor.Encode().Should().Be("v2|lb1100|1");
        var decoded = SnapshotCursor.Decode("v2|lb1100|1", "litteraturbanken");
        decoded.AfterWorkId.Should().Be("lb1100");
        decoded.PagesRead.Should().Be(1);
        decoded.Next("lb1200").Encode().Should().Be("v2|lb1200|2");

        SnapshotCursor.Start.Encode().Should().Be("v2||0");
        SnapshotCursor.Decode("v2|lb0889slfmxtqsqq3m|12", "litteraturbanken")
            .AfterWorkId.Should().Be("lb0889slfmxtqsqq3m");
    }

    [Theory]
    [InlineData("v1|lb100|1")]
    [InlineData("v2|100|1")]
    [InlineData("v2||1")]
    [InlineData("v2|lb100|0")]
    [InlineData("v2|lb100|1000")]
    [InlineData("v2|lb100|not-a-number")]
    [InlineData("v2|lb100")]
    public void Cursor_DecodeRejectsAnythingItDidNotEncode(string value)
    {
        var act = () => SnapshotCursor.Decode(value, "litteraturbanken");

        var error = act.Should().Throw<ProviderException>().Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Message.Should().Contain("the catalog cursor was invalid");
    }
}

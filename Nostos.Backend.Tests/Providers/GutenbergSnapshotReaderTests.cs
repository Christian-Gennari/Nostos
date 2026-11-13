using System.Text;
using FluentAssertions;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// The extracted Gutenberg archive reader: member selection, the ebook filter and
/// the size bounds, exercised against an archive built in memory rather than
/// through the downloader or the provider.
/// </summary>
public sealed class GutenbergSnapshotReaderTests
{
    private static GutenbergSnapshotReader CreateReader(GutenbergSnapshotLimits? limits = null) =>
        new(limits ?? new GutenbergSnapshotLimits(), GutenbergProvider.ProviderIdentifier);

    private static async Task<List<GutenbergBook>> ReadAsync(
        byte[] archive,
        GutenbergSnapshotLimits? limits = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "nostos-reader-test-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await File.WriteAllBytesAsync(path, archive);
            var books = new List<GutenbergBook>();
            await foreach (var book in CreateReader(limits).ReadAsync(path))
                books.Add(book);

            return books;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_YieldsOnlyEbookRecordsInArchiveOrder()
    {
        var archive = GutenbergFixtures.BuildArchive(
            ("cache/epub/1342/pg1342.rdf", GutenbergFixtures.Rdf("1342", "Pride and Prejudice", "Austen, Jane")),
            ("cache/epub/99999/notes.txt", "not a book"),
            ("cache/epub/3002/pg3002.rdf", GutenbergFixtures.Rdf("3002", "Society's Child", type: "Sound")),
            ("index.html", "<html/>"),
            ("cache/epub/11/pg11.rdf", GutenbergFixtures.Rdf("11", "A Record")));

        var books = await ReadAsync(archive);

        books.Select(book => book.Id).Should().Equal("1342", "11");
        books[0].Title.Should().Be("Pride and Prejudice");
        books[0].Author.Should().Be("Jane Austen");
        books[0].Assets.Should().BeEmpty("the archive carries metadata only");
        books[1].Title.Should().Be("A Record");
    }

    [Fact]
    public async Task ReadAsync_WithoutATarMember_IsAResponseInvalid()
    {
        var archive = GutenbergFixtures.BuildArchiveWithDirectory("not-the-catalogue.txt");

        var act = async () => await ReadAsync(archive);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Message.Should().Contain("the snapshot archive has no tar catalog");
    }

    [Fact]
    public async Task ReadAsync_OnABodyThatIsNotAZip_IsAResponseInvalid()
    {
        var act = async () => await ReadAsync(Encoding.UTF8.GetBytes("this is not a zip"));

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }

    [Fact]
    public async Task ReadAsync_RejectsAMemberOverTheMemberLimit()
    {
        var limits = new GutenbergSnapshotLimits { MaxMemberBytes = 512 };
        var archive = GutenbergFixtures.BuildArchive(
            ("cache/epub/1342/pg1342.rdf", GutenbergFixtures.Rdf("1342", "A title long enough to exceed the cap")));

        var act = async () => await ReadAsync(archive, limits);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
        error.Message.Should().Contain("member limit");
    }

    [Fact]
    public async Task ReadAsync_OnATruncatedRdfMember_IsAResponseInvalid()
    {
        var archive = GutenbergFixtures.BuildArchive(
            ("cache/epub/1342/pg1342.rdf", "<rdf:RDF><pgterms:ebook><dcterms:title>Broken"));

        var act = async () => await ReadAsync(archive);

        var error = (await act.Should().ThrowAsync<ProviderException>()).Which;
        error.Code.Should().Be(ProviderException.ResponseInvalid);
    }
}

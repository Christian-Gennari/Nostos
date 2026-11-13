using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Xunit;
using static Nostos.Backend.Tests.Portability.PortableArchiveTestSupport;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// An archive written before #332 can hold a book that is <c>physical</c> and
/// carries a book file. Older builds made that shape legitimately, so the
/// import must not refuse it — it restores the book as the digital book its
/// file makes it, by the same rule as the database migration. The fixture below
/// is a real exported archive whose relational payload is rewritten into the old
/// shape, so the whole path (validation, restore, row-for-row verification) runs.
/// </summary>
public sealed class PortableLegacyPhysicalFileImportTests
{
    private const string DataPath = "data/library.json";

    [Fact]
    public async Task A_physical_book_with_a_file_is_restored_as_the_digital_book_it_is()
    {
        using var archive = await ArchiveWithLegacyPhysicalBooksAsync();

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        var imported = await destination.Portability().ImportAsync(archive);

        imported.IntegrityVerified.Should().BeTrue("the archive is genuine, only older");
        destination.Db.ChangeTracker.Clear();

        var ids = await IdsAsync(destination);

        var epub = await destination.Db.Books.AsNoTracking().SingleAsync(b => b.Id == ids.Epub);
        epub.Should().BeOfType<EBookModel>("a book that holds an EPUB is an ebook");
        ((EBookModel)epub).Isbn.Should().Be("9780140449495", "the print ISBN has an ebook column");
        ((EBookModel)epub).PageCount.Should().Be(329, "and so has the page count");
        epub.FileDetails.HasFile.Should().BeTrue();
        epub.FileDetails.FileName.Should().Be("book.epub");
        epub.Progress.LastLocation.Should().Be("{\"pageNumber\":74,\"yPercent\":0,\"rects\":[]}",
            "the reader's place is restored exactly");

        var audio = await destination.Db.Books.AsNoTracking().SingleAsync(b => b.Id == ids.Audio);
        audio.Should().BeOfType<AudioBookModel>();
        ((AudioBookModel)audio).Asin.Should().Be("B000000001");
        audio.NormalizedIsbn.Should().BeNull("an audiobook never carries ISBN identity");
        audio.FileDetails.HasFile.Should().BeTrue();

        var physical = await destination.Db.Books.AsNoTracking().SingleAsync(b => b.Id == ids.Physical);
        physical.Should().BeOfType<PhysicalBookModel>("a metadata-only paper book is left alone");
        physical.FileDetails.HasFile.Should().BeFalse();

        (await destination.Db.Notes.CountAsync()).Should().Be(1, "notes travel with the book");
    }

    [Fact]
    public async Task A_paper_page_on_a_book_that_has_a_file_imports_as_a_digital_book_with_its_place_kept()
    {
        // The migration leaves this shape alone, because moving someone's saved
        // place is the one thing it must not guess. A restore has no such
        // option: it may neither refuse the archive nor write a physical book
        // that holds a file, so it converts and keeps the position as written.
        using var archive = await ArchiveWithLegacyPhysicalBooksAsync(paperProgress: true);

        await using var destination = await LocalPortableTestLibrary.CreateAsync();
        var imported = await destination.Portability().ImportAsync(archive);

        imported.IntegrityVerified.Should().BeTrue();
        destination.Db.ChangeTracker.Clear();

        var ids = await IdsAsync(destination);
        var pdf = await destination.Db.Books.AsNoTracking().SingleAsync(b => b.Id == ids.Pdf);
        pdf.Should().BeOfType<EBookModel>();
        pdf.Progress.LastLocation.Should().Be("{\"page\":73}");
        pdf.FileDetails.FileName.Should().Be("book.pdf");
    }

    // ------------------------------------------------------------------

    private sealed record FixtureIds(Guid Epub, Guid Pdf, Guid Audio, Guid Physical);

    private static async Task<FixtureIds> IdsAsync(LocalPortableTestLibrary library)
    {
        var books = await library.Db.Books.AsNoTracking().ToListAsync();
        return new FixtureIds(
            books.Single(b => b.FileDetails.FileName == "book.epub").Id,
            books.Single(b => b.FileDetails.FileName == "book.pdf").Id,
            books.Single(b => b is AudioBookModel).Id,
            books.Single(b => b is PhysicalBookModel).Id);
    }

    /// <summary>
    /// The representative fixture's archive, rewritten so an EPUB book and an
    /// audio book are labelled physical and carry the print edition's ISBN and
    /// page count — the shape older builds wrote.
    /// </summary>
    private static async Task<MemoryStream> ArchiveWithLegacyPhysicalBooksAsync(bool paperProgress = false)
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage);

        using var exported = new MemoryStream();
        await source.Portability().ExportAsync(exported);

        var entries = await ReadEntriesAsync(new MemoryStream(exported.ToArray()));
        MutateJsonEntry(entries, DataPath, root =>
        {
            var books = root["books"]!.AsArray();

            var epub = Book(books, ids.EpubBookId);
            epub["type"] = "physical";
            epub["isbn"] = "9780140449495";
            epub["pageCount"] = 329;
            // Read as a file, in the PDF reader's own format.
            epub["progress"]!["lastLocation"] = "{\"pageNumber\":74,\"yPercent\":0,\"rects\":[]}";

            var audio = Book(books, ids.AudioBookId);
            audio["type"] = "physical";
            audio["isbn"] = "9780143036272";
            audio["pageCount"] = 176;

            var pdf = Book(books, ids.PdfBookId);
            pdf["type"] = "physical";
            pdf["isbn"] = "9781108040365";
            pdf["pageCount"] = 634;
            if (paperProgress)
                pdf["progress"]!["lastLocation"] = "{\"page\":73}";
        });
        RehashDataDescriptor(entries);

        return await BuildArchiveAsync(entries);
    }

    private static JsonObject Book(JsonArray books, Guid id) =>
        books.Single(book => book!["id"]!.GetValue<Guid>() == id)!.AsObject();
}

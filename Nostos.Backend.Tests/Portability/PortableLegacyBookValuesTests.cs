using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// An archive written before rating, favourite and review moved onto the work
/// kept them on every edition. Reading it must lose none of them: it goes
/// through the same roll-up as the migration and as "link as one book".
/// </summary>
public sealed class PortableLegacyBookValuesTests
{
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_old_archive_with_disagreeing_editions_keeps_both_values()
    {
        var work = Guid.NewGuid();
        var older = Edition(work, rating: 4, review: "Older words", favourite: false, lastRead: Jan);
        var newer = Edition(work, rating: 2, review: "Newer words", favourite: true, lastRead: Jun);

        var current = PortableLegacyBookValues.ToCurrent(Data(5, [work], [older, newer]));

        var book = current.Works.Single();
        (book.Rating, book.PersonalReview, book.IsFavorite).Should().Be((2, "Newer words", true));
        var olderNow = current.Books.Single(b => b.Id == older.Id).Progress;
        olderNow.Rating.Should().Be(4, "the edition that lost keeps its rating as an exception");
        olderNow.PersonalReview.Should().Be("Older words");
        var newerNow = current.Books.Single(b => b.Id == newer.Id).Progress;
        (newerNow.Rating, newerNow.PersonalReview).Should().Be((0, null));
        current.Books.Should().OnlyContain(b => !b.Progress.IsFavorite, "the favourite is the book's now");
        current.Version.Should().Be(5, "the declared version still agrees with the manifest");
    }

    [Fact]
    public void An_old_archive_with_one_rated_edition_gives_the_book_that_rating()
    {
        var work = Guid.NewGuid();
        var rated = Edition(work, rating: 5, review: null, favourite: false, lastRead: null);
        var bare = Edition(work, rating: 0, review: null, favourite: false, lastRead: Jun);

        var current = PortableLegacyBookValues.ToCurrent(Data(4, [work], [rated, bare]));

        current.Works.Single().Rating.Should().Be(5);
        current.Books.Should().OnlyContain(b => b.Progress.Rating == 0);
    }

    [Fact]
    public void A_current_archive_is_left_exactly_as_it_is()
    {
        var work = Guid.NewGuid();
        var edition = Edition(work, rating: 3, review: null, favourite: false, lastRead: Jan);
        var data = Data(PortableArchiveFormat.DataVersion, [work], [edition]);

        PortableLegacyBookValues.ToCurrent(data).Should().BeSameAs(data);
    }

    [Fact]
    public void A_current_archive_with_values_on_a_work_below_version_six_is_refused()
    {
        var work = Guid.NewGuid();
        var data = Data(5, [work], [Edition(work, 0, null, false, null)]);
        data = data with { Works = [data.Works.Single() with { Rating = 4 }] };

        var act = () => PortableArchiveValidation.ValidatePortableData(data);

        act.Should().Throw<PortableArchiveException>().Which.Code.Should().Be("unexpected_version_data");
    }

    [Fact]
    public void A_version_six_archive_that_still_carries_an_edition_favourite_is_refused()
    {
        var work = Guid.NewGuid();
        var data = Data(6, [work], [Edition(work, 0, null, favourite: true, lastRead: null)]);

        var act = () => PortableArchiveValidation.ValidatePortableData(data);

        act.Should().Throw<PortableArchiveException>().Which.Code.Should().Be("unexpected_version_data");
    }

    private static PortableBook Edition(Guid work, int rating, string? review, bool favourite, DateTime? lastRead) =>
        new(
            Guid.NewGuid(), work, "ebook", nameof(BookStatus.Ready), null, "Book", null,
            new PortableBookMetadata(null, null, null, null, null, null, null, null, null, null, null, null),
            new PortableReadingProgress(null, 0, rating, favourite, review, lastRead, null),
            Jan, null, null, null, null, null, null, false, false);

    private static PortableLibraryData Data(int version, Guid[] works, PortableBook[] books) =>
        new(
            version,
            works.Select(id => new PortableWork(id, "Book", null, Jan)).ToList(),
            books.ToList(),
            [], [], [], [], [], [], [], null,
            WritingNotes: [], NoteImportBookLinks: [], ReadingListEntries: []);
}

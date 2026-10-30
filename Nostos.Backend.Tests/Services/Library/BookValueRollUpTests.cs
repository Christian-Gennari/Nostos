using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// The one rule for combining what is said about a book when parties become one
/// book: agreeing or one-sided values become the book's, differing values go to
/// the party read most recently while the others keep theirs as edition
/// exceptions, and the favourite is on if any party has it on.
/// </summary>
public sealed class BookValueRollUpTests
{
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BookValueSide Side(int rating = 0, string? review = null, bool favourite = false,
        DateTime? lastRead = null, DateTime? created = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), lastRead, created ?? Jan, rating, review, favourite);

    [Fact]
    public void Values_that_agree_become_the_books_and_nobody_is_a_loser()
    {
        var result = BookValueRollUp.Combine([Side(rating: 4, review: "Same"), Side(rating: 4, review: "Same")]);

        result.Rating.Should().Be(4);
        result.Review.Should().Be("Same");
        result.RatingLosers.Should().BeEmpty();
        result.ReviewLosers.Should().BeEmpty();
    }

    [Fact]
    public void A_value_only_one_side_has_becomes_the_books()
    {
        var result = BookValueRollUp.Combine([Side(), Side(rating: 3, review: "Only mine", lastRead: Jan)]);

        result.Rating.Should().Be(3);
        result.Review.Should().Be("Only mine");
        result.RatingLosers.Should().BeEmpty("the other side said nothing, so nothing is displaced");
    }

    [Fact]
    public void Values_that_differ_go_to_the_side_read_most_recently_and_the_other_is_kept()
    {
        var older = Side(rating: 4, review: "Older", lastRead: Jan);
        var newer = Side(rating: 2, review: "Newer", lastRead: Jun);

        var result = BookValueRollUp.Combine([older, newer]);

        result.Rating.Should().Be(2);
        result.Review.Should().Be("Newer");
        result.RatingLosers.Should().BeEquivalentTo([older.Id]);
        result.ReviewLosers.Should().BeEquivalentTo([older.Id]);
    }

    [Fact]
    public void A_side_that_was_read_beats_one_that_never_was()
    {
        var read = Side(rating: 3, lastRead: Jan, created: Jan);
        var unread = Side(rating: 5, lastRead: null, created: Jun);

        BookValueRollUp.Combine([unread, read]).Rating.Should().Be(3);
    }

    [Fact]
    public void Ties_fall_to_the_newest_then_the_lowest_id_so_the_answer_is_deterministic()
    {
        var older = Side(rating: 1, created: Jan, id: Guid.Parse("00000000-0000-0000-0000-000000000009"));
        var newer = Side(rating: 2, created: Jun, id: Guid.Parse("00000000-0000-0000-0000-000000000008"));
        BookValueRollUp.Combine([older, newer]).Rating.Should().Be(2, "same read state, newest first");

        var low = Side(rating: 4, created: Jan, id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var high = Side(rating: 5, created: Jan, id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        BookValueRollUp.Combine([high, low]).Rating.Should().Be(4);
        BookValueRollUp.Combine([low, high]).Rating.Should().Be(4, "the order of the input does not matter");
    }

    [Fact]
    public void The_favourite_is_on_if_any_side_has_it_on()
    {
        BookValueRollUp.Combine([Side(favourite: false), Side(favourite: true)]).IsFavorite.Should().BeTrue();
        BookValueRollUp.Combine([Side(), Side()]).IsFavorite.Should().BeFalse();
    }

    [Fact]
    public void Merge_keeps_a_losers_value_on_its_editions_unless_they_already_hold_their_own()
    {
        var plain = new EBookModel { Title = "T" };
        var holdsOwn = new PhysicalBookModel { Title = "T" };
        holdsOwn.Progress.Rating = 1;
        var winnerEdition = new AudioBookModel { Title = "T" };
        var survivor = new WorkModel { Title = "T" };

        var loser = Side(rating: 4, review: "Loser's words", lastRead: Jan);
        var winner = Side(rating: 2, review: "Winner's words", lastRead: Jun);
        BookValueRollUp.Merge(
            survivor,
            [
                new BookValueParty(loser, [plain, holdsOwn]),
                new BookValueParty(winner, [winnerEdition]),
            ],
            [plain, holdsOwn, winnerEdition]);

        survivor.Rating.Should().Be(2);
        survivor.PersonalReview.Should().Be("Winner's words");
        plain.Progress.Rating.Should().Be(4);
        plain.Progress.PersonalReview.Should().Be("Loser's words");
        holdsOwn.Progress.Rating.Should().Be(1, "an edition that already had its own keeps it");
        winnerEdition.Progress.Rating.Should().Be(0, "an edition value equal to the book's is no exception");
    }

    [Fact]
    public void Merge_clears_an_edition_value_equal_to_the_books()
    {
        var edition = new EBookModel { Title = "T" };
        edition.Progress.Rating = 5;
        edition.Progress.PersonalReview = "Same";
        var survivor = new WorkModel { Title = "T", Rating = 5, PersonalReview = "Same" };

        BookValueRollUp.Merge(
            survivor,
            [new BookValueParty(BookValueRollUp.WorkSide(survivor, [edition]), [edition])],
            [edition]);

        edition.Progress.Rating.Should().Be(0);
        edition.Progress.PersonalReview.Should().BeNull();
        survivor.Rating.Should().Be(5);
    }
}

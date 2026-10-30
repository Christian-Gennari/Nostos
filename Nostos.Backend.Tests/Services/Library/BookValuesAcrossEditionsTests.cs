using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// What is said about a book (rating, favourite, review) belongs to the book,
/// so every edition of it shows the same values; an edition can still hold its
/// own rating or review as an exception. Reading status is calculated from all
/// the editions and never stored.
/// </summary>
public sealed class BookValuesAcrossEditionsTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";

    private readonly SqliteTestFixture _fixture;

    public BookValuesAcrossEditionsTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // The book's values are shared; the edition's are exceptions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Rating_favourite_and_review_written_on_one_edition_show_on_every_edition()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");

        var baseline = await VersionAsync(h);
        var result = await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), epub, Rating: 5, IsFavorite: true, PersonalReview: "A labyrinth."));

        long.Parse(result.StateVersion).Should().Be(baseline + 1, "the write moves the state version");
        foreach (var id in new[] { epub, audio })
        {
            var book = await GetAsync(h, id);
            book.Rating.Should().Be(5);
            book.IsFavorite.Should().BeTrue();
            book.PersonalReview.Should().Be("A labyrinth.");
            book.EditionRating.Should().Be(0, "nothing was written as an exception");
        }

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Books.AsNoTracking().Where(b => b.Progress.Rating != 0).CountAsync()).Should().Be(0,
            "the book's rating lives on the work, not on the edition");
        (await db.Works.AsNoTracking().SingleAsync()).Rating.Should().Be(5);
    }

    [Fact]
    public async Task An_edition_rating_is_an_exception_that_clears_with_zero()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), epub, Rating: 4));

        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), audio, EditionRating: 2));

        var narrated = await GetAsync(h, audio);
        narrated.Rating.Should().Be(4, "the book's rating is unchanged");
        narrated.EditionRating.Should().Be(2);
        (await GetAsync(h, epub)).EditionRating.Should().Be(0);
        narrated.OtherEditions!.Single(e => e.Id == epub).Rating.Should().Be(0);
        (await GetAsync(h, epub)).OtherEditions!.Single(e => e.Id == audio).Rating.Should().Be(2);

        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), audio, EditionRating: 0));
        (await GetAsync(h, audio)).EditionRating.Should().Be(0);
    }

    [Fact]
    public async Task An_edition_review_is_an_exception_that_clears_with_an_empty_string()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), epub, PersonalReview: "A labyrinth."));

        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), audio, EditionReview: "The narrator hurries."));

        var narrated = await GetAsync(h, audio);
        narrated.PersonalReview.Should().Be("A labyrinth.", "the book's review is unchanged");
        narrated.EditionReview.Should().Be("The narrator hurries.");
        (await GetAsync(h, epub)).EditionReview.Should().BeNull();
        (await GetAsync(h, epub)).OtherEditions!.Single(e => e.Id == audio).PersonalReview
            .Should().Be("The narrator hurries.");

        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), audio, Rating: 3));
        (await GetAsync(h, audio)).EditionReview.Should().Be("The narrator hurries.", "null leaves it");

        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), audio, EditionReview: ""));
        var cleared = await GetAsync(h, audio);
        cleared.EditionReview.Should().BeNull();
        cleared.PersonalReview.Should().Be("A labyrinth.");
    }

    [Fact]
    public async Task A_card_keeps_the_books_values_when_another_edition_is_read()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), epub, Rating: 5, IsFavorite: true, PersonalReview: "Review"));
        await h.Service.UpdateProgressAsync(epub, "cfi", 30);

        var before = (await GroupedAsync(h, BookFilter.All)).Single();
        before.Id.Should().Be(epub, "the edition read most recently stands for the book");

        await Task.Delay(5);
        await h.Service.UpdateProgressAsync(audio, "10", 10);

        var after = (await GroupedAsync(h, BookFilter.All)).Single();
        after.Id.Should().Be(audio, "the card now shows the other edition");
        (after.Rating, after.IsFavorite, after.PersonalReview)
            .Should().Be((before.Rating, before.IsFavorite, before.PersonalReview));
    }

    [Fact]
    public async Task Rating_sort_and_favourite_filter_use_the_books_values_in_both_lists()
    {
        var h = Harness();
        var low = await CreateAsync(h, "ebook", "Low", "A");
        var lowAudio = await CreateAsync(h, "audiobook", "Low", "A");
        var high = await CreateAsync(h, "ebook", "High", "B");
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), low, Rating: 2, IsFavorite: true));
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), high, Rating: 5));

        var grouped = await GroupedAsync(h, BookFilter.All, BookSort.Rating);
        grouped.Select(b => b.Title).Should().Equal("High", "Low");

        var flat = ((PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.Favorites, BookSort.Rating, null, 1, 50, null, groupByWork: false)).Data!).Items.ToList();
        flat.Select(b => b.Id).Should().BeEquivalentTo([low, lowAudio],
            "the audiobook is a favourite too: the favourite is the book's");
        (await GroupedAsync(h, BookFilter.Favorites)).Should().ContainSingle().Which.Title.Should().Be("Low");

        var counts = (LibraryStatusCountsDto)(await h.Service.GetStatusCountsAsync()).Data!;
        counts.Favorites.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Creating a book
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_rating_favourite_and_review_on_a_new_book_go_to_its_new_work()
    {
        var h = Harness();
        var id = await CreateAsync(h, "ebook", "Fresh", "Writer", rating: 4, favourite: true, review: "Good.");

        await using var db = await h.Factory.CreateDbContextAsync();
        var book = await db.Books.AsNoTracking().SingleAsync(b => b.Id == id);
        (book.Work!.Rating, book.Work.IsFavorite, book.Work.PersonalReview).Should().Be((4, true, "Good."));
        book.Progress.Rating.Should().Be(0);
        book.Progress.PersonalReview.Should().BeNull();
    }

    [Fact]
    public async Task A_new_edition_joining_a_work_uses_the_shared_rule()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges", rating: 4);
        await h.Service.UpdateProgressAsync(epub, "cfi", 5);

        // One side has a value: the book keeps it. A favourite is added, never lost.
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges", favourite: true);
        var joined = await GetAsync(h, audio);
        (joined.Rating, joined.IsFavorite, joined.EditionRating).Should().Be((4, true, 0));

        // Disagreement: the epub was read, the new audiobook was not, so the epub's 4 wins
        // and the new edition keeps its own 2 as an exception.
        var physical = await CreateAsync(h, "physical", "Fictions", "Borges", rating: 2);
        var third = await GetAsync(h, physical);
        (third.Rating, third.EditionRating).Should().Be((4, 2));
    }

    // ------------------------------------------------------------------
    // Link and unlink lose nothing
    // ------------------------------------------------------------------

    [Fact]
    public async Task Linking_two_books_that_disagree_keeps_both_values_and_unlinking_splits_them_sensibly()
    {
        var h = Harness();
        var a = await CreateAsync(h, "ebook", "Fictions", "Borges", rating: 4, review: "From A", favourite: true);
        var b = await CreateAsync(h, "audiobook", "Ficciones", "J. L. Borges", rating: 2, review: "From B");
        await h.Service.UpdateProgressAsync(a, "cfi", 20);
        await Task.Delay(5);
        await h.Service.UpdateProgressAsync(b, "10", 10);

        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), a, b));

        // B was read most recently, so its words are the book's; A's survive on A.
        var linkedA = await GetAsync(h, a);
        var linkedB = await GetAsync(h, b);
        (linkedA.Rating, linkedA.PersonalReview, linkedA.IsFavorite).Should().Be((2, "From B", true));
        (linkedA.EditionRating, linkedA.EditionReview).Should().Be((4, "From A"));
        (linkedB.Rating, linkedB.EditionRating, linkedB.EditionReview).Should().Be((2, 0, null));

        // Unlink A: it takes its own exception as the new book's values; the favourite is copied.
        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), a));
        var afterA = await GetAsync(h, a);
        var afterB = await GetAsync(h, b);
        (afterA.Rating, afterA.PersonalReview, afterA.IsFavorite).Should().Be((4, "From A", true));
        (afterA.EditionRating, afterA.EditionReview).Should().Be((0, null), "its own values became the book's");
        (afterB.Rating, afterB.PersonalReview, afterB.IsFavorite).Should().Be((2, "From B", true),
            "the old work is unchanged");
    }

    [Fact]
    public async Task Link_then_unlink_round_trip_loses_no_rating_and_no_review()
    {
        var h = Harness();
        var a = await CreateAsync(h, "ebook", "One", "X", rating: 5, review: "Five stars", favourite: true);
        var b = await CreateAsync(h, "ebook", "Two", "Y", rating: 3, review: "Three stars");

        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), a, b));
        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), a));
        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), b));

        // Re-reading every value that exists anywhere: nothing a person wrote is gone.
        await using var db = await h.Factory.CreateDbContextAsync();
        var ratings = (await db.Works.AsNoTracking().Select(w => w.Rating).ToListAsync())
            .Concat(await db.Books.AsNoTracking().Select(x => x.Progress.Rating).ToListAsync())
            .Where(r => r > 0).Distinct().ToList();
        ratings.Should().BeEquivalentTo([5, 3]);
        var reviews = (await db.Works.AsNoTracking().Select(w => w.PersonalReview).ToListAsync())
            .Concat(await db.Books.AsNoTracking().Select(x => x.Progress.PersonalReview).ToListAsync())
            .Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
        reviews.Should().BeEquivalentTo(["Five stars", "Three stars"]);
        (await db.Works.AsNoTracking().AnyAsync(w => w.IsFavorite)).Should().BeTrue();
    }

    [Fact]
    public async Task Unlinking_an_edition_takes_its_own_rating_else_the_old_works_and_copies_the_favourite()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), epub, Rating: 4, IsFavorite: true, PersonalReview: "Shared"));
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(Client, Key(), audio, EditionRating: 1));

        var baseline = await VersionAsync(h);
        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), audio));

        var split = await GetAsync(h, audio);
        (split.Rating, split.IsFavorite, split.PersonalReview, split.EditionRating)
            .Should().Be((1, true, "Shared", 0));
        (await GetAsync(h, epub)).Rating.Should().Be(4, "the old work is unchanged");
        (await VersionAsync(h)).Should().Be(baseline + 1);
    }

    // ------------------------------------------------------------------
    // Reading status is the book's, calculated from its editions
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_finished_epub_with_an_untouched_audiobook_is_finished_and_not_not_started()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateProgressAsync(epub, "cfi", 100);

        (await GroupedAsync(h, BookFilter.Finished)).Should().HaveCount(1);
        (await GroupedAsync(h, BookFilter.NotStarted)).Should().BeEmpty();
        (await GroupedAsync(h, BookFilter.Reading)).Should().BeEmpty();

        var counts = (LibraryStatusCountsDto)(await h.Service.GetStatusCountsAsync()).Data!;
        (counts.Finished, counts.NotStarted, counts.Reading).Should().Be((1, 0, 0));
        await AssertInMemoryRuleAgreesAsync(h);
    }

    [Fact]
    public async Task Sixty_percent_of_the_audiobook_and_then_a_finished_epub_is_not_reading()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateProgressAsync(audio, "60", 60);
        await Task.Delay(5);
        await h.Service.UpdateProgressAsync(epub, "cfi", 100);

        (await GroupedAsync(h, BookFilter.Reading)).Should().BeEmpty(
            "the unfinished audiobook was left before the epub was finished");
        (await GroupedAsync(h, BookFilter.Finished)).Should().HaveCount(1);
        (await GroupedAsync(h, BookFilter.NotStarted)).Should().BeEmpty();
        await AssertInMemoryRuleAgreesAsync(h);
    }

    [Fact]
    public async Task A_reread_is_both_reading_and_finished()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateProgressAsync(epub, "cfi", 100);
        await Task.Delay(5);
        await h.Service.UpdateProgressAsync(audio, "10", 10);

        (await GroupedAsync(h, BookFilter.Reading)).Should().HaveCount(1);
        (await GroupedAsync(h, BookFilter.Finished)).Should().HaveCount(1);
        (await GroupedAsync(h, BookFilter.NotStarted)).Should().BeEmpty();

        var counts = (LibraryStatusCountsDto)(await h.Service.GetStatusCountsAsync()).Data!;
        (counts.Finished, counts.Reading, counts.NotStarted).Should().Be((1, 1, 0));
        await AssertInMemoryRuleAgreesAsync(h);
    }

    [Fact]
    public async Task An_untouched_book_is_not_started_and_the_flat_list_keeps_per_edition_filters()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await CreateAsync(h, "ebook", "Untouched", "Z");
        await h.Service.UpdateProgressAsync(epub, "cfi", 100);

        (await GroupedAsync(h, BookFilter.NotStarted)).Select(b => b.Title).Should().Equal("Untouched");

        // Flat list: editions, each filtered on its own progress, but with the book's values.
        var flat = ((PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.NotStarted, BookSort.Title, null, 1, 50, null, groupByWork: false)).Data!).Items.ToList();
        flat.Select(b => b.Title).Should().BeEquivalentTo(["Fictions", "Untouched"]);
        flat.Should().Contain(b => b.Id == audio, "the untouched audiobook is its own entry in the flat list");
        flat.Should().NotContain(b => b.Id == epub);
    }

    [Fact]
    public async Task Reset_with_all_editions_starts_every_edition_over()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");
        await h.Service.UpdateProgressAsync(epub, "cfi", 100);
        await h.Service.UpdateProgressAsync(audio, "10", 10);

        await h.Service.ResetProgressAsync(epub);
        (await GetAsync(h, audio)).ProgressPercent.Should().Be(10, "one edition only by default");

        var baseline = await VersionAsync(h);
        await h.Service.ResetProgressAsync(epub, allEditions: true);

        (await GetAsync(h, audio)).ProgressPercent.Should().Be(0);
        (await GetAsync(h, audio)).LastReadAt.Should().BeNull();
        (await VersionAsync(h)).Should().Be(baseline + 1);
        (await GroupedAsync(h, BookFilter.NotStarted)).Should().HaveCount(1);

        (await VersionAsync(h)).Should().Be(baseline + 1);
        await h.Service.ResetProgressAsync(epub, allEditions: true);
        (await VersionAsync(h)).Should().Be(baseline + 1, "resetting what is already reset changes nothing");
    }

    // ------------------------------------------------------------------
    // The mapper never reports a rating it cannot know
    // ------------------------------------------------------------------

    [Fact]
    public void Mapping_a_book_without_its_work_fails_loudly_instead_of_reporting_zero()
    {
        var book = new EBookModel { Title = "No work" };

        var act = () => book.ToDto();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Work*");
    }

    [Fact]
    public async Task Books_read_from_the_context_always_carry_their_work()
    {
        var h = Harness();
        var id = await CreateAsync(h, "ebook", "Fictions", "Borges", rating: 3);

        await using var db = await h.Factory.CreateDbContextAsync();
        var plain = await db.Books.AsNoTracking().SingleAsync(b => b.Id == id);

        plain.Work.Should().NotBeNull("Work is auto-included");
        plain.ToDto().Rating.Should().Be(3);
    }

    // ------------------------------------------------------------------
    // Undo restores the book's values on the work
    // ------------------------------------------------------------------

    [Fact]
    public async Task Undo_puts_the_books_values_back_on_the_work_even_with_two_editions_changed()
    {
        var h = Harness();
        var epub = await CreateAsync(h, "ebook", "Fictions", "Borges", rating: 2, review: "Before");
        var audio = await CreateAsync(h, "audiobook", "Fictions", "Borges");

        var applied = await h.Service.ApplyAsync(
        [
            new UpdateBookOp(epub, Rating: 5, IsFavorite: true, PersonalReview: "After"),
            new UpdateBookOp(audio, Rating: 5),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var changed = await GetAsync(h, audio);
        (changed.Rating, changed.IsFavorite, changed.PersonalReview).Should().Be((5, true, "After"));

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().BeEmpty("two editions of one book share the values, so restoring one is not a clash");
        foreach (var id in new[] { epub, audio })
        {
            var book = await GetAsync(h, id);
            (book.Rating, book.IsFavorite, book.PersonalReview).Should().Be((2, false, "Before"));
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task AssertInMemoryRuleAgreesAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        var books = await db.Books.AsNoTracking().ToListAsync();
        foreach (var work in books.GroupBy(b => b.WorkId))
        {
            var (finished, reading, notStarted) = WorkReadingStatus.Of(work.ToList());
            (await GroupedAsync(h, BookFilter.Finished)).Any(b => b.WorkId == work.Key).Should().Be(finished);
            (await GroupedAsync(h, BookFilter.Reading)).Any(b => b.WorkId == work.Key).Should().Be(reading);
            (await GroupedAsync(h, BookFilter.NotStarted)).Any(b => b.WorkId == work.Key).Should().Be(notStarted);
        }
    }

    private static async Task<List<BookDto>> GroupedAsync(
        TestHarness h, BookFilter filter, BookSort sort = BookSort.Title) =>
        ((PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            filter, sort, null, 1, 50, null, groupByWork: true)).Data!).Items.ToList();

    private static async Task<Guid> CreateAsync(
        TestHarness h, string type, string title, string author,
        int rating = 0, bool favourite = false, string? review = null)
    {
        var result = await h.Service.CreateOrMatchBookAsync(new LibraryCreateBookRequest(
            Client, Key(), type, title, Author: author,
            Rating: rating, IsFavorite: favourite, PersonalReview: review), strictConfirmation: false);
        var outcome = (LibraryCreateOrMatchResultDto)result.Data!;
        outcome.Outcome.Should().Be("created");
        return outcome.BookId!.Value;
    }

    private static async Task<BookDto> GetAsync(TestHarness h, Guid id) =>
        (BookDto)(await h.Service.GetBookAsync(id)).Data!;

    private static async Task<long> VersionAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return long.Parse((await db.LibraryStates.AsNoTracking().SingleOrDefaultAsync())?.StateVersion ?? "0");
    }

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private TestHarness Harness()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_fixture.CreateDatabasePath()}")
            .Options;
        using (var bootstrap = new NostosDbContext(options))
            bootstrap.Database.EnsureCreated();

        var factory = new TestContextFactory(options);
        var service = new LibraryService(factory,
            new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance));
        return new TestHarness(factory, service);
    }

    private sealed record TestHarness(IDbContextFactory<NostosDbContext> Factory, LibraryService Service);

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);
        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

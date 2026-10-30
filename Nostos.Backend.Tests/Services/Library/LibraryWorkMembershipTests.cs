using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Manual multi-edition work membership (issue #143). Automatic grouping by
/// normalized title+author stays the default and is covered elsewhere; these
/// tests are the explicit user override for when the heuristic is wrong.
///
/// The semantics under test, in one place:
///   link   — MERGE the two work groups (target's work survives), so nothing is
///            silently pulled out of an already-valid group;
///   unlink — SPLIT one book into a NEW work carrying its own title/author, so
///            it never ends up with a null/empty WorkId;
///   both   — touch only WorkId on the book rows, and clean up a work whose last
///            book left it.
/// </summary>
public sealed class LibraryWorkMembershipTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";

    private readonly SqliteTestFixture _fixture;

    public LibraryWorkMembershipTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // Link: singleton → group
    // ------------------------------------------------------------------

    [Fact]
    public async Task Link_two_books_with_different_title_and_author_groups_them()
    {
        var h = Harness();
        var borges = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var unrelated = await CreateBookAsync(h, "ebook", "Something Else Entirely", "Another Author");

        await using (var before = await h.Factory.CreateDbContextAsync())
        {
            var first = await before.Books.SingleAsync(b => b.Id == borges);
            var second = await before.Books.SingleAsync(b => b.Id == unrelated);
            first.WorkId.Should().NotBe(second.WorkId, "the metadata does not match, so grouping is manual");
        }

        var baseline = await VersionAsync(h);
        var result = await h.Service.LinkWorkAsync(
            new LibraryLinkWorkRequest(Client, Key(), unrelated, borges));

        Version(result).Should().Be(baseline + 1);
        var data = (LibraryWorkMembershipResultDto)result.Data!;
        data.WorkId.Should().Be(await WorkIdOfAsync(h, borges), "the target's work is the survivor");
        data.EditionCount.Should().Be(2);
        data.RemovedWorkId.Should().NotBeNull("the source work was left empty and must be cleaned up");

        await using var db = await h.Factory.CreateDbContextAsync();
        var editions = await db.Books.Where(b => b.WorkId == data.WorkId).ToListAsync();
        editions.Should().HaveCount(2);
        editions.Select(b => b.Id).Should().BeEquivalentTo([borges, unrelated]);
    }

    [Fact]
    public async Task Link_shows_up_through_otherEditions_on_both_books()
    {
        var h = Harness();
        var first = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var second = await CreateBookAsync(h, "ebook", "Fictions", "Borges, Jorge Luis");

        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), second, first));

        var asPhysical = await GetBookAsync(h, first);
        asPhysical.EditionCount.Should().Be(2);
        asPhysical.OtherEditions!.Select(e => e.Id).Should().BeEquivalentTo([second]);

        var asEbook = await GetBookAsync(h, second);
        asEbook.EditionCount.Should().Be(2);
        asEbook.OtherEditions!.Select(e => e.Id).Should().BeEquivalentTo([first]);
    }

    // ------------------------------------------------------------------
    // Link: group → group merge
    // ------------------------------------------------------------------

    [Fact]
    public async Task Link_merges_two_existing_groups_entirely()
    {
        var h = Harness();
        // Group A: two physical editions of one title.
        var a1 = await CreateBookAsync(h, "physical", "Shared Title", "An Author", isbn: "9780141183848");
        var a2 = await CreateBookAsync(h, "ebook", "Shared Title", "An Author");
        // Group B: two editions of a different title.
        var b1 = await CreateBookAsync(h, "physical", "Other Title", "Other Author", isbn: "9780199535576");
        var b2 = await CreateBookAsync(h, "ebook", "Other Title", "Other Author");

        await using (var check = await h.Factory.CreateDbContextAsync())
        {
            (await check.Books.SingleAsync(b => b.Id == a1)).WorkId
                .Should().Be((await check.Books.SingleAsync(b => b.Id == a2)).WorkId);
            (await check.Books.SingleAsync(b => b.Id == b1)).WorkId
                .Should().Be((await check.Books.SingleAsync(b => b.Id == b2)).WorkId);
        }

        var result = await h.Service.LinkWorkAsync(
            new LibraryLinkWorkRequest(Client, Key(), a1, b1));

        var data = (LibraryWorkMembershipResultDto)result.Data!;
        data.EditionCount.Should().Be(4, "every edition of both groups comes along");

        await using var db = await h.Factory.CreateDbContextAsync();
        var merged = await db.Books.Where(b => b.WorkId == data.WorkId).Select(b => b.Id).ToListAsync();
        merged.Should().BeEquivalentTo([a1, a2, b1, b2]);
        (await db.Works.CountAsync()).Should().Be(1, "the emptied source work and its siblings collapse into one");
    }

    // ------------------------------------------------------------------
    // Link: no-op and invalid cases
    // ------------------------------------------------------------------

    [Fact]
    public async Task Link_a_book_to_itself_is_rejected()
    {
        var h = Harness();
        var book = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");

        var baseline = await VersionAsync(h);
        var result = await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), book, book));

        ErrorCode(result).Should().Be("invalid_work_link");
        Version(result).Should().Be(baseline, "a rejected request does not bump the version");
        (await BookCountAsync(h)).Should().Be(1);
    }

    [Fact]
    public async Task Link_books_already_in_one_work_is_a_no_op()
    {
        var h = Harness();
        var first = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var second = await CreateBookAsync(h, "ebook", "Fictions", "Jorge Luis Borges");
        var workId = await WorkIdOfAsync(h, first);
        second.Should().NotBe(first);

        // Same automatic group already.
        await using (var check = await h.Factory.CreateDbContextAsync())
            (await check.Books.SingleAsync(b => b.Id == second)).WorkId.Should().Be(workId);

        var baseline = await VersionAsync(h);
        var result = await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), second, first));

        Version(result).Should().Be(baseline, "an already-grouped pair succeeds without a version bump");
        ((LibraryWorkMembershipResultDto)result.Data!).EditionCount.Should().Be(2);
        ((LibraryWorkMembershipResultDto)result.Data!).RemovedWorkId.Should().BeNull();
    }

    [Fact]
    public async Task Link_with_a_missing_book_is_rejected()
    {
        var h = Harness();
        var book = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");

        var missingTarget = await h.Service.LinkWorkAsync(
            new LibraryLinkWorkRequest(Client, Key(), book, Guid.NewGuid()));
        ErrorCode(missingTarget).Should().Be("target_book_not_found");

        var missingSource = await h.Service.LinkWorkAsync(
            new LibraryLinkWorkRequest(Client, Key(), Guid.NewGuid(), book));
        ErrorCode(missingSource).Should().Be("book_not_found");

        (await BookCountAsync(h)).Should().Be(1);
    }

    [Fact]
    public async Task Link_without_an_idempotency_key_is_rejected()
    {
        var h = Harness();
        var first = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var second = await CreateBookAsync(h, "ebook", "Something Else", "Another Author");

        var result = await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest("", "", second, first));

        ErrorCode(result).Should().Be("invalid_idempotency");
    }

    [Fact]
    public async Task Link_replays_exactly_once_for_the_same_key()
    {
        var h = Harness();
        var first = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var second = await CreateBookAsync(h, "ebook", "Something Else", "Another Author");
        var key = Key();

        var baseline = await ReceiptCountAsync(h);
        var live = await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, key, second, first));
        var replay = await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, key, second, first));

        replay.Duplicate.Should().BeTrue();
        replay.StateVersion.Should().Be(live.StateVersion);
        replay.Reply.Should().Be(live.Reply);
        (await ReceiptCountAsync(h)).Should().Be(baseline + 1, "the replay reuses the stored receipt");
        (await WorkCountAsync(h)).Should().Be(1, "the replay must not merge twice");
    }

    // ------------------------------------------------------------------
    // Unlink: split
    // ------------------------------------------------------------------

    [Fact]
    public async Task Unlink_gives_the_book_its_own_new_work()
    {
        var h = Harness();
        var borges = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var other = await CreateBookAsync(h, "ebook", "Something Else Entirely", "Another Author");
        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), other, borges));

        var baseline = await VersionAsync(h);
        var result = await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), other));

        var data = (LibraryWorkMembershipResultDto)result.Data!;
        data.BookId.Should().Be(other);
        data.WorkId.Should().NotBe(Guid.Empty, "a book must never be left with a null/empty work");
        data.WorkId.Should().NotBe(await WorkIdOfAsync(h, borges));
        data.EditionCount.Should().Be(1);
        Version(result).Should().Be(baseline + 1);

        await using var db = await h.Factory.CreateDbContextAsync();
        var split = await db.Books.SingleAsync(b => b.Id == other);
        split.WorkId.Should().Be(data.WorkId);

        // The new work is populated from the book's OWN identity, not the group's.
        var work = await db.Works.SingleAsync(w => w.Id == data.WorkId);
        work.Title.Should().Be("Something Else Entirely");
        work.Author.Should().Be("Another Author");
        work.NormalizedTitle.Should().Be(BookIdentityNormalizer.NormalizeTitle("Something Else Entirely"));
        work.NormalizedAuthor.Should().Be(BookIdentityNormalizer.NormalizeAuthor("Another Author"));

        // The work it left still exists, because its other book is still in it.
        var remainingWorkId = (await db.Books.SingleAsync(b => b.Id == borges)).WorkId;
        (await db.Works.AnyAsync(w => w.Id == remainingWorkId)).Should().BeTrue();
    }

    [Fact]
    public async Task Unlink_authors_corrected_metadata_into_the_new_work()
    {
        var h = Harness();
        var book = await CreateBookAsync(h, "physical", "Wrong Title", "Wrong Author");
        var sibling = await CreateBookAsync(h, "ebook", "Wrong Title", "Wrong Author");

        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), book, Title: "Corrected Title", Author: "Corrected Author"));

        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), book));

        await using var db = await h.Factory.CreateDbContextAsync();
        var work = await db.Works.SingleAsync(w => w.Id == (db.Books.Single(b => b.Id == book)).WorkId);
        work.Title.Should().Be("Corrected Title");
        work.Author.Should().Be("Corrected Author");
        work.NormalizedTitle.Should().Be(BookIdentityNormalizer.NormalizeTitle("Corrected Title"));

        // The sibling stayed where it was; only the unlinked book moved.
        (await db.Books.SingleAsync(b => b.Id == sibling)).WorkId.Should().NotBe(work.Id);
    }

    [Fact]
    public async Task Unlink_from_a_three_edition_group_leaves_the_other_two_together()
    {
        var h = Harness();
        var a = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var b = await CreateBookAsync(h, "ebook", "Fictions", "Jorge Luis Borges");
        var c = await CreateBookAsync(h, "audiobook", "Fictions", "Jorge Luis Borges", asin: "B095TNRPXD");

        var workId = await WorkIdOfAsync(h, a);
        await using (var check = await h.Factory.CreateDbContextAsync())
            (await check.Books.CountAsync(x => x.WorkId == workId)).Should().Be(3);

        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), c));

        await using var db = await h.Factory.CreateDbContextAsync();
        var remaining = await db.Books.Where(x => x.WorkId == workId).Select(x => x.Id).ToListAsync();
        remaining.Should().BeEquivalentTo([a, b]);
        (await db.Books.SingleAsync(x => x.Id == c)).WorkId.Should().NotBe(workId);
    }

    [Fact]
    public async Task Unlink_a_book_already_alone_is_a_no_op()
    {
        var h = Harness();
        var book = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var workId = await WorkIdOfAsync(h, book);

        var baseline = await VersionAsync(h);
        var result = await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), book));

        Version(result).Should().Be(baseline);
        ((LibraryWorkMembershipResultDto)result.Data!).WorkId.Should().Be(workId);
        ((LibraryWorkMembershipResultDto)result.Data!).EditionCount.Should().Be(1);
        (await WorkCountAsync(h)).Should().Be(1, "a no-op must not leave a stray work behind");
    }

    [Fact]
    public async Task Unlink_with_a_missing_book_is_rejected()
    {
        var h = Harness();

        var baseline = await VersionAsync(h);
        var result = await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), Guid.NewGuid()));

        ErrorCode(result).Should().Be("book_not_found");
        Version(result).Should().Be(baseline);
    }

    [Fact]
    public async Task Unlink_then_relink_returns_to_the_original_work()
    {
        var h = Harness();
        var first = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var second = await CreateBookAsync(h, "ebook", "Something Else", "Another Author");
        var originalWorkId = await WorkIdOfAsync(h, first);

        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), second, first));
        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), second));
        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), second, first));

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Books.SingleAsync(b => b.Id == second)).WorkId.Should().Be(originalWorkId);
        (await db.Works.CountAsync()).Should().Be(1, "the intermediate work is not left orphaned");
    }

    // ------------------------------------------------------------------
    // Preservation: only WorkId moves
    // ------------------------------------------------------------------

    [Fact]
    public async Task Link_and_unlink_preserve_every_book_level_value()
    {
        var h = Harness();
        var collection = (CollectionDto)(await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), "Philosophy"))).Data!;

        var book = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges", isbn: "9780141183848");
        var sibling = await CreateBookAsync(h, "ebook", "Fictions", "Jorge Luis Borges");

        // A full book-level state: progress, finished, rating, review, favourite
        // and two collection memberships.
        await h.Service.UpdateProgressAsync(book, "epubcfi(/6/4!/4/2/2)", 42);
        await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), book,
            Rating: 5,
            PersonalReview: "Worth rereading.",
            IsFavorite: true,
            CollectionIds: [collection.Id, (await CreateCollectionAsync(h, "Short Stories")).Id]));

        await using var capture = await h.Factory.CreateDbContextAsync();
        var before = await SnapshotAsync(capture, book);

        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), book));
        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), book, sibling));

        await using var after = await h.Factory.CreateDbContextAsync();
        var now = await SnapshotAsync(after, book);

        now.Should().BeEquivalentTo(before, o => o.Excluding(s => s.WorkId));
    }

    [Fact]
    public async Task Link_does_not_touch_the_other_books_in_either_group()
    {
        var h = Harness();
        var a1 = await CreateBookAsync(h, "physical", "Group A", "Author A");
        var a2 = await CreateBookAsync(h, "ebook", "Group A", "Author A");
        var b1 = await CreateBookAsync(h, "physical", "Group B", "Author B");
        var b2 = await CreateBookAsync(h, "ebook", "Group B", "Author B");

        await h.Service.UpdateProgressAsync(a2, "loc-a2", 33);
        await h.Service.UpdateProgressAsync(b2, "loc-b2", 66);

        await using var capture = await h.Factory.CreateDbContextAsync();
        var beforeA2 = await SnapshotAsync(capture, a2);
        var beforeB2 = await SnapshotAsync(capture, b2);

        await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), a1, b1));

        await using var after = await h.Factory.CreateDbContextAsync();
        (await SnapshotAsync(after, a2)).Should().BeEquivalentTo(beforeA2, o => o.Excluding(s => s.WorkId));
        (await SnapshotAsync(after, b2)).Should().BeEquivalentTo(beforeB2, o => o.Excluding(s => s.WorkId));
    }

    // ------------------------------------------------------------------
    // Persistence and versioning
    // ------------------------------------------------------------------

    [Fact]
    public async Task Membership_survives_a_fresh_context()
    {
        var h = Harness();
        var first = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var second = await CreateBookAsync(h, "ebook", "Something Else", "Another Author");

        var link = await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), second, first));
        var workId = ((LibraryWorkMembershipResultDto)link.Data!).WorkId;

        // Re-read through a brand-new service + factory, as a later request would.
        var reread = await GetBookAsync(h, second);
        reread.WorkId.Should().Be(workId);
        reread.OtherEditions!.Single().Id.Should().Be(first);
    }

    [Fact]
    public async Task Link_bumps_the_state_version_by_exactly_one()
    {
        var h = Harness();
        var first = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var second = await CreateBookAsync(h, "ebook", "Something Else", "Another Author");

        var baseline = await VersionAsync(h);
        var result = await h.Service.LinkWorkAsync(new LibraryLinkWorkRequest(Client, Key(), second, first));

        Version(result).Should().Be(baseline + 1, "one mutation, one version bump");
    }

    [Fact]
    public async Task Unlink_does_not_delete_a_work_another_book_still_uses()
    {
        var h = Harness();
        var a = await CreateBookAsync(h, "physical", "Fictions", "Jorge Luis Borges");
        var b = await CreateBookAsync(h, "ebook", "Fictions", "Jorge Luis Borges");
        var otherWorkId = await WorkIdOfAsync(h, a);

        await h.Service.UnlinkWorkAsync(new LibraryUnlinkWorkRequest(Client, Key(), a));

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Works.AnyAsync(w => w.Id == otherWorkId)).Should().BeTrue();
        (await db.Books.SingleAsync(x => x.Id == b)).WorkId.Should().Be(otherWorkId);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private sealed record BookSnapshot(
        Guid Id,
        string Title,
        string? Author,
        Guid WorkId,
        int ProgressPercent,
        string? LastLocation,
        int Rating,
        bool IsFavorite,
        string? PersonalReview,
        bool HasFile,
        string? FileName,
        string? Isbn,
        string? Asin,
        IReadOnlyList<Guid> CollectionIds);

    private static async Task<BookSnapshot> SnapshotAsync(NostosDbContext db, Guid bookId)
    {
        var book = await db.Books
            .Include(b => b.BookCollections)
            .AsNoTracking()
            .SingleAsync(b => b.Id == bookId);

        return new BookSnapshot(
            book.Id,
            book.Title,
            book.Author,
            book.WorkId,
            book.Progress.ProgressPercent,
            book.Progress.LastLocation,
            book.Work!.Rating,
            book.Work.IsFavorite,
            book.Work.PersonalReview,
            book.FileDetails.HasFile,
            book.FileDetails.FileName,
            book switch { PhysicalBookModel p => p.Isbn, EBookModel e => e.Isbn, _ => null },
            book is AudioBookModel a ? a.Asin : null,
            book.BookCollections.Select(bc => bc.CollectionId).OrderBy(id => id).ToList());
    }

    private static string? ErrorCode(LibraryCommandResultDto result) =>
        result.Data switch
        {
            LibraryErrorDto e => e.Code,
            LibraryConfirmationErrorDto c => c.Code,
            _ => null,
        };

    private async Task<Guid> CreateBookAsync(
        TestHarness h,
        string type,
        string title,
        string? author = null,
        string? isbn = null,
        string? asin = null)
    {
        var result = await h.Service.CreateOrMatchBookAsync(new LibraryCreateBookRequest(
            Client, Key(), type, title, Author: author, Isbn: isbn, Asin: asin,
            ForceCreate: true), strictConfirmation: false);

        var outcome = (LibraryCreateOrMatchResultDto)result.Data!;
        outcome.Outcome.Should().Be("created");
        return outcome.BookId!.Value;
    }

    private async Task<CollectionDto> CreateCollectionAsync(TestHarness h, string name)
    {
        var result = await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), name));
        return (CollectionDto)result.Data!;
    }

    private async Task<BookDto> GetBookAsync(TestHarness h, Guid id) =>
        (BookDto)(await h.Service.GetBookAsync(id)).Data!;

    private async Task<Guid> WorkIdOfAsync(TestHarness h, Guid bookId)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return (await db.Books.AsNoTracking().SingleAsync(b => b.Id == bookId)).WorkId;
    }

    private async Task<int> BookCountAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Books.CountAsync();
    }

    private async Task<int> WorkCountAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Works.CountAsync();
    }

    private async Task<int> ReceiptCountAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.LibraryCommandReceipts.CountAsync();
    }

    /// <summary>
    /// The command's reported state version, parsed for arithmetic. Creating the
    /// fixture's books legitimately bumps the version, so a mutation's version is
    /// only meaningful relative to the value it started from.
    /// </summary>
    private static long Version(LibraryCommandResultDto result) => long.Parse(result.StateVersion);

    private async Task<long> VersionAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        var state = await db.LibraryStates.AsNoTracking().SingleOrDefaultAsync();
        return long.Parse(state?.StateVersion ?? "0");
    }

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private TestHarness Harness()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        using (var bootstrap = new NostosDbContext(options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var factory = new TestContextFactory(options);
        var lookup = new BookLookupService(new NoopHttpClientFactory(), new SilentLogger<BookLookupService>());
        var service = new LibraryService(factory, lookup);
        return new TestHarness(factory, service);
    }

    private sealed record TestHarness(IDbContextFactory<NostosDbContext> Factory, ILibraryService Service);

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

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}

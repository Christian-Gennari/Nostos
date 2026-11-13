using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Characterization of the one book-update field mapping (issue #1612). Every
/// field an update can carry is exercised here through the live entry point
/// (<see cref="LibraryService.UpdateBookAsync"/>), so moving that mapping into a
/// shared helper is a pure refactor: these assertions must not change.
///
/// The rules this file pins, because they are easy to break:
/// rating, favourite and review are the BOOK's (the work every edition shares);
/// an edition only holds its own rating/review as an exception; collection
/// membership is BookCollections only, and the singular CollectionId REPLACES
/// the whole set; null means "leave unchanged" and an empty string clears.
/// </summary>
public sealed class BookUpdateMappingTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";

    private readonly SqliteTestFixture _fixture;

    public BookUpdateMappingTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // Root and metadata fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_sets_root_fields_and_clears_author_with_an_empty_string()
    {
        var h = Harness();
        var bookId = await CreateAsync(h, title: "Fictions", author: "Borges");

        var dto = await UpdateAsync(h, bookId, Title: "Ficciones", Author: "J.L. Borges");
        dto.Title.Should().Be("Ficciones");
        dto.Author.Should().Be("J.L. Borges");

        var cleared = await UpdateAsync(h, bookId, Author: "");
        cleared.Author.Should().BeNull("an empty string clears the author");
        cleared.Title.Should().Be("Ficciones", "a field the request did not carry is untouched");
    }

    [Fact]
    public async Task Update_sets_and_clears_every_metadata_field_with_an_empty_string()
    {
        var h = Harness();
        var bookId = await CreateAsync(h);

        var fields = new (string Name, Func<string?, LibraryUpdateBookRequest> Patch, Func<BookDto, string?> Read)[]
        {
            ("Subtitle", v => Patch(bookId, Subtitle: v), d => d.Subtitle),
            ("Editor", v => Patch(bookId, Editor: v), d => d.Editor),
            ("Translator", v => Patch(bookId, Translator: v), d => d.Translator),
            ("Description", v => Patch(bookId, Description: v), d => d.Description),
            ("Publisher", v => Patch(bookId, Publisher: v), d => d.Publisher),
            ("PlaceOfPublication", v => Patch(bookId, PlaceOfPublication: v), d => d.PlaceOfPublication),
            ("PublishedDate", v => Patch(bookId, PublishedDate: v), d => d.PublishedDate),
            ("Edition", v => Patch(bookId, Edition: v), d => d.Edition),
            ("Language", v => Patch(bookId, Language: v), d => d.Language),
            ("Categories", v => Patch(bookId, Categories: v), d => d.Categories),
            ("Series", v => Patch(bookId, Series: v), d => d.Series),
            ("VolumeNumber", v => Patch(bookId, VolumeNumber: v), d => d.VolumeNumber),
        };

        foreach (var field in fields)
        {
            ShouldSucceed(await h.Service.UpdateBookAsync(field.Patch($"{field.Name} value")));
            field.Read(await GetAsync(h, bookId)).Should().Be(
                $"{field.Name} value", $"{field.Name} is written by an update");

            ShouldSucceed(await h.Service.UpdateBookAsync(field.Patch("")));
            field.Read(await GetAsync(h, bookId)).Should().BeNull(
                $"{field.Name} is cleared by an empty string");

            ShouldSucceed(await h.Service.UpdateBookAsync(field.Patch(null)));
            field.Read(await GetAsync(h, bookId)).Should().BeNull(
                $"{field.Name} left alone by null stays cleared");
        }
    }

    // ------------------------------------------------------------------
    // The book's values (work) and the edition's exceptions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_writes_rating_favourite_and_review_to_the_work_and_an_edition_exception_to_the_edition()
    {
        var h = Harness();
        var bookId = await CreateAsync(h);

        var dto = await UpdateAsync(h, bookId,
            Rating: 9,
            IsFavorite: true,
            PersonalReview: "A labyrinth.",
            EditionRating: 9,
            EditionReview: "This copy.");

        dto.Rating.Should().Be(5, "the book's rating is clamped to 0..5");
        dto.IsFavorite.Should().BeTrue();
        dto.PersonalReview.Should().Be("A labyrinth.");
        dto.EditionRating.Should().Be(5, "the edition's own rating is clamped too");
        dto.EditionReview.Should().Be("This copy.");

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var work = await db.Works.AsNoTracking().SingleAsync(w => w.Id == dto.WorkId);
            work.Rating.Should().Be(5, "the book's rating lives on the work");
            work.IsFavorite.Should().BeTrue();
            work.PersonalReview.Should().Be("A labyrinth.");

            var edition = await db.Books.AsNoTracking().SingleAsync(b => b.Id == bookId);
            edition.Progress.Rating.Should().Be(5, "the edition exception is stored on the edition");
            edition.Progress.PersonalReview.Should().Be("This copy.");
        }

        var cleared = await UpdateAsync(h, bookId,
            Rating: 0,
            IsFavorite: false,
            PersonalReview: "",
            EditionRating: 0,
            EditionReview: "");

        cleared.Rating.Should().Be(0, "zero clears the book's rating");
        cleared.IsFavorite.Should().BeFalse();
        cleared.PersonalReview.Should().BeNull("an empty string clears the book's review");
        cleared.EditionRating.Should().Be(0, "zero clears the edition exception");
        cleared.EditionReview.Should().BeNull("an empty string clears the edition exception");
    }

    // ------------------------------------------------------------------
    // Finished state
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_finished_semantics_cover_an_explicit_date_finishing_and_unfinishing()
    {
        var h = Harness();
        var bookId = await CreateAsync(h);

        // Reading it first: finishing must not move Last read.
        await h.Service.UpdateProgressAsync(bookId, "loc-1", 40);
        var readAt = (await GetAsync(h, bookId)).LastReadAt;
        readAt.Should().NotBeNull();

        var finished = await UpdateAsync(h, bookId, IsFinished: true);
        finished.FinishedAt.Should().NotBeNull("IsFinished: true sets the moment it finished");
        finished.ProgressPercent.Should().Be(100);
        finished.LastReadAt.Should().Be(readAt, "finishing is not a separate read");

        var explicitDate = new DateTime(2024, 3, 1, 8, 30, 0, DateTimeKind.Utc);
        var dated = await UpdateAsync(h, bookId, FinishedAt: explicitDate);
        dated.FinishedAt.Should().Be(explicitDate, "an explicit FinishedAt is taken as given");

        var unfinished = await UpdateAsync(h, bookId, IsFinished: false);
        unfinished.FinishedAt.Should().BeNull("unfinishing clears the finished moment");
        unfinished.ProgressPercent.Should().Be(100,
            "unfinishing clears the date only; the percentage is left as it was");
    }

    [Fact]
    public async Task Update_with_no_finished_or_is_finished_leaves_finished_state_alone()
    {
        var h = Harness();
        var bookId = await CreateAsync(h);
        var finished = await UpdateAsync(h, bookId, IsFinished: true);
        var finishedAt = finished.FinishedAt;

        var unchanged = await UpdateAsync(h, bookId, Title: "Renamed");

        unchanged.FinishedAt.Should().Be(finishedAt, "a metadata edit does not reopen or re-date the book");
        unchanged.ProgressPercent.Should().Be(100);
    }

    // ------------------------------------------------------------------
    // Subtype fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_clears_a_paper_books_isbn_with_an_empty_string()
    {
        var h = Harness();
        var bookId = await CreateAsync(h, "physical");

        (await UpdateAsync(h, bookId, Isbn: "9780141183848")).Isbn.Should().Be("9780141183848");

        var cleared = await UpdateAsync(h, bookId, Isbn: "");
        cleared.Isbn.Should().BeNull("an empty string clears an ISBN, as for every optional string");

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Books.OfType<PhysicalBookModel>().AsNoTracking().SingleAsync(b => b.Id == bookId))
            .Isbn.Should().BeNull();
    }

    [Fact]
    public async Task Update_clears_an_ebooks_isbn_with_an_empty_string()
    {
        var h = Harness();
        var bookId = await CreateAsync(h, "ebook");

        (await UpdateAsync(h, bookId, Isbn: "9780141183848")).Isbn.Should().Be("9780141183848");
        (await UpdateAsync(h, bookId, Isbn: "")).Isbn.Should().BeNull();
    }

    [Fact]
    public async Task Update_clears_an_audiobooks_asin_duration_and_narrator_with_an_empty_string()
    {
        var h = Harness();
        var bookId = await CreateAsync(h, "audiobook", asin: "B095TNRPXD");

        var set = await UpdateAsync(h, bookId,
            Asin: "B000000001", Duration: "12:34:56", Narrator: "Reader");
        set.Asin.Should().Be("B000000001");
        set.Duration.Should().Be("12:34:56");
        set.Narrator.Should().Be("Reader");

        var cleared = await UpdateAsync(h, bookId, Asin: "", Duration: "", Narrator: "");
        cleared.Asin.Should().BeNull("an empty string clears the ASIN");
        cleared.Duration.Should().BeNull("an empty string clears the duration");
        cleared.Narrator.Should().BeNull("an empty string clears the narrator");
    }

    [Fact]
    public async Task Update_page_count_recomputes_a_paper_books_progress_but_leaves_an_ebooks_alone()
    {
        var h = Harness();
        var paper = await CreateAsync(h, "physical", title: "Paper");
        var ebook = await CreateAsync(h, "ebook", title: "Digital");

        await h.Service.UpdateProgressAsync(paper, """{"page":100}""", 0);
        await h.Service.UpdateProgressAsync(ebook, """{"page":100}""", 0);

        var repaged = await UpdateAsync(h, paper, PageCount: 400);
        repaged.PageCount.Should().Be(400);
        repaged.ProgressPercent.Should().Be(25, "a changed page count changes what the stored page means");

        var unchanged = await UpdateAsync(h, paper, PageCount: 400);
        unchanged.ProgressPercent.Should().Be(25, "an unchanged page count recomputes nothing");

        var digital = await UpdateAsync(h, ebook, PageCount: 400);
        digital.PageCount.Should().Be(400);
        digital.ProgressPercent.Should().Be(0,
            "the paper page rule does not apply to an ebook's own location");
    }

    // ------------------------------------------------------------------
    // Collection membership (the singular id REPLACES the set)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_singular_collection_id_replaces_every_membership()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = await CreateAsync(h);
        ShouldSucceed(await h.Service.UpdateBookAsync(
            Patch(bookId, CollectionIds: [coll1.Id, coll2.Id])));

        var replaced = await UpdateAsync(h, bookId, CollectionId: coll1.Id);
        replaced.CollectionIds.Should().BeEquivalentTo([coll1.Id],
            "the singular id is the legacy contract and replaces the whole set");

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.BookCollections.Where(bc => bc.BookId == bookId).ToListAsync())
            .Select(bc => bc.CollectionId).Should().BeEquivalentTo([coll1.Id]);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static LibraryUpdateBookRequest Patch(
        Guid bookId,
        string? Title = null,
        string? Subtitle = null,
        string? Author = null,
        string? Editor = null,
        string? Translator = null,
        string? Narrator = null,
        string? Description = null,
        string? Isbn = null,
        string? Asin = null,
        string? Duration = null,
        string? Publisher = null,
        string? PlaceOfPublication = null,
        string? PublishedDate = null,
        string? Edition = null,
        int? PageCount = null,
        string? Language = null,
        string? Categories = null,
        string? Series = null,
        string? VolumeNumber = null,
        Guid? CollectionId = null,
        bool ClearCollection = false,
        IReadOnlyList<Guid>? CollectionIds = null,
        int? Rating = null,
        bool? IsFavorite = null,
        string? PersonalReview = null,
        DateTime? FinishedAt = null,
        bool? IsFinished = null,
        int? EditionRating = null,
        string? EditionReview = null) =>
        new(Client, $"k-{Guid.NewGuid():N}", bookId,
            Title: Title, Subtitle: Subtitle, Author: Author, Editor: Editor,
            Translator: Translator, Narrator: Narrator, Description: Description,
            Isbn: Isbn, Asin: Asin, Duration: Duration, Publisher: Publisher,
            PlaceOfPublication: PlaceOfPublication, PublishedDate: PublishedDate,
            Edition: Edition, PageCount: PageCount, Language: Language,
            Categories: Categories, Series: Series, VolumeNumber: VolumeNumber,
            CollectionId: CollectionId, ClearCollection: ClearCollection,
            CollectionIds: CollectionIds, Rating: Rating, IsFavorite: IsFavorite,
            PersonalReview: PersonalReview, FinishedAt: FinishedAt,
            IsFinished: IsFinished, EditionRating: EditionRating,
            EditionReview: EditionReview);

    private async Task<BookDto> UpdateAsync(
        TestHarness h,
        Guid bookId,
        string? Title = null,
        string? Author = null,
        string? Isbn = null,
        string? Asin = null,
        string? Duration = null,
        string? Narrator = null,
        int? PageCount = null,
        Guid? CollectionId = null,
        IReadOnlyList<Guid>? CollectionIds = null,
        int? Rating = null,
        bool? IsFavorite = null,
        string? PersonalReview = null,
        DateTime? FinishedAt = null,
        bool? IsFinished = null,
        int? EditionRating = null,
        string? EditionReview = null)
    {
        var result = await h.Service.UpdateBookAsync(Patch(bookId,
            Title: Title, Author: Author, Isbn: Isbn, Asin: Asin,
            Duration: Duration, Narrator: Narrator, PageCount: PageCount,
            CollectionId: CollectionId, CollectionIds: CollectionIds,
            Rating: Rating, IsFavorite: IsFavorite, PersonalReview: PersonalReview,
            FinishedAt: FinishedAt, IsFinished: IsFinished,
            EditionRating: EditionRating, EditionReview: EditionReview));
        ShouldSucceed(result);
        return (BookDto)result.Data!;
    }

    private static void ShouldSucceed(LibraryCommandResultDto result) =>
        (result.Data is LibraryErrorDto or LibraryConfirmationErrorDto)
            .Should().BeFalse($"the update should have succeeded, but replied: {result.Reply}");

    private async Task<Guid> CreateAsync(
        TestHarness h,
        string type = "physical",
        string title = "Fictions",
        string? author = "Borges",
        string? isbn = null,
        string? asin = null)
    {
        var created = await h.Service.CreateOrMatchBookAsync(
            new LibraryCreateBookRequest(Client, $"k-{Guid.NewGuid():N}", type, title,
                Author: author, Isbn: isbn, Asin: asin),
            strictConfirmation: false);
        ShouldSucceed(created);
        return ((LibraryCreateOrMatchResultDto)created.Data!).BookId!.Value;
    }

    private static async Task<BookDto> GetAsync(TestHarness h, Guid id) =>
        (BookDto)(await h.Service.GetBookAsync(id)).Data!;

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
        var lookup = new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance);
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
}

using System.Net.Http;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

public sealed class LibraryServiceTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";
    private const string IsbnBorges = "9780141183848";
    private const string AsinExample = "B095TNRPXD";
    private const string IsbnValid = "9780141183848";

    private readonly SqliteTestFixture _fixture;

    public LibraryServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // Create-or-match
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_book_with_isbn_creates_and_normalizes_identity()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(CreateRequest("physical", "Fictions", Isbn: IsbnBorges), strictConfirmation: true);

        result.Duplicate.Should().BeFalse();
        result.StateVersion.Should().Be("1");
        var data = (LibraryCreateOrMatchResultDto)result.Data!;
        data.Outcome.Should().Be("created");
        data.BookId.Should().NotBeNull();

        await using var db = await h.Factory.CreateDbContextAsync();
        var book = await db.Books.OfType<PhysicalBookModel>().SingleAsync(b => b.Id == data.BookId);
        book.NormalizedIsbn.Should().Be(IsbnBorges);
        book.NormalizedAsin.Should().BeNull();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(1);
        (await db.LibraryStates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_with_same_isbn_different_key_matches_existing()
    {
        var h = Harness();
        var first = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Isbn: IsbnBorges), strictConfirmation: true);
        var second = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions (different title)", Isbn: IsbnBorges), strictConfirmation: true);

        second.Duplicate.Should().BeFalse();
        var data = (LibraryCreateOrMatchResultDto)second.Data!;
        data.Outcome.Should().Be("matched");
        data.BookId.Should().Be(((LibraryCreateOrMatchResultDto)first.Data!).BookId);
        second.StateVersion.Should().Be("1", "matching does not bump the version");

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_audiobook_with_asin_sets_normalized_asin_only()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "War and Peace", Asin: AsinExample),
            strictConfirmation: true);

        var data = (LibraryCreateOrMatchResultDto)result.Data!;
        await using var db = await h.Factory.CreateDbContextAsync();
        var book = await db.Books.OfType<AudioBookModel>().SingleAsync(b => b.Id == data.BookId);
        book.NormalizedAsin.Should().Be(AsinExample);
        book.NormalizedIsbn.Should().BeNull("an audiobook does not carry ISBN identity");
    }

    [Fact]
    public async Task Create_audiobook_with_isbn_is_rejected_instead_of_silently_dropped()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "War and Peace", Asin: AsinExample, Isbn: IsbnBorges),
            strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_book_identity",
            "an identifier the model cannot store must never be silently dropped");
    }

    [Fact]
    public async Task Create_matches_exact_title_and_author()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Jorge Luis Borges"), strictConfirmation: true);
        var second = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "fictions", Author: "jorge luis borges"), strictConfirmation: true);

        var data = (LibraryCreateOrMatchResultDto)second.Data!;
        data.Outcome.Should().Be("matched");
        second.StateVersion.Should().Be("1");
    }

    [Fact]
    public async Task Create_multiple_title_author_matches_strict_requires_confirmation()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations", Author: "Marcus Aurelius"), strictConfirmation: true);

        // Seed a second same-title/author edition directly (the service would
        // otherwise match it instead of creating a duplicate).
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Books.Add(new PhysicalBookModel
            {
                Title = "Meditations",
                Author = "Marcus Aurelius",
                Metadata = { Edition = "Different edition" },
            });
            await db.SaveChangesAsync();
        }

        var strict = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations", Author: "Marcus Aurelius"), strictConfirmation: true);

        strict.Duplicate.Should().BeFalse();
        var err = (LibraryConfirmationErrorDto)strict.Data!;
        err.Code.Should().Be("confirmation_required");
        err.Candidates.Should().HaveCount(2);
        strict.StateVersion.Should().Be("1", "no state change from a rejected command");

        var permissive = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations", Author: "Marcus Aurelius"), strictConfirmation: false);
        ((LibraryCreateOrMatchResultDto)permissive.Data!).Outcome.Should().Be("created");

        await using var db2 = await h.Factory.CreateDbContextAsync();
        (await db2.Books.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Create_title_only_strict_with_fuzzy_candidates_requires_confirmation()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "The Republic", Author: "Plato"), strictConfirmation: true);

        var strict = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "republic"), strictConfirmation: true);

        var err = (LibraryConfirmationErrorDto)strict.Data!;
        err.Code.Should().Be("confirmation_required");
        err.Candidates.Should().HaveCount(1);
        err.Candidates[0].MatchReason.Should().Be("fuzzy title match");
    }

    [Fact]
    public async Task Create_bare_title_strict_without_candidates_requires_more_information()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Some Unknown Book"), strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("confirmation_required");
        (await CountBooksAsync(h)).Should().Be(0);
    }

    [Fact]
    public async Task Create_bare_title_non_strict_creates()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Some Unknown Book"), strictConfirmation: false);

        ((LibraryCreateOrMatchResultDto)result.Data!).Outcome.Should().Be("created");
        (await CountBooksAsync(h)).Should().Be(1);
    }

    [Fact]
    public async Task Create_identity_conflict_when_isbn_and_asin_resolve_to_different_books()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Book A", Isbn: "9780141183848"), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "Book B", Asin: AsinExample), strictConfirmation: true);

        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Book C", Isbn: "9780141183848", Asin: AsinExample),
            strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("identity_conflict");
    }

    [Fact]
    public async Task ForceCreate_creates_despite_fuzzy_candidates()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "The Republic", Author: "Plato"), strictConfirmation: true);

        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "republic", ForceCreate: true), strictConfirmation: true);

        ((LibraryCreateOrMatchResultDto)result.Data!).Outcome.Should().Be("created");
        (await CountBooksAsync(h)).Should().Be(2);
    }

    [Fact]
    public async Task ConfirmedBookId_returns_that_book()
    {
        var h = Harness();
        var created = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true);
        var createdId = ((LibraryCreateOrMatchResultDto)created.Data!).BookId!.Value;

        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Totally Different Title", ConfirmedBookId: createdId),
            strictConfirmation: true);

        var data = (LibraryCreateOrMatchResultDto)result.Data!;
        data.Outcome.Should().Be("matched");
        data.BookId.Should().Be(createdId);
        data.Book!.Title.Should().Be("Fictions");
    }

    [Fact]
    public async Task ConfirmedBookId_missing_returns_book_not_found()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "X", ConfirmedBookId: Guid.NewGuid()), strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("book_not_found");
    }

    [Fact]
    public async Task Create_with_missing_collection_returns_collection_not_found()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges", CollectionId: Guid.NewGuid()),
            strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_not_found");
    }

    [Fact]
    public async Task Create_with_existing_collection_places_book()
    {
        var h = Harness();
        var collection = await h.Service.CreateCollectionAsync(new(Client, "coll-1", "Philosophy"));
        var collectionId = ((CollectionDto)collection.Data!).Id;

        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges", CollectionId: collectionId),
            strictConfirmation: true);

        var bookId = ((LibraryCreateOrMatchResultDto)result.Data!).BookId!.Value;
        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.BookCollections.Where(bc => bc.BookId == bookId).Select(bc => bc.CollectionId).ToListAsync())
            .Should().BeEquivalentTo([collectionId]);
    }

    [Fact]
    public async Task Create_blank_title_returns_invalid_book_identity()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "   "), strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_book_identity");
    }

    [Fact]
    public async Task Same_key_replay_returns_duplicate_without_recreating()
    {
        var h = Harness();
        var first = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Isbn: IsbnBorges, Key: "key-1"), strictConfirmation: true);
        var replay = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Isbn: IsbnBorges, Key: "key-1"), strictConfirmation: true);

        replay.Duplicate.Should().BeTrue();
        replay.Reply.Should().Be(first.Reply);
        replay.StateVersion.Should().Be(first.StateVersion);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(1);
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_same_key_produces_one_row_and_one_receipt()
    {
        var h = Harness();
        var tasks = Enumerable.Range(0, 4).Select(_ =>
            h.Service.CreateOrMatchBookAsync(
                CreateRequest("physical", "Fictions", Isbn: IsbnBorges, Key: "key-race"), strictConfirmation: true));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Duplicate).Should().Be(3, "the gate serializes; later callers replay the stored receipt");
        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(1);
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Invalid_idempotency_pair_is_rejected()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Key: ""), strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_idempotency");
    }

    [Fact]
    public async Task Version_bumps_on_change_and_stays_stable_on_reads_and_matches()
    {
        var h = Harness();
        var create = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Isbn: IsbnBorges), strictConfirmation: true);
        create.StateVersion.Should().Be("1");

        var list = await h.Service.ListBooksAsync(BookFilter.All, BookSort.Recent, null, 1, 20, null);
        list.StateVersion.Should().Be("1");

        var match = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Isbn: IsbnBorges), strictConfirmation: true);
        match.StateVersion.Should().Be("1");

        var update = await h.Service.UpdateBookAsync(new(Client, "upd-1",
            ((LibraryCreateOrMatchResultDto)create.Data!).BookId!.Value, Author: "Borges"));
        update.StateVersion.Should().Be("2");
    }

    // ------------------------------------------------------------------
    // Update
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_applies_null_preserving_semantics_and_clears_with_empty_string()
    {
        var h = Harness();
        var created = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges", Subtitle: "Sub"), strictConfirmation: true);
        var bookId = ((LibraryCreateOrMatchResultDto)created.Data!).BookId!.Value;

        var updated = await h.Service.UpdateBookAsync(new(Client, "upd-1", bookId, Author: "J.L. Borges", Subtitle: ""));
        var data = (BookDto)updated.Data!;
        data.Author.Should().Be("J.L. Borges");
        data.Subtitle.Should().BeNull();
        data.Title.Should().Be("Fictions", "unset fields are untouched");
    }

    [Fact]
    public async Task Update_moves_and_clears_collection()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "u1", bookId, CollectionId: coll1.Id));
        (await GetBookAsync(h, bookId)).CollectionIds.Should().BeEquivalentTo([coll1.Id]);

        await h.Service.UpdateBookAsync(new(Client, "u2", bookId, CollectionId: coll2.Id));
        (await GetBookAsync(h, bookId)).CollectionIds.Should().BeEquivalentTo([coll2.Id]);

        await h.Service.UpdateBookAsync(new(Client, "u3", bookId, ClearCollection: true));
        (await GetBookAsync(h, bookId)).CollectionIds.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Multi-collection membership
    // ------------------------------------------------------------------

    [Fact]
    public async Task Book_can_belong_to_multiple_collections()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        var result = await h.Service.UpdateBookAsync(
            new(Client, "m1", bookId, CollectionIds: [coll1.Id, coll2.Id]));

        var dto = (BookDto)result.Data!;
        dto.CollectionIds.Should().BeEquivalentTo([coll1.Id, coll2.Id],
            "a book belongs to both collections at once");

        await using var db = await h.Factory.CreateDbContextAsync();
        var rows = await db.BookCollections.Where(bc => bc.BookId == bookId).ToListAsync();
        rows.Select(r => r.CollectionId).Should().BeEquivalentTo([coll1.Id, coll2.Id]);
    }

    [Fact]
    public async Task Clearing_all_collections_is_expressed_by_an_empty_set()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "m1", bookId, CollectionIds: [coll1.Id]));
        var cleared = await h.Service.UpdateBookAsync(new(Client, "m2", bookId, CollectionIds: []));

        // This is the defect the set contract fixes: previously "remove from
        // collection" was not expressible at all through REST.
        ((BookDto)cleared.Data!).CollectionIds.Should().BeEmpty();

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.BookCollections.CountAsync(bc => bc.BookId == bookId)).Should().Be(0);
    }

    [Fact]
    public async Task Removing_one_membership_keeps_the_others()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "m1", bookId, CollectionIds: [coll1.Id, coll2.Id]));
        await h.Service.UpdateBookAsync(new(Client, "m2", bookId, CollectionIds: [coll2.Id]));

        var dto = await GetBookAsync(h, bookId);
        dto.CollectionIds.Should().BeEquivalentTo([coll2.Id]);
    }

    [Fact]
    public async Task Collection_filter_matches_any_membership()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "m1", bookId, CollectionIds: [coll1.Id, coll2.Id]));

        // Filtering by the SECOND collection must find the book — with a single
        // CollectionId column only the first would ever match.
        var listed = await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.Title, null, 1, 20, coll2.Id, null, null);
        var page = (PaginatedResponse<BookDto>)listed.Data!;
        page.Items.Select(b => b.Id).Should().Contain(bookId);
    }

    [Fact]
    public async Task An_importing_book_sorts_first_whatever_the_sort_key_is()
    {
        var h = Harness();

        // Two books the user has already read, and one still being imported whose
        // title, creation date and reading history all rank it LAST — the three keys
        // every user-facing sort would otherwise order it by.
        var readId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "A book already read", Author: "Test One"), strictConfirmation: true)).Data!).BookId!.Value;
        var alsoReadId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "B book also read", Author: "Test Two"), strictConfirmation: true)).Data!).BookId!.Value;
        var importingId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Zzz importing, newest and unread", Author: "Test Three"), strictConfirmation: true)).Data!).BookId!.Value;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            foreach (var id in new[] { readId, alsoReadId })
            {
                var book = await db.Books.Include(b => b.Progress).SingleAsync(b => b.Id == id);
                book.Progress.LastReadAt = DateTime.UtcNow.AddDays(-1);
            }

            var importing = await db.Books.SingleAsync(b => b.Id == importingId);
            importing.Status = BookStatus.Downloading;
            importing.CreatedAt = DateTime.UtcNow.AddYears(-1);
            await db.SaveChangesAsync();
        }

        foreach (var sort in new[] { BookSort.LastRead, BookSort.Title, BookSort.Rating, BookSort.Recent })
        {
            var listed = await h.Service.ListBooksAsync(BookFilter.All, sort, null, 1, 20, null);
            var page = (PaginatedResponse<BookDto>)listed.Data!;

            page.Items.First().Id.Should().Be(importingId,
                $"an import is the thing the user just asked for, so {sort} must not bury it " +
                "(under Last Read a new book has no LastReadAt and lands behind everything read)");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_importing_book_stays_visible_through_library_filters_until_it_finishes(bool groupByWork)
    {
        var h = Harness();
        var unrelatedCollection = (CollectionDto)(await h.Service.CreateCollectionAsync(
            new(Client, "import-filter-collection", "Other shelf"))).Data!;
        var importingId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("ebook", "Visible while importing", Author: "Progress Test"),
            strictConfirmation: true)).Data!).BookId!.Value;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var importing = await db.Books.SingleAsync(b => b.Id == importingId);
            importing.Status = BookStatus.Downloading;
            await db.SaveChangesAsync();
        }

        // Every ordinary filter rejects this row:
        // - it is not a favourite,
        // - the search text does not match,
        // - it has no PDF filename yet,
        // - and it is not in the selected collection.
        // The import must still be present because its own card/row is the only
        // surface that can show Downloading/Transcoding progress.
        var listed = await h.Service.ListBooksAsync(
            BookFilter.Favorites,
            BookSort.Title,
            "definitely-no-match",
            1,
            20,
            unrelatedCollection.Id,
            groupByWork,
            format: "pdf");
        var page = (PaginatedResponse<BookDto>)listed.Data!;

        page.Items.Select(b => b.Id).Should().ContainSingle().Which.Should().Be(importingId);

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var importing = await db.Books.SingleAsync(b => b.Id == importingId);
            importing.Status = BookStatus.Ready;
            await db.SaveChangesAsync();
        }

        var settled = await h.Service.ListBooksAsync(
            BookFilter.Favorites,
            BookSort.Title,
            "definitely-no-match",
            1,
            20,
            unrelatedCollection.Id,
            groupByWork,
            format: "pdf");
        var settledPage = (PaginatedResponse<BookDto>)settled.Data!;

        settledPage.Items.Should().BeEmpty(
            "once the operational import state ends, ordinary library filters own visibility again");
    }

    [Fact]
    public async Task An_importing_work_sorts_first_in_the_grouped_list()
    {
        var h = Harness();

        var readId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "A book already read", Author: "Test One"), strictConfirmation: true)).Data!).BookId!.Value;
        var importingId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Zzz importing", Author: "Test Four"), strictConfirmation: true)).Data!).BookId!.Value;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var read = await db.Books.Include(b => b.Progress).SingleAsync(b => b.Id == readId);
            read.Progress.LastReadAt = DateTime.UtcNow.AddDays(-1);

            var importing = await db.Books.SingleAsync(b => b.Id == importingId);
            importing.Status = BookStatus.Transcoding;
            await db.SaveChangesAsync();
        }

        var listed = await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.LastRead, null, 1, 20, null, groupByWork: true);
        var page = (PaginatedResponse<BookDto>)listed.Data!;

        page.Items.First().Id.Should().Be(importingId);
    }

    [Fact]
    public async Task An_import_that_joins_an_existing_work_represents_that_work_in_the_list()
    {
        var h = Harness();

        // A print copy the user has read, and an audiobook of the same work arriving.
        var printId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations", Author: "Marcus Aurelius"), strictConfirmation: true)).Data!).BookId!.Value;
        var audioId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "Meditations (audio)", Author: "Marcus Aurelius"), strictConfirmation: true)).Data!).BookId!.Value;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var print = await db.Books.Include(b => b.Progress).SingleAsync(b => b.Id == printId);
            print.Progress.LastReadAt = DateTime.UtcNow.AddDays(-1);

            // The import joins the print edition's work, which is what makes this case
            // different: without the rule below, the card shown for that work would be
            // the print copy, with nothing on it to watch.
            var audio = await db.Books.SingleAsync(b => b.Id == audioId);
            audio.WorkId = print.WorkId;
            audio.Status = BookStatus.Downloading;
            await db.SaveChangesAsync();
        }

        var listed = await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.LastRead, null, 1, 20, null, groupByWork: true);
        var page = (PaginatedResponse<BookDto>)listed.Data!;

        page.Items.Should().HaveCount(1, "both editions belong to one work");
        page.Items.First().Id.Should().Be(audioId, "the work is represented by the edition being imported");
    }

    [Fact]
    public async Task Sidebar_counts_include_books_with_multiple_memberships()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "m1", bookId, CollectionIds: [coll1.Id, coll2.Id]));

        var counts = ((IEnumerable<CollectionCountDto>)(await h.Service.ListCollectionCountsAsync()).Data!)
            .ToDictionary(c => c.CollectionId, c => c.BookCount);
        counts[coll1.Id].Should().Be(1);
        counts[coll2.Id].Should().Be(1, "the same book is counted in each of its collections");
    }

    [Fact]
    public async Task Unsorted_excludes_books_that_belong_to_a_collection()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "m1", bookId, CollectionIds: [coll1.Id]));

        // Membership is the only record of belonging now, so this is a direct
        // assertion: a book in a collection is not unsorted.
        var listed = await h.Service.ListBooksAsync(
            BookFilter.Unsorted, BookSort.Title, null, 1, 50, null, null, null);
        ((PaginatedResponse<BookDto>)listed.Data!).Items.Select(b => b.Id)
            .Should().NotContain(bookId, "the book belongs to a collection");
    }

    [Fact]
    public async Task Set_update_with_unknown_collection_returns_collection_not_found()
    {
        var h = Harness();
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        var result = await h.Service.UpdateBookAsync(
            new(Client, "m1", bookId, CollectionIds: [Guid.NewGuid()]));

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_not_found");
    }

    [Fact]
    public async Task Deleting_a_collection_keeps_a_books_other_memberships()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "m1", bookId, CollectionIds: [coll1.Id, coll2.Id]));
        var deleted = await h.Service.DeleteCollectionAsync(new(Client, "d1", coll1.Id, Confirm: true));
        ((LibraryDeleteCollectionResultDto)deleted.Data!).BooksUnlinked.Should().Be(1);

        var dto = await GetBookAsync(h, bookId);
        dto.CollectionIds.Should().BeEquivalentTo([coll2.Id], "only the deleted membership is removed");

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.BookCollections.CountAsync(bc => bc.BookId == bookId)).Should().Be(1);
    }

    [Fact]
    public async Task Creating_a_book_with_a_membership_set_files_it_everywhere()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;

        var created = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges", CollectionIds: [coll1.Id, coll2.Id]),
            strictConfirmation: true);

        var dto = ((LibraryCreateOrMatchResultDto)created.Data!).Book!;
        dto.CollectionIds.Should().BeEquivalentTo([coll1.Id, coll2.Id]);
    }

    [Fact]
    public async Task Legacy_single_collection_update_does_not_leave_stale_memberships()
    {
        var h = Harness();
        var coll1 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c1", "Philosophy"))).Data!;
        var coll2 = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "c2", "Fiction"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "m1", bookId, CollectionIds: [coll1.Id, coll2.Id]));
        await h.Service.UpdateBookAsync(new(Client, "m2", bookId, CollectionId: coll1.Id));

        var dto = await GetBookAsync(h, bookId);
        dto.CollectionIds.Should().BeEquivalentTo([coll1.Id],
            "the legacy single-value contract replaces the whole set, so the two cannot drift");

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.BookCollections.CountAsync(bc => bc.BookId == bookId)).Should().Be(1);
    }

    [Fact]
    public async Task Update_isbn_recomputes_normalized_identity()
    {
        var h = Harness();
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateBookAsync(new(Client, "u1", bookId, Isbn: IsbnBorges));

        await using var db = await h.Factory.CreateDbContextAsync();
        var book = await db.Books.OfType<PhysicalBookModel>().SingleAsync(b => b.Id == bookId);
        book.NormalizedIsbn.Should().Be(IsbnBorges);
    }

    [Fact]
    public async Task Update_isbn_conflicting_with_another_book_returns_duplicate_identifier()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Book A", Isbn: IsbnBorges), strictConfirmation: true);
        var b = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Book B", Author: "Author B"), strictConfirmation: true)).Data!).BookId!.Value;

        var result = await h.Service.UpdateBookAsync(new(Client, "u1", b, Isbn: IsbnBorges));

        ((LibraryErrorDto)result.Data!).Code.Should().Be("duplicate_identifier");
    }

    [Fact]
    public async Task Update_missing_book_returns_book_not_found()
    {
        var h = Harness();
        var result = await h.Service.UpdateBookAsync(new(Client, "u1", Guid.NewGuid(), Title: "X"));
        ((LibraryErrorDto)result.Data!).Code.Should().Be("book_not_found");
    }

    [Fact]
    public async Task Update_blank_title_is_rejected()
    {
        var h = Harness();
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true)).Data!).BookId!.Value;

        var result = await h.Service.UpdateBookAsync(new(Client, "u1", bookId, Title: " "));
        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_book_identity");
    }

    // ------------------------------------------------------------------
    // Collections
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_collection_and_replay_same_key()
    {
        var h = Harness();
        var created = await h.Service.CreateCollectionAsync(new(Client, "k1", "Philosophy"));
        created.StateVersion.Should().Be("1");
        ((CollectionDto)created.Data!).Name.Should().Be("Philosophy");

        var replay = await h.Service.CreateCollectionAsync(new(Client, "k1", "Philosophy"));
        replay.Duplicate.Should().BeTrue();
    }

    [Fact]
    public async Task Create_duplicate_sibling_returns_existing_collection()
    {
        var h = Harness();
        var first = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "k1", "Philosophy"))).Data!;
        var second = await h.Service.CreateCollectionAsync(new(Client, "k2", "  philosophy "));

        second.Reply.Should().Be(LibraryReplyFormatter.CollectionExists("Philosophy"));
        ((CollectionDto)second.Data!).Id.Should().Be(first.Id);
        second.StateVersion.Should().Be("1", "no change when returning the existing collection");

        // Same name under a different parent is a different collection.
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "k3", "Root"))).Data!;
        var child = await h.Service.CreateCollectionAsync(new(Client, "k4", "Philosophy", ParentId: root.Id));
        ((CollectionDto)child.Data!).Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task Create_collection_missing_parent_and_blank_name()
    {
        var h = Harness();
        var missingParent = await h.Service.CreateCollectionAsync(new(Client, "k1", "X", ParentId: Guid.NewGuid()));
        ((LibraryErrorDto)missingParent.Data!).Code.Should().Be("invalid_collection_parent");

        var blank = await h.Service.CreateCollectionAsync(new(Client, "k2", "  "));
        ((LibraryErrorDto)blank.Data!).Code.Should().Be("invalid_collection_name");
    }

    [Fact]
    public async Task Rename_collection_and_detect_sibling_collision()
    {
        var h = Harness();
        var a = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "k1", "Alpha"))).Data!;
        await h.Service.CreateCollectionAsync(new(Client, "k2", "Beta"));

        var renamed = await h.Service.RenameCollectionAsync(new(Client, "k3", a.Id, "Gamma"));
        ((CollectionDto)renamed.Data!).Name.Should().Be("Gamma");

        var collision = await h.Service.RenameCollectionAsync(new(Client, "k4", a.Id, "BETA"));
        ((LibraryErrorDto)collision.Data!).Code.Should().Be("collection_name_conflict");
    }

    [Fact]
    public async Task Move_collection_cycle_detection_and_root_move()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "k1", "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "k2", "Child", ParentId: root.Id))).Data!;

        // Self-parent.
        var self = await h.Service.MoveCollectionAsync(new(Client, "k3", root.Id, NewParentId: root.Id));
        ((LibraryErrorDto)self.Data!).Code.Should().Be("collection_cycle");

        // Move root under its own child.
        var cycle = await h.Service.MoveCollectionAsync(new(Client, "k4", root.Id, NewParentId: child.Id));
        ((LibraryErrorDto)cycle.Data!).Code.Should().Be("collection_cycle");

        // Move child to root (null parent).
        var moved = await h.Service.MoveCollectionAsync(new(Client, "k5", child.Id, NewParentId: null));
        ((CollectionDto)moved.Data!).ParentId.Should().BeNull();

        // Missing parent.
        var missing = await h.Service.MoveCollectionAsync(new(Client, "k6", child.Id, NewParentId: Guid.NewGuid()));
        ((LibraryErrorDto)missing.Data!).Code.Should().Be("invalid_collection_parent");
    }

    [Fact]
    public async Task Delete_collection_requires_confirmation_rejects_children_and_unlinks_books()
    {
        var h = Harness();
        var parent = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, "k1", "Parent"))).Data!;
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges", CollectionId: parent.Id),
            strictConfirmation: true)).Data!).BookId!.Value;

        // Without confirmation.
        var noConfirm = await h.Service.DeleteCollectionAsync(new(Client, "k2", parent.Id));
        ((LibraryErrorDto)noConfirm.Data!).Code.Should().Be("confirmation_required");

        // With children.
        await h.Service.CreateCollectionAsync(new(Client, "k3", "Child", ParentId: parent.Id));
        var hasChildren = await h.Service.DeleteCollectionAsync(new(Client, "k4", parent.Id, Confirm: true));
        ((LibraryErrorDto)hasChildren.Data!).Code.Should().Be("collection_has_children");

        // Remove the child, then delete: books unlinked, not deleted.
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var child = await db.Collections.SingleAsync(c => c.Name == "Child");
            db.Collections.Remove(child);
            await db.SaveChangesAsync();
        }

        var deleted = await h.Service.DeleteCollectionAsync(new(Client, "k5", parent.Id, Confirm: true));
        var outcome = (LibraryDeleteCollectionResultDto)deleted.Data!;
        outcome.BooksUnlinked.Should().Be(1);
        outcome.ChildrenAffected.Should().Be(0);

        (await GetBookAsync(h, bookId)).CollectionIds.Should().BeEmpty();
        (await CountCollectionsAsync(h)).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Resolve (read-only)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Resolve_exact_isbn_matches()
    {
        var h = Harness();
        var created = (LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Isbn: IsbnBorges), strictConfirmation: true)).Data!;

        var result = await h.Service.ResolveBookAsync(new LibraryResolveBookRequest(Isbn: "978-0-141-18384-8"));

        result.Resolution.Should().Be(LibraryResolution.ExactMatch);
        result.MatchedBook!.Id.Should().Be(created.BookId!.Value);
    }

    [Fact]
    public async Task Resolve_identity_conflict()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "A", Isbn: IsbnBorges), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "B", Asin: AsinExample), strictConfirmation: true);

        var result = await h.Service.ResolveBookAsync(new LibraryResolveBookRequest(Isbn: IsbnBorges, Asin: AsinExample));
        result.Resolution.Should().Be(LibraryResolution.IdentityConflict);
    }

    [Fact]
    public async Task Resolve_title_author_and_fuzzy_candidates()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "The Republic", Author: "Plato"), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Republic of Silence", Author: "Hanna"), strictConfirmation: true);

        var exact = await h.Service.ResolveBookAsync(new LibraryResolveBookRequest(Title: "the republic", Author: "plato"));
        exact.Resolution.Should().Be(LibraryResolution.ExactMatch);

        var fuzzy = await h.Service.ResolveBookAsync(new LibraryResolveBookRequest(Title: "republic"));
        fuzzy.Resolution.Should().Be(LibraryResolution.Candidates);
        fuzzy.Candidates.Should().HaveCount(2);
        fuzzy.Candidates.Should().OnlyContain(c => c.MatchReason == "fuzzy title/author match");
    }

    [Fact]
    public async Task Resolve_not_found_without_external_prefill()
    {
        var h = Harness();
        var result = await h.Service.ResolveBookAsync(new LibraryResolveBookRequest(Isbn: "9780141183848", IncludeExternalMetadata: false));

        result.Resolution.Should().Be(LibraryResolution.NotFound);
        result.Prefill.Should().BeNull();
        result.MatchedBook.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Identity backfill
    // ------------------------------------------------------------------

    [Fact]
    public async Task Backfill_normalizes_legacy_rows_idempotently()
    {
        var h = Harness();
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Books.Add(new PhysicalBookModel
            {
                Title = "Legacy Physical",
                Author = "A",
                Isbn = "978-0-141-18384-8",
            });
            db.Books.Add(new AudioBookModel
            {
                Title = "Legacy Audio",
                Author = "B",
                Asin = "b095tnrpxd",
            });
            db.Books.Add(new PhysicalBookModel
            {
                Title = "Legacy Invalid",
                Author = "C",
                Isbn = "not-an-isbn",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            await LibraryIdentityBackfill.BackfillAsync(db);
        }

        await using (var verify = await h.Factory.CreateDbContextAsync())
        {
            var physical = await verify.Books.OfType<PhysicalBookModel>().SingleAsync(b => b.Title == "Legacy Physical");
            physical.NormalizedIsbn.Should().Be("9780141183848");

            var audio = await verify.Books.OfType<AudioBookModel>().SingleAsync(b => b.Title == "Legacy Audio");
            audio.NormalizedAsin.Should().Be("B095TNRPXD");
            audio.NormalizedIsbn.Should().BeNull();

            var invalid = await verify.Books.OfType<PhysicalBookModel>().SingleAsync(b => b.Title == "Legacy Invalid");
            invalid.NormalizedIsbn.Should().BeNull();
        }

        // Idempotent: second run changes nothing and does not throw.
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            await LibraryIdentityBackfill.BackfillAsync(db);
        }
    }

    // ------------------------------------------------------------------
    // List / Get
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_books_validates_pagination_and_honors_search_and_filter()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Fictions", Author: "Borges"), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Truth and Method", Author: "Gadamer"), strictConfirmation: true);
        await h.Service.UpdateBookAsync(new(Client, "fav-1",
            (Guid)((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
                CreateRequest("physical", "Meditations", Author: "Marcus Aurelius"), strictConfirmation: true)).Data!).BookId!,
            IsFavorite: true));

        var clamped = await h.Service.ListBooksAsync(BookFilter.All, BookSort.Recent, null, 0, 5000, null);
        var page = (PaginatedResponse<BookDto>)clamped.Data!;
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(100);

        var search = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.Recent, "borges", 1, 20, null)).Data!;
        search.TotalCount.Should().Be(1);
        search.Items.Single().Title.Should().Be("Fictions");

        var favorites = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.Favorites, BookSort.Recent, null, 1, 20, null)).Data!;
        favorites.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task List_books_progress_filters_cover_not_started_reading_and_finished()
    {
        var h = Harness();
        _ = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Not Started Book", Author: "Author A"), strictConfirmation: true)).Data!).BookId!;
        var inProgress = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "In Progress Book", Author: "Author B"), strictConfirmation: true)).Data!).BookId!.Value;
        var finished = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Finished Book", Author: "Author C"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateProgressAsync(inProgress, "loc-1", 50);
        await h.Service.UpdateProgressAsync(finished, "loc-2", 100);

        // NotStarted: only progressPercent == 0 books.
        var notStartedPage = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.NotStarted, BookSort.Recent, null, 1, 20, null)).Data!;
        notStartedPage.TotalCount.Should().Be(1);
        var notStartedBook = notStartedPage.Items.Single();
        notStartedBook.Title.Should().Be("Not Started Book");
        notStartedBook.ProgressPercent.Should().Be(0);

        // Reading (unchanged semantics): not finished and 1..99 progress.
        var readingPage = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.Reading, BookSort.Recent, null, 1, 20, null)).Data!;
        readingPage.TotalCount.Should().Be(1);
        readingPage.Items.Single().Title.Should().Be("In Progress Book");

        // Finished (unchanged semantics): FinishedAt set (aligned with 100%).
        var finishedPage = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.Finished, BookSort.Recent, null, 1, 20, null)).Data!;
        finishedPage.TotalCount.Should().Be(1);
        finishedPage.Items.Single().Title.Should().Be("Finished Book");
    }

    [Fact]
    public async Task List_books_format_filters_include_supported_aliases_and_exclude_other_formats()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Physical Book", Author: "Format Tester"), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "Audio Book", Author: "Format Tester"), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("ebook", "EPUB Book", Author: "Format Tester"), strictConfirmation: true);
        var pdf = (LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("ebook", "PDF Book", Author: "Format Tester"), strictConfirmation: true)).Data!;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var pdfModel = await db.Books.OfType<EBookModel>().SingleAsync(b => b.Id == pdf.BookId);
            pdfModel.FileDetails.FileName = "document.pdf";
            await db.SaveChangesAsync();
        }

        async Task<PaginatedResponse<BookDto>> ListByFormat(string format) =>
            (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
                BookFilter.All, BookSort.Title, null, 1, 100, null, format: format)).Data!;

        (await ListByFormat("physical")).Items.Select(b => b.Title)
            .Should().Equal("Physical Book");
        (await ListByFormat("audiobook")).Items.Select(b => b.Title)
            .Should().Equal("Audio Book");
        (await ListByFormat("audio")).Items.Select(b => b.Title)
            .Should().Equal("Audio Book");
        (await ListByFormat("ebook")).Items.Select(b => b.Title)
            .Should().Equal("EPUB Book");
        (await ListByFormat("epub")).Items.Select(b => b.Title)
            .Should().Equal("EPUB Book");
        (await ListByFormat("pdf")).Items.Select(b => b.Title)
            .Should().Equal("PDF Book");
    }

    [Fact]
    public async Task Get_status_counts_includes_media_format_counts()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Physical Book", Author: "Format Tester"), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "Audio Book", Author: "Format Tester"), strictConfirmation: true);
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("ebook", "EPUB Book", Author: "Format Tester"), strictConfirmation: true);
        var pdf = (LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("ebook", "PDF Book", Author: "Format Tester"), strictConfirmation: true)).Data!;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var pdfModel = await db.Books.OfType<EBookModel>().SingleAsync(b => b.Id == pdf.BookId);
            pdfModel.FileDetails.FileName = "document.pdf";
            await db.SaveChangesAsync();
        }

        var counts = (LibraryStatusCountsDto)(await h.Service.GetStatusCountsAsync()).Data!;

        counts.All.Should().Be(4);
        counts.Audiobooks.Should().Be(1);
        counts.Ebooks.Should().Be(1);
        counts.Pdfs.Should().Be(1);
    }

    [Fact]
    public async Task Get_book_and_get_collection_not_found_envelopes()
    {
        var h = Harness();
        var book = await h.Service.GetBookAsync(Guid.NewGuid());
        ((LibraryErrorDto)book.Data!).Code.Should().Be("book_not_found");

        var collection = await h.Service.GetCollectionAsync(Guid.NewGuid());
        ((LibraryErrorDto)collection.Data!).Code.Should().Be("collection_not_found");
    }

    // ------------------------------------------------------------------
    // Progress reset (issue #9) — explicit reset intent
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reset_progress_clears_all_four_fields_and_advances_state_version_exactly_once()
    {
        var h = Harness();
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Reset Me", Author: "Author A"), strictConfirmation: true)).Data!).BookId!.Value;

        // A finished book exercises every field the reset must clear.
        await h.Service.UpdateProgressAsync(bookId, "loc-1", 100);

        var result = await h.Service.ResetProgressAsync(bookId);

        result.Duplicate.Should().BeFalse();
        result.StateVersion.Should().Be("3", "create + progress update + reset = exactly three bumps");
        result.Reply.Should().Be(LibraryReplyFormatter.ProgressReset("Reset Me"));
        result.Data.Should().BeEquivalentTo(new { updated = true });

        var dto = await GetBookAsync(h, bookId);
        dto.LastLocation.Should().BeNull();
        dto.ProgressPercent.Should().Be(0);
        dto.LastReadAt.Should().BeNull();
        dto.FinishedAt.Should().BeNull();

        await using var db = await h.Factory.CreateDbContextAsync();
        var stored = await db.Books.SingleAsync(b => b.Id == bookId);
        stored.Progress.LastLocation.Should().BeNull();
        stored.Progress.ProgressPercent.Should().Be(0);
        stored.Progress.LastReadAt.Should().BeNull();
        stored.Progress.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reset_progress_partially_read_book_clears_location_percent_and_recency()
    {
        var h = Harness();
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Partial Me", Author: "Author B"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateProgressAsync(bookId, "loc-1", 50);

        var before = await GetBookAsync(h, bookId);
        before.LastLocation.Should().Be("loc-1");
        before.ProgressPercent.Should().Be(50);
        before.LastReadAt.Should().NotBeNull();

        var result = await h.Service.ResetProgressAsync(bookId);
        result.StateVersion.Should().Be("3");
        result.Data.Should().BeEquivalentTo(new { updated = true });

        var dto = await GetBookAsync(h, bookId);
        dto.LastLocation.Should().BeNull();
        dto.ProgressPercent.Should().Be(0);
        dto.LastReadAt.Should().BeNull();
        dto.FinishedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reset_progress_already_reset_book_is_successful_noop_without_version_bump()
    {
        var h = Harness();
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Noop Me", Author: "Author C"), strictConfirmation: true)).Data!).BookId!.Value;

        var result = await h.Service.ResetProgressAsync(bookId);

        result.Duplicate.Should().BeFalse();
        result.StateVersion.Should().Be("1", "a no-op never bumps the version");
        result.Reply.Should().Be(LibraryReplyFormatter.ProgressReset("Noop Me"));
        result.Data.Should().BeEquivalentTo(new { updated = false });

        var second = await h.Service.ResetProgressAsync(bookId);
        second.StateVersion.Should().Be("1");
        second.Data.Should().BeEquivalentTo(new { updated = false });
    }

    [Fact]
    public async Task Reset_progress_unknown_book_returns_book_not_found()
    {
        var h = Harness();
        var result = await h.Service.ResetProgressAsync(Guid.NewGuid());
        ((LibraryErrorDto)result.Data!).Code.Should().Be("book_not_found");
    }

    [Fact]
    public async Task Ordinary_zero_percent_update_does_not_acquire_reset_semantics()
    {
        var h = Harness();
        var bookId = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Zero Me", Author: "Author D"), strictConfirmation: true)).Data!).BookId!.Value;

        await h.Service.UpdateProgressAsync(bookId, "", 0);

        var dto = await GetBookAsync(h, bookId);
        dto.ProgressPercent.Should().Be(0);
        dto.LastLocation.Should().BeNull();
        dto.LastReadAt.Should().NotBeNull("a 0% update is still a read event and must record recency");
        dto.FinishedAt.Should().BeNull();

        // Only the explicit reset intent clears recency.
        await h.Service.ResetProgressAsync(bookId);
        (await GetBookAsync(h, bookId)).LastReadAt.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Collections Phase 1 — atomic update contract
    // ------------------------------------------------------------------

    [Fact]
    public async Task Update_collection_moves_to_root_with_explicit_null_parent()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), child.Id, child.Name, null);

        var dto = (CollectionDto)result.Data!;
        dto.ParentId.Should().BeNull();
        dto.Name.Should().Be(child.Name);
        result.StateVersion.Should().Be("3");
    }

    [Fact]
    public async Task Update_collection_rename_preserves_current_parent()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), child.Id, "Renamed", child.ParentId);

        var dto = (CollectionDto)result.Data!;
        dto.Name.Should().Be("Renamed");
        dto.ParentId.Should().Be(root.Id);
    }

    [Fact]
    public async Task Update_collection_move_preserves_current_name()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child"))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), child.Id, child.Name, root.Id);

        var dto = (CollectionDto)result.Data!;
        dto.ParentId.Should().Be(root.Id);
        dto.Name.Should().Be(child.Name);
    }

    [Fact]
    public async Task Update_collection_whitespace_name_returns_invalid_collection_name()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), root.Id, "   ", null);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_collection_name");
        (await GetCollectionDtoAsync(h, root.Id)).Name.Should().Be("Root");
    }

    [Fact]
    public async Task Update_collection_missing_collection_returns_collection_not_found()
    {
        var h = Harness();
        var result = await h.Service.UpdateCollectionAsync(Client, Key(), Guid.NewGuid(), "X", null);
        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_not_found");
    }

    [Fact]
    public async Task Update_collection_combined_rename_and_move_is_atomic_with_one_receipt_and_one_bump()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var other = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Other"))).Data!;
        var target = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Target", root.Id))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), target.Id, "Moved + Renamed", other.Id);

        result.StateVersion.Should().Be("4", "exactly one bump for the combined change");
        var dto = (CollectionDto)result.Data!;
        dto.Name.Should().Be("Moved + Renamed");
        dto.ParentId.Should().Be(other.Id);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(4, "exactly one receipt for the combined change");
        var stored = await db.Collections.SingleAsync(c => c.Id == target.Id);
        stored.Name.Should().Be("Moved + Renamed");
        stored.ParentId.Should().Be(other.Id);
    }

    [Fact]
    public async Task Update_collection_destination_name_conflict_leaves_everything_unchanged()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;
        await h.Service.CreateCollectionAsync(new(Client, Key(), "Taken", root.Id));

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), child.Id, "TAKEN", root.Id);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_name_conflict");
        result.StateVersion.Should().Be("3", "a rejected update never bumps the version");
        var stored = await GetCollectionDtoAsync(h, child.Id);
        stored.Name.Should().Be("Child");
        stored.ParentId.Should().Be(root.Id);
    }

    [Fact]
    public async Task Update_collection_invalid_destination_leaves_everything_unchanged()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), child.Id, "New Name", Guid.NewGuid());

        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_collection_parent");
        result.StateVersion.Should().Be("2");
        var stored = await GetCollectionDtoAsync(h, child.Id);
        stored.Name.Should().Be("Child");
        stored.ParentId.Should().Be(root.Id);
    }

    [Fact]
    public async Task Update_collection_move_to_descendant_leaves_everything_unchanged()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;
        var grandchild = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Grandchild", child.Id))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), root.Id, root.Name, grandchild.Id);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_cycle");
        result.StateVersion.Should().Be("3");
        var stored = await GetCollectionDtoAsync(h, root.Id);
        stored.Name.Should().Be("Root");
        stored.ParentId.Should().BeNull();
    }

    [Fact]
    public async Task Update_collection_same_name_and_parent_is_successful_noop()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), child.Id, "Child", root.Id);

        result.Duplicate.Should().BeFalse();
        result.StateVersion.Should().Be("2", "a no-op never bumps the version");
        result.Reply.Should().Be(LibraryReplyFormatter.CollectionUpdated("Child"));
        var dto = (CollectionDto)result.Data!;
        dto.Name.Should().Be("Child");
        dto.ParentId.Should().Be(root.Id);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(3, "the no-op still writes its receipt");
    }

    [Fact]
    public async Task Update_collection_same_key_replay_returns_duplicate_without_second_mutation()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child"))).Data!;
        var key = Key();

        var first = await h.Service.UpdateCollectionAsync(Client, key, child.Id, "First", root.Id);
        var replay = await h.Service.UpdateCollectionAsync(Client, key, child.Id, "Second", null);

        replay.Duplicate.Should().BeTrue();
        replay.StateVersion.Should().Be(first.StateVersion);
        var dto = (CollectionDto)replay.Data!;
        dto.Name.Should().Be("First");
        dto.ParentId.Should().Be(root.Id);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(3, "the replay writes no second receipt");
        var stored = await db.Collections.SingleAsync(c => c.Id == child.Id);
        stored.Name.Should().Be("First");
        stored.ParentId.Should().Be(root.Id);
    }

    [Fact]
    public async Task Update_collection_key_reuse_after_noop_replays_the_noop()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;
        var key = Key();

        var noop = await h.Service.UpdateCollectionAsync(Client, key, child.Id, "Child", root.Id);
        noop.Duplicate.Should().BeFalse();
        noop.StateVersion.Should().Be("2");

        // The same key must replay the stored no-op even though the new
        // values WOULD mutate if re-executed (stale-retry hazard guard).
        var replay = await h.Service.UpdateCollectionAsync(Client, key, child.Id, "Changed", null);
        replay.Duplicate.Should().BeTrue();
        replay.StateVersion.Should().Be("2");
        ((CollectionDto)replay.Data!).Name.Should().Be("Child");

        await using var db = await h.Factory.CreateDbContextAsync();
        var stored = await db.Collections.SingleAsync(c => c.Id == child.Id);
        stored.Name.Should().Be("Child");
        stored.ParentId.Should().Be(root.Id);
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Update_collection_rejects_normalized_equivalent_sibling_name()
    {
        var h = Harness();
        var target = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Sci-Fi"))).Data!;

        // The service converges normalized-equivalent CREATES on the existing
        // sibling, so the colliding sibling is seeded directly: a raw row
        // that is a normalized twin of the target at the same parent.
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Collections.Add(new CollectionModel { Name = "sci fi" });
            await db.SaveChangesAsync();
        }

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), target.Id, "Sci - Fi", null);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_name_conflict");
        (await GetCollectionDtoAsync(h, target.Id)).Name.Should().Be("Sci-Fi");
    }

    [Fact]
    public async Task Update_collection_move_rejects_normalized_equivalent_at_destination()
    {
        var h = Harness();
        var parent = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Parent"))).Data!;
        await h.Service.CreateCollectionAsync(new(Client, Key(), "Sci-Fi", parent.Id));
        var target = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "sci fi"))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), target.Id, target.Name, parent.Id);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_name_conflict");
        (await GetCollectionDtoAsync(h, target.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task Update_collection_combined_rename_and_move_checks_final_name_at_final_parent()
    {
        var h = Harness();
        var parentA = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Parent A"))).Data!;
        var parentB = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Parent B"))).Data!;
        var target = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Target", parentA.Id))).Data!;
        await h.Service.CreateCollectionAsync(new(Client, Key(), "Collision", parentB.Id));

        // "collision" is legal under parentA (no such sibling there) but the
        // move to parentB would collide — the WHOLE update must be rejected.
        var result = await h.Service.UpdateCollectionAsync(Client, Key(), target.Id, "Collision", parentB.Id);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_name_conflict");
        var stored = await GetCollectionDtoAsync(h, target.Id);
        stored.Name.Should().Be("Target");
        stored.ParentId.Should().Be(parentA.Id);
    }

    [Fact]
    public async Task Update_collection_same_name_allowed_under_different_parent()
    {
        var h = Harness();
        var parentA = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Parent A"))).Data!;
        var parentB = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Parent B"))).Data!;
        var target = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "One", parentA.Id))).Data!;
        await h.Service.CreateCollectionAsync(new(Client, Key(), "Shared", parentB.Id));

        var renamed = await h.Service.UpdateCollectionAsync(Client, Key(), target.Id, "Shared", parentA.Id);
        ((CollectionDto)renamed.Data!).Name.Should().Be("Shared", "no sibling under parentA collides");

        var moved = await h.Service.UpdateCollectionAsync(Client, Key(), target.Id, "Shared", parentB.Id);
        ((LibraryErrorDto)moved.Data!).Code.Should().Be("collection_name_conflict",
            "the same normalized name under a different parent is fine, but not as a sibling");
    }

    [Fact]
    public async Task Update_collection_move_to_root_rejects_root_sibling_collision()
    {
        var h = Harness();
        await h.Service.CreateCollectionAsync(new(Client, Key(), "Root Name"));
        var parent = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Parent"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root Name", parent.Id))).Data!;

        var result = await h.Service.UpdateCollectionAsync(Client, Key(), child.Id, child.Name, null);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_name_conflict");
        (await GetCollectionDtoAsync(h, child.Id)).ParentId.Should().Be(parent.Id);
    }

    [Fact]
    public async Task Concurrent_normalized_equivalent_creates_produce_one_collection()
    {
        var h = Harness();
        var results = await Task.WhenAll(
            h.Service.CreateCollectionAsync(new(Client, Key(), "Sci-Fi")),
            h.Service.CreateCollectionAsync(new(Client, Key(), "sci fi")));

        results.Select(r => r.StateVersion).Distinct().Should().HaveCount(1);
        results.Select(r => ((CollectionDto)r.Data!).Id).Distinct().Should().HaveCount(1,
            "the mutation gate serializes; the loser converges on the existing collection");
        (await CountCollectionsAsync(h)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_same_key_updates_converge_on_one_receipt_and_one_mutation()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child"))).Data!;
        var key = Key();

        var results = await Task.WhenAll(
            h.Service.UpdateCollectionAsync(Client, key, child.Id, "First", root.Id),
            h.Service.UpdateCollectionAsync(Client, key, child.Id, "Second", null));

        results.Count(r => r.Duplicate).Should().Be(1);
        var winner = results.Single(r => !r.Duplicate);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(3);
        var stored = await db.Collections.SingleAsync(c => c.Id == child.Id);
        stored.Name.Should().Be(((CollectionDto)winner.Data!).Name);
        stored.ParentId.Should().Be(((CollectionDto)winner.Data!).ParentId);
    }

    // ------------------------------------------------------------------
    // Collections Phase 1 — recursive filtering and counts
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_books_with_parent_collection_filter_includes_descendants_and_excludes_others()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;
        var grandchild = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Grandchild", child.Id))).Data!;
        var siblingRoot = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Sibling Root"))).Data!;

        await CreateBookInAsync(h, "Root Book", root.Id);
        await CreateBookInAsync(h, "Child Book", child.Id);
        await CreateBookInAsync(h, "Grandchild Book", grandchild.Id);
        await CreateBookInAsync(h, "Sibling Book", siblingRoot.Id);
        await h.Service.CreateOrMatchBookAsync(CreateRequest("physical", "Uncollected Book"), strictConfirmation: true);

        var rootPage = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.Recent, null, 1, 100, root.Id)).Data!;
        rootPage.TotalCount.Should().Be(3, "parent includes direct + child + grandchild books");
        rootPage.Items.Select(b => b.Title).Should().BeEquivalentTo(new[] { "Root Book", "Child Book", "Grandchild Book" });

        var childPage = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.Recent, null, 1, 100, child.Id)).Data!;
        childPage.TotalCount.Should().Be(2);
        childPage.Items.Select(b => b.Title).Should().BeEquivalentTo(new[] { "Child Book", "Grandchild Book" });

        var leafPage = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.Recent, null, 1, 100, grandchild.Id)).Data!;
        leafPage.TotalCount.Should().Be(1, "a leaf returns only its own books");
        leafPage.Items.Single().Title.Should().Be("Grandchild Book");

        var siblingPage = (PaginatedResponse<BookDto>)(await h.Service.ListBooksAsync(
            BookFilter.All, BookSort.Recent, null, 1, 100, siblingRoot.Id)).Data!;
        siblingPage.TotalCount.Should().Be(1, "ancestors, siblings and unrelated roots stay excluded");
        siblingPage.Items.Single().Title.Should().Be("Sibling Book");
    }

    [Fact]
    public async Task List_collection_counts_rolls_up_descendants_and_excludes_uncollected()
    {
        var h = Harness();
        var root = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Root"))).Data!;
        var child = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", root.Id))).Data!;
        var grandchild = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Grandchild", child.Id))).Data!;
        await CreateBookInAsync(h, "Root Book", root.Id);
        await CreateBookInAsync(h, "Child Book", child.Id);
        await CreateBookInAsync(h, "Grandchild Book", grandchild.Id);
        await h.Service.CreateOrMatchBookAsync(CreateRequest("physical", "Uncollected Book"), strictConfirmation: true);

        var result = await h.Service.ListCollectionCountsAsync();
        var counts = ((IEnumerable<CollectionCountDto>)result.Data!).ToDictionary(c => c.CollectionId, c => c.BookCount);

        counts[root.Id].Should().Be(3, "sidebar count = direct + all descendants");
        counts[child.Id].Should().Be(2);
        counts[grandchild.Id].Should().Be(1);
        // Each book is counted once per ancestor collection (root 3, child 2,
        // grandchild 1); the uncollected book contributes to NO collection.
        counts.Values.Sum().Should().Be(6);
    }

    [Fact]
    public async Task List_collection_counts_returns_zero_for_empty_collections()
    {
        var h = Harness();
        var empty = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Empty"))).Data!;

        var result = await h.Service.ListCollectionCountsAsync();
        var counts = ((IEnumerable<CollectionCountDto>)result.Data!).ToDictionary(c => c.CollectionId, c => c.BookCount);

        counts.Should().ContainKey(empty.Id);
        counts[empty.Id].Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Collections Phase 1 — restrictive foreign keys
    // ------------------------------------------------------------------

    [Fact]
    public async Task Direct_sql_delete_of_referenced_collection_is_rejected_by_foreign_keys()
    {
        var h = Harness();
        var parent = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Parent"))).Data!;
        await h.Service.CreateCollectionAsync(new(Client, Key(), "Child", parent.Id));
        await CreateBookInAsync(h, "Book", parent.Id);

        await using var db = await h.Factory.CreateDbContextAsync();

        // A collection referenced by a child cannot be deleted directly.
        var deleteParent = () => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"Collections\" WHERE \"Id\" = {0}", parent.Id);
        await deleteParent.Should().ThrowAsync<SqliteException>();

        // A collection referenced by a book cannot be deleted directly.
        await deleteParent.Should().ThrowAsync<SqliteException>();

        // An unreferenced empty leaf stays deletable directly.
        var leaf = (CollectionDto)(await h.Service.CreateCollectionAsync(new(Client, Key(), "Leaf"))).Data!;
        var deleted = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"Collections\" WHERE \"Id\" = {0}", leaf.Id);
        deleted.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Expert acceptance regressions (issue #34 review)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_title_only_with_one_existing_exact_title_strict_returns_confirmation()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations", Author: "Marcus Aurelius"), strictConfirmation: true);

        var result = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations"), strictConfirmation: true);

        result.Data.Should().BeOfType<LibraryConfirmationErrorDto>();
        ((LibraryConfirmationErrorDto)result.Data!).Code.Should().Be("confirmation_required",
            "a title-only request must never auto-match, even with exactly one exact title");
        ((LibraryConfirmationErrorDto)result.Data!).Candidates.Should().HaveCount(1);

        // Non-strict (legacy REST) still creates.
        var permissive = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations"), strictConfirmation: false);
        ((LibraryCreateOrMatchResultDto)permissive.Data!).Outcome.Should().Be("created");
    }

    [Fact]
    public async Task Resolve_title_only_single_exact_title_returns_candidates()
    {
        var h = Harness();
        await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Meditations", Author: "Marcus Aurelius"), strictConfirmation: true);

        var result = await h.Service.ResolveBookAsync(new LibraryResolveBookRequest(Title: "Meditations"));

        result.Resolution.Should().Be(LibraryResolution.Candidates);
        result.Candidates.Should().HaveCount(1);
        result.MatchedBook.Should().BeNull();
    }

    [Fact]
    public async Task Move_collection_into_sibling_with_same_name_returns_conflict()
    {
        var h = Harness();
        var rootA = ((CollectionDto)(await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), "Root A"))).Data!)!;
        await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), "Shared Name", rootA.Id));
        // Same name at a DIFFERENT parent is allowed by create...
        var rootB = ((CollectionDto)(await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), "Shared Name"))).Data!)!;

        // ...but moving it under root A collides with the existing sibling.
        var result = await h.Service.MoveCollectionAsync(
            new LibraryMoveCollectionRequest(Client, Key(), rootB.Id, rootA.Id));

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_name_conflict");
    }

    [Fact]
    public async Task Replay_returns_typed_payload_not_json_element()
    {
        var h = Harness();
        var request = CreateRequest("physical", "Typed Replay", Author: "Author");
        var first = await h.Service.CreateOrMatchBookAsync(request, strictConfirmation: true);
        var firstOutcome = (LibraryCreateOrMatchResultDto)first.Data!;

        var replay = await h.Service.CreateOrMatchBookAsync(request, strictConfirmation: true);

        replay.Duplicate.Should().BeTrue();
        replay.Data.Should().BeOfType<LibraryCreateOrMatchResultDto>(
            "receipt replay must return the exact stored typed payload");
        var replayedOutcome = (LibraryCreateOrMatchResultDto)replay.Data!;
        replayedOutcome.Outcome.Should().Be("created");
        replayedOutcome.BookId.Should().Be(firstOutcome.BookId);
    }

    [Fact]
    public async Task Create_with_type_incompatible_identifier_is_rejected()
    {
        var h = Harness();
        var audiobookWithIsbn = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "Audio", Isbn: IsbnValid), strictConfirmation: true);
        ((LibraryErrorDto)audiobookWithIsbn.Data!).Code.Should().Be("invalid_book_identity");

        var physicalWithAsin = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", "Paper", Asin: AsinExample), strictConfirmation: true);
        ((LibraryErrorDto)physicalWithAsin.Data!).Code.Should().Be("invalid_book_identity");

        var unknownType = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("hologram", "Ghost"), strictConfirmation: true);
        ((LibraryErrorDto)unknownType.Data!).Code.Should().Be("invalid_book_identity");

        (await CountBooksAsync(h)).Should().Be(0);
    }

    [Fact]
    public async Task Update_with_type_incompatible_identifier_is_rejected()
    {
        var h = Harness();
        var created = ((LibraryCreateOrMatchResultDto)(await h.Service.CreateOrMatchBookAsync(
            CreateRequest("audiobook", "Audio Book", Asin: AsinExample), strictConfirmation: true)).Data!)!;

        var result = await h.Service.UpdateBookAsync(new LibraryUpdateBookRequest(
            Client, Key(), created.BookId!.Value, Isbn: IsbnValid));

        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_book_identity");
    }

    [Fact]
    public async Task Backfill_throws_actionable_error_on_duplicate_identifiers()
    {
        var h = Harness();
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Books.AddRange(
                new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Dupe One", Isbn = "9780141183848" },
                new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Dupe Two", Isbn = "9780141183848" });
            await db.SaveChangesAsync();
        }

        var act = async () => await LibraryIdentityBackfill.BackfillAsync(
            await h.Factory.CreateDbContextAsync(), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Dupe One").And.Contain("Dupe Two")
            .And.Contain("9780141183848");
    }

    [Fact]
    public async Task Mutate_with_oversized_idempotency_key_returns_invalid_idempotency()
    {
        var h = Harness();
        var result = await h.Service.CreateOrMatchBookAsync(new LibraryCreateBookRequest(
            Client, new string('k', 200), "physical", "Too Big"), strictConfirmation: true);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("invalid_idempotency");
    }

    [Fact]
    public async Task Reads_do_not_create_library_state_row()
    {
        var h = Harness();
        await h.Service.ListBooksAsync(BookFilter.All, BookSort.Recent, null, 1, 20, null);
        await h.Service.ListCollectionsAsync();

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryStates.CountAsync()).Should().Be(0, "reads are strictly read-only");
    }

    [Fact]
    public async Task Move_collection_to_root_collides_with_root_sibling_name()
    {
        var h = Harness();
        await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), "Root Name"));
        var tmpParent = ((CollectionDto)(await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), "Tmp Parent"))).Data!)!;
        // Same name under a DIFFERENT parent is allowed by create...
        var child = ((CollectionDto)(await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), "Root Name", tmpParent.Id))).Data!)!;

        // ...but moving it to the root collides with the existing root sibling.
        var result = await h.Service.MoveCollectionAsync(
            new LibraryMoveCollectionRequest(Client, Key(), child.Id, null));

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_name_conflict",
            "the sibling-name rule applies at the root level too");
    }

    [Fact]
    public async Task Resolve_external_lookup_failure_surfaces_lookup_timeout()
    {
        var h = Harness(new ThrowingHttpClientFactory());
        var result = await h.Service.ResolveBookAsync(
            new LibraryResolveBookRequest(Isbn: IsbnBorges, IncludeExternalMetadata: true));

        result.Resolution.Should().Be(LibraryResolution.NotFound);
        result.Prefill.Should().BeNull();
        result.LookupError.Should().Be("lookup_timeout");
    }

    [Fact]
    public async Task Resolve_external_lookup_cancellation_rethrows()
    {
        var h = Harness(new ThrowingHttpClientFactory());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => h.Service.ResolveBookAsync(
            new LibraryResolveBookRequest(Isbn: IsbnBorges, IncludeExternalMetadata: true), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Resolve_external_lookup_cancellation_mid_request_rethrows()
    {
        // Locks the exact path the reviewer wanted: cancellation arrives WHILE
        // the provider request is in flight (not during the initial EF query),
        // and the OperationCanceledException still propagates instead of being
        // converted to lookup_timeout.
        using var cts = new CancellationTokenSource();
        var h = Harness(new CancelOnRequestHttpClientFactory(cts));

        var act = () => h.Service.ResolveBookAsync(
            new LibraryResolveBookRequest(Isbn: IsbnBorges, IncludeExternalMetadata: true), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private static LibraryCreateBookRequest CreateRequest(
        string type,
        string title,
        string? Author = null,
        string? Subtitle = null,
        string? Isbn = null,
        string? Asin = null,
        Guid? CollectionId = null,
        Guid? ConfirmedBookId = null,
        bool ForceCreate = false,
        string? Key = null,
        IReadOnlyList<Guid>? CollectionIds = null) =>
        new(Client, Key ?? $"k-{Guid.NewGuid():N}", type, title,
            Subtitle: Subtitle,
            Author: Author,
            Isbn: Isbn,
            Asin: Asin,
            CollectionId: CollectionId,
            ConfirmedBookId: ConfirmedBookId,
            ForceCreate: ForceCreate,
            CollectionIds: CollectionIds);

    private async Task<int> CountBooksAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Books.CountAsync();
    }

    private async Task<int> CountCollectionsAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Collections.CountAsync();
    }

    private async Task<BookDto> GetBookAsync(TestHarness h, Guid id)
    {
        var result = await h.Service.GetBookAsync(id);
        return (BookDto)result.Data!;
    }

    private async Task<CollectionDto> GetCollectionDtoAsync(TestHarness h, Guid id)
    {
        var result = await h.Service.GetCollectionAsync(id);
        return (CollectionDto)result.Data!;
    }

    private async Task<Guid> CreateBookInAsync(TestHarness h, string title, Guid collectionId)
    {
        // Permissive create (legacy REST semantics): a bare title creates.
        var created = await h.Service.CreateOrMatchBookAsync(
            CreateRequest("physical", title, CollectionId: collectionId), strictConfirmation: false);
        return ((LibraryCreateOrMatchResultDto)created.Data!).BookId!.Value;
    }

    private TestHarness Harness(IHttpClientFactory? lookupFactory = null)
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
        var lookup = new BookLookupService(lookupFactory ?? new NoopHttpClientFactory(), new SilentLogger<BookLookupService>());
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

    // Every request fails with a transport error, simulating unreachable
    // external metadata providers.
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("provider unreachable");
        }
    }

    // Cancels the CALLER's token the moment the first provider request
    // arrives, then fails — the mid-flight cancellation path.
    private sealed class CancelOnRequestHttpClientFactory : IHttpClientFactory
    {
        private readonly CancellationTokenSource _cts;

        public CancelOnRequestHttpClientFactory(CancellationTokenSource cts) => _cts = cts;

        public HttpClient CreateClient(string name) => new(new CancelOnRequestHandler(_cts));

        private sealed class CancelOnRequestHandler : HttpMessageHandler
        {
            private readonly CancellationTokenSource _cts;

            public CancelOnRequestHandler(CancellationTokenSource cts) => _cts = cts;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _cts.Cancel();
                throw new OperationCanceledException(_cts.Token);
            }
        }
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

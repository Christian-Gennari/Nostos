using System.Net.Http;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Changesets against a real SQLite file: a preview must leave no trace, an
/// apply must be all-or-nothing, and undo must restore exactly what it replaced
/// without overwriting anything changed since.
/// </summary>
public sealed class LibraryChangesetTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public LibraryChangesetTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_preview_reports_the_exact_diff_and_leaves_the_library_untouched()
    {
        var h = await SeedAsync();
        var before = await StateAsync(h);

        var preview = await h.Service.PreviewAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new CreateCollectionOp("stoicism", "Stoicism"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("stoicism"), [h.Meditations, h.Letters]),
        ]);

        preview.Success.Should().BeTrue();
        preview.Committed.Should().BeFalse();
        preview.Undo.Should().BeNull();
        var diff = preview.Diff!;
        diff.Collections.Select(c => (c.Kind, c.Name, c.BookCount)).Should().BeEquivalentTo(new[]
        {
            ("deleted", "Fiction", 2),
            ("deleted", "Russian", 1),
            ("created", "Stoicism", 2),
        });
        diff.Memberships.Should().ContainSingle(m => m.Title == "Anna Karenina")
            .Which.Removed.Should().BeEquivalentTo("Fiction", "Russian");
        diff.Memberships.Should().ContainSingle(m => m.Title == "Meditations")
            .Which.Added.Should().Equal("Stoicism");
        diff.Counts.CollectionsDeleted.Should().Be(2);
        diff.Counts.CollectionsCreated.Should().Be(1);
        diff.Counts.BooksLosingMembership.Should().Be(2);
        preview.DiffHash.Should().MatchRegex("^[0-9a-f]{64}$");

        (await StateAsync(h)).Should().BeEquivalentTo(before, "a preview is rolled back");
    }

    [Fact]
    public async Task Deleting_every_collection_and_regrouping_the_library_is_one_atomic_change()
    {
        var h = await SeedAsync();
        var versionBefore = await VersionAsync(h);

        var result = await h.Service.ApplyAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Philosophy)),
            new CreateCollectionOp("novels", "Novels"),
            new CreateCollectionOp("classics", "Classical thought"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("novels"), [h.AnnaKarenina, h.Dune]),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("classics"), [h.Meditations, h.Letters]),
        ], expectedDiffHash: null);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Committed.Should().BeTrue();
        result.Undo.Should().NotBeNull();

        var state = await StateAsync(h);
        state.Collections.Select(c => c.Name).Should().BeEquivalentTo("Novels", "Classical thought");
        state.MembershipNames(h.AnnaKarenina).Should().Equal("Novels");
        state.MembershipNames(h.Meditations).Should().Equal("Classical thought");
        long.Parse(await VersionAsync(h)).Should().Be(long.Parse(versionBefore) + 1,
            "one changeset advances the library revision once");
    }

    [Fact]
    public async Task A_failing_operation_rolls_back_every_operation_before_it()
    {
        var h = await SeedAsync();
        var before = await StateAsync(h);

        var result = await h.Service.ApplyAsync(
        [
            new CreateCollectionOp("new", "Brand new"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("new"), [h.Dune]),
            new RenameCollectionOp(LibraryCollectionRef.Existing(Guid.NewGuid()), "Nowhere"),
        ], expectedDiffHash: null);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("collection_not_found");
        result.FailedOperation.Should().Be(2);
        result.Committed.Should().BeFalse();
        (await StateAsync(h)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task The_same_changes_hash_the_same_although_new_collections_get_new_ids()
    {
        var h = await SeedAsync();
        LibraryChangeOp[] ops =
        [
            new CreateCollectionOp("sf", "Science fiction"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("sf"), [h.Dune]),
        ];

        var first = await h.Service.PreviewAsync(ops);
        var second = await h.Service.PreviewAsync(ops);
        first.Diff!.Collections.Single().Id.Should().NotBe(second.Diff!.Collections.Single().Id);
        second.DiffHash.Should().Be(first.DiffHash);

        var applied = await h.Service.ApplyAsync(ops, first.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        applied.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task An_apply_reviewed_against_an_older_library_is_refused_with_the_new_diff()
    {
        var h = await SeedAsync();
        LibraryChangeOp[] ops = [new RemoveBooksFromCollectionOp(LibraryCollectionRef.Existing(h.Fiction), [h.Dune])];
        var reviewed = await h.Service.PreviewAsync(ops);
        reviewed.Diff!.Memberships.Should().ContainSingle();

        // Someone else removes the book first: the same operation would now do nothing.
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.BookCollections.RemoveRange(db.BookCollections.Where(bc => bc.BookId == h.Dune));
            await db.SaveChangesAsync();
        }
        var before = await StateAsync(h);

        var result = await h.Service.ApplyAsync(ops, reviewed.DiffHash);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        result.Diff!.IsEmpty.Should().BeTrue();
        result.Committed.Should().BeFalse();
        (await StateAsync(h)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Creating_a_collection_that_already_exists_reuses_it()
    {
        var h = await SeedAsync();

        var result = await h.Service.ApplyAsync(
        [
            new CreateCollectionOp("fiction", "fiction"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("fiction"), [h.Meditations]),
        ], expectedDiffHash: null);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Diff!.Collections.Should().BeEmpty();
        (await StateAsync(h)).MembershipNames(h.Meditations).Should().BeEquivalentTo("Fiction", "Philosophy");
    }

    [Fact]
    public async Task Book_edits_show_each_changed_field()
    {
        var h = await SeedAsync();

        var preview = await h.Service.PreviewAsync(
            [new UpdateBookOp(h.Dune, Title: "Dune (Ace edition)", IsFinished: true, IsFavorite: true)]);

        var edit = preview.Diff!.BookEdits.Should().ContainSingle().Which;
        edit.Title.Should().Be("Dune");
        edit.Fields.Select(f => (f.Field, f.Before, f.After)).Should().BeEquivalentTo(new[]
        {
            ("Title", "Dune", "Dune (Ace edition)"),
            ("Favorite", "No", "Yes"),
            ("Finished", "No", "Yes"),
        });
    }

    [Fact]
    public async Task Invalid_changesets_are_refused_before_anything_runs()
    {
        var h = await SeedAsync();

        (await h.Service.PreviewAsync([])).ErrorCode.Should().Be(LibraryChangesetErrorCodes.Empty);
        (await h.Service.PreviewAsync(
            [new AddBooksToCollectionOp(LibraryCollectionRef.Created("later"), [h.Dune])]))
            .ErrorCode.Should().Be(LibraryChangesetErrorCodes.UnknownReference);
        (await h.Service.PreviewAsync(
            [new CreateCollectionOp("x", "One"), new CreateCollectionOp("x", "Two")]))
            .ErrorCode.Should().Be(LibraryChangesetErrorCodes.DuplicateReference);
        (await h.Service.PreviewAsync(
            [new AddBooksToCollectionOp(LibraryCollectionRef.Existing(h.Fiction),
                Enumerable.Range(0, LibraryService.MaxChangesetBooks + 1).Select(_ => Guid.NewGuid()).ToList())]))
            .ErrorCode.Should().Be(LibraryChangesetErrorCodes.TooLarge);
        (await h.Service.PreviewAsync(
            [new AddBooksToCollectionOp(LibraryCollectionRef.Existing(h.Fiction), [Guid.NewGuid()])]))
            .ErrorCode.Should().Be(LibraryChangesetErrorCodes.BookNotFound);
    }

    [Fact]
    public async Task Undo_restores_collections_memberships_and_book_fields_exactly()
    {
        var h = await SeedAsync();
        var original = await StateAsync(h);

        var applied = await h.Service.ApplyAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new RenameCollectionOp(LibraryCollectionRef.Existing(h.Philosophy), "Thought"),
            new CreateCollectionOp("sf", "Science fiction"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("sf"), [h.Dune]),
            new UpdateBookOp(h.Letters, Rating: 5, IsFinished: true),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().BeEmpty();
        (await StateAsync(h)).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Undo_leaves_anything_changed_since_and_says_so()
    {
        var h = await SeedAsync();

        var applied = await h.Service.ApplyAsync(
        [
            new CreateCollectionOp("sf", "Science fiction"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("sf"), [h.Dune, h.AnnaKarenina]),
        ], expectedDiffHash: null);
        var sf = applied.Diff!.Collections.Single().Id;

        // Afterwards the user adds Dune to Philosophy by hand.
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.BookCollections.Add(new BookCollectionModel { BookId = h.Dune, CollectionId = h.Philosophy });
            await db.SaveChangesAsync();
        }

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Conflicts.Should().Contain(c => c.Contains("Dune"));
        reverted.Conflicts.Should().Contain(c => c.Contains("Science fiction"));
        var state = await StateAsync(h);
        state.MembershipNames(h.AnnaKarenina).Should().BeEquivalentTo("Fiction", "Russian");
        state.MembershipNames(h.Dune).Should().BeEquivalentTo("Fiction", "Philosophy", "Science fiction");
        state.Collections.Should().Contain(c => c.Id == sf, "it still holds a book the user placed there");
    }

    // ------------------------------------------------------------------
    // Fixture
    // ------------------------------------------------------------------

    private sealed record Harness(
        IDbContextFactory<NostosDbContext> Factory,
        ILibraryChangesetService Service,
        Guid Fiction, Guid Russian, Guid Philosophy,
        Guid AnnaKarenina, Guid Dune, Guid Meditations, Guid Letters);

    /// <summary>
    /// Fiction (Anna Karenina, Dune) › Russian (Anna Karenina);
    /// Philosophy (Meditations); Letters from a Stoic in nothing.
    /// </summary>
    private async Task<Harness> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_fixture.CreateDatabasePath()}")
            .Options;
        await using (var bootstrap = new NostosDbContext(options))
            await bootstrap.Database.EnsureCreatedAsync();

        var fiction = new CollectionModel { Name = "Fiction" };
        var russian = new CollectionModel { Name = "Russian", ParentId = fiction.Id };
        var philosophy = new CollectionModel { Name = "Philosophy" };
        var anna = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Anna Karenina", Author = "Leo Tolstoy" };
        var dune = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Dune", Author = "Frank Herbert" };
        var meditations = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Meditations", Author = "Marcus Aurelius" };
        var letters = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Letters from a Stoic", Author = "Seneca" };

        await using (var db = new NostosDbContext(options))
        {
            db.Collections.AddRange(fiction, philosophy);
            await db.SaveChangesAsync();
            db.Collections.Add(russian);
            db.Books.AddRange(anna, dune, meditations, letters);
            await db.SaveChangesAsync();
            db.BookCollections.AddRange(
                new BookCollectionModel { BookId = anna.Id, CollectionId = fiction.Id },
                new BookCollectionModel { BookId = anna.Id, CollectionId = russian.Id },
                new BookCollectionModel { BookId = dune.Id, CollectionId = fiction.Id },
                new BookCollectionModel { BookId = meditations.Id, CollectionId = philosophy.Id });
            await db.SaveChangesAsync();
        }

        var factory = new TestContextFactory(options);
        var service = new LibraryService(factory,
            new BookLookupService(new NoopHttpClientFactory(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BookLookupService>.Instance));
        return new Harness(factory, service, fiction.Id, russian.Id, philosophy.Id,
            anna.Id, dune.Id, meditations.Id, letters.Id);
    }

    private sealed record LibraryState(
        IReadOnlyList<CollectionRow> Collections,
        IReadOnlyList<(Guid BookId, Guid CollectionId)> Memberships,
        IReadOnlyList<BookRow> Books)
    {
        public IReadOnlyList<string> MembershipNames(Guid bookId) =>
            Memberships.Where(m => m.BookId == bookId)
                .Select(m => Collections.Single(c => c.Id == m.CollectionId).Name)
                .Order()
                .ToList();
    }

    private sealed record CollectionRow(Guid Id, string Name, Guid? ParentId);

    private sealed record BookRow(Guid Id, string Title, int Rating, bool IsFavorite, DateTime? FinishedAt, int ProgressPercent);

    private static async Task<LibraryState> StateAsync(Harness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        var collections = await db.Collections.AsNoTracking()
            .OrderBy(c => c.Id).Select(c => new CollectionRow(c.Id, c.Name, c.ParentId)).ToListAsync();
        var memberships = (await db.BookCollections.AsNoTracking()
                .Select(bc => new { bc.BookId, bc.CollectionId }).ToListAsync())
            .Select(m => (m.BookId, m.CollectionId)).OrderBy(m => m.BookId).ThenBy(m => m.CollectionId).ToList();
        var books = await db.Books.AsNoTracking().OrderBy(b => b.Id)
            .Select(b => new BookRow(b.Id, b.Title, b.Work!.Rating, b.Work.IsFavorite,
                b.Progress.FinishedAt, b.Progress.ProgressPercent))
            .ToListAsync();
        return new LibraryState(collections, memberships, books);
    }

    private static async Task<string> VersionAsync(Harness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.LibraryStates.AsNoTracking().Select(s => s.StateVersion).SingleAsync();
    }

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

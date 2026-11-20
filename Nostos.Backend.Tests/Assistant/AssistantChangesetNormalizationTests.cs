using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// Deterministic server-side plan normalisation for Ask Nostos changesets (#1740):
/// equivalent regroup plans — whether the model deletes-and-recreates existing
/// collections, redundantly creates ones that exist, repeats steps, or includes
/// no-op memberships — collapse to the same minimal steps and diffHash.
/// </summary>
public sealed class AssistantChangesetNormalizationTests(SqliteTestFixture fixture)
    : IClassFixture<SqliteTestFixture>
{
    [Fact]
    public async Task Delete_and_recreate_regroup_collapses_to_same_plan_and_hash_as_direct_reuse()
    {
        var h1 = await SeedAsync();
        var h2 = await SeedAsync();
        var h3 = await SeedAsync();

        // Plan A: model deletes all three collections, recreates Fiction and Essays, and re-adds every book.
        var draftA = new AssistantChangesetDraft(h1.Library, h1.Library);
        await InvokeAsync(h1, draftA, "library_delete_collection", new { collection = h1.Craft });
        await InvokeAsync(h1, draftA, "library_delete_collection", new { collection = h1.Essays });
        await InvokeAsync(h1, draftA, "library_delete_collection", new { collection = h1.Fiction });
        await InvokeAsync(h1, draftA, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h1, draftA, "library_create_collection", new { name = "Essays" });
        await InvokeAsync(h1, draftA, "library_add_books_to_collection", new
        {
            collection = "Fiction",
            bookIds = new[] { h1.Dune, h1.Anna, h1.ValleyPaper },
        });
        await InvokeAsync(h1, draftA, "library_add_books_to_collection", new
        {
            collection = "Essays",
            bookIds = new[] { h1.QuietMornings, h1.WoodenBoats, h1.Granary },
        });

        // Plan B: model deletes Craft and Essays, calls create for both Fiction (still alive) and Essays (deleted).
        var draftB = new AssistantChangesetDraft(h2.Library, h2.Library);
        await InvokeAsync(h2, draftB, "library_delete_collection", new { collection = h2.Craft });
        await InvokeAsync(h2, draftB, "library_delete_collection", new { collection = h2.Essays });
        await InvokeAsync(h2, draftB, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h2, draftB, "library_create_collection", new { name = "Essays" });
        await InvokeAsync(h2, draftB, "library_add_books_to_collection", new
        {
            collection = "Fiction",
            bookIds = new[] { h2.Dune, h2.Anna, h2.ValleyPaper },
        });
        await InvokeAsync(h2, draftB, "library_add_books_to_collection", new
        {
            collection = "Essays",
            bookIds = new[] { h2.QuietMornings, h2.WoodenBoats, h2.Granary },
        });

        // Plan C: model directly deletes only Craft and adds only the books not already in Fiction/Essays.
        var draftC = new AssistantChangesetDraft(h3.Library, h3.Library);
        await InvokeAsync(h3, draftC, "library_delete_collection", new { collection = h3.Craft });
        await InvokeAsync(h3, draftC, "library_add_books_to_collection", new
        {
            collection = h3.Fiction,
            bookIds = new[] { h3.ValleyPaper },
        });
        await InvokeAsync(h3, draftC, "library_add_books_to_collection", new
        {
            collection = h3.Essays,
            bookIds = new[] { h3.WoodenBoats, h3.Granary },
        });

        foreach (var draft in new[] { draftA, draftB, draftC })
        {
            draft.Diff!.Counts.CollectionsDeleted.Should().Be(1, "only Craft is deleted; Fiction and Essays are reused");
            draft.Diff.Counts.CollectionsCreated.Should().Be(0);
            draft.Steps.Select(s => s.Summary).Should().Equal(
                "Delete collection “Craft”",
                "Add “The Valley Road” to “Fiction”",
                "Add 2 books to “Essays”");
        }

        draftA.DiffHash.Should().Be(draftC.DiffHash);
        draftB.DiffHash.Should().Be(draftC.DiffHash);

        // Applying Plan A keeps the original Fiction and Essays ids intact.
        var applied = await h1.Library.ApplyAsync(draftA.Steps.Select(s => s.Op).ToList(), draftA.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        await using var db = await h1.Factory.CreateDbContextAsync();
        var ids = await db.Collections.AsNoTracking().Select(c => c.Id).ToListAsync();
        ids.Should().BeEquivalentTo([h1.Fiction, h1.Essays]);
    }

    [Fact]
    public async Task Delete_and_recreate_that_moves_a_book_out_of_a_kept_collection_preserves_the_removal()
    {
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        // Delete and recreate Fiction, keeping only Dune in Fiction and moving Anna to Essays.
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Fiction });
        await InvokeAsync(h, draft, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { h.Dune } });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = h.Essays, bookIds = new[] { h.Anna } });

        draft.Diff!.Counts.CollectionsDeleted.Should().Be(0);
        draft.Diff.Counts.CollectionsCreated.Should().Be(0);
        draft.Steps.Select(s => s.Summary).Should().Equal(
            "Remove “Anna Karenina” from “Fiction”",
            "Add “Anna Karenina” to “Essays”");

        var applied = await h.Library.ApplyAsync(draft.Steps.Select(s => s.Op).ToList(), draft.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        await using var db = await h.Factory.CreateDbContextAsync();
        var annaCollections = await db.BookCollections.AsNoTracking()
            .Where(bc => bc.BookId == h.Anna)
            .Select(bc => bc.CollectionId)
            .ToListAsync();
        annaCollections.Should().Equal(h.Essays);
    }

    [Fact]
    public async Task Delete_and_recreate_on_parent_with_subcollection_deletes_unrequested_child_and_reuses_recreated_child()
    {
        // Case A: subcollection is NOT recreated -> parent reused, child deleted.
        var hA = await SeedAsync(withSubcollection: true);
        var draftA = new AssistantChangesetDraft(hA.Library, hA.Library);
        await InvokeAsync(hA, draftA, "library_delete_collection", new { collection = hA.Fiction });
        await InvokeAsync(hA, draftA, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(hA, draftA, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { hA.Dune, hA.Anna } });

        draftA.Diff!.Counts.CollectionsDeleted.Should().Be(1);
        draftA.Diff.Counts.CollectionsCreated.Should().Be(0);
        draftA.Steps.Select(s => s.Summary).Should().Equal("Delete collection “Russian”");

        // Case B: both parent AND subcollection are recreated -> both reused with zero deletes/creates.
        var hB = await SeedAsync(withSubcollection: true);
        var draftB = new AssistantChangesetDraft(hB.Library, hB.Library);
        await InvokeAsync(hB, draftB, "library_delete_collection", new { collection = hB.Fiction });
        await InvokeAsync(hB, draftB, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(hB, draftB, "library_create_collection", new { name = "Russian", parent = "Fiction" });
        await InvokeAsync(hB, draftB, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { hB.Dune, hB.Anna } });
        await InvokeAsync(hB, draftB, "library_add_books_to_collection", new { collection = "Russian", bookIds = new[] { hB.Anna } });

        draftB.IsEmpty.Should().BeTrue("recreating the exact same tree and memberships is a no-op");
    }

    [Fact]
    public async Task Duplicate_creates_deletes_adds_and_set_book_collections_are_deduplicated()
    {
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        // Duplicate create of a brand-new collection does not fail with DuplicateReference.
        var c1 = await InvokeAsync(h, draft, "library_create_collection", new { name = "Sea Stories" });
        var c2 = await InvokeAsync(h, draft, "library_create_collection", new { name = "sea stories" });
        c1.Success.Should().BeTrue();
        c2.Success.Should().BeTrue();

        // Duplicate delete of Craft.
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Craft });
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Craft });

        // Split and overlapping add calls for the same new collection collapse into one step.
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Sea Stories", bookIds = new[] { h.Dune, h.Dune } });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Sea Stories", bookIds = new[] { h.Dune, h.Anna } });

        draft.Steps.Select(s => s.Summary).Should().Equal(
            "Create collection “Sea Stories”",
            "Delete collection “Craft”",
            "Add 2 books to “Sea Stories”");
        draft.Steps[2].DependsOn.Should().Equal(0);
    }

    [Fact]
    public async Task Set_book_collections_drops_noops_and_deduplicates_repeated_updates_for_same_book()
    {
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        await InvokeAsync(h, draft, "library_set_book_collections", new
        {
            updates = new object[]
            {
                // Already in Fiction only -> no-op.
                new { bookId = h.Dune, collections = new[] { h.Fiction } },
                // Duplicate update for ValleyPaper -> only latest survives.
                new { bookId = h.ValleyPaper, collections = new[] { h.Essays } },
                new { bookId = h.ValleyPaper, collections = new[] { h.Fiction, h.Fiction } },
            },
        });

        draft.Steps.Select(s => s.Summary).Should().Equal(
            "Put “The Valley Road” in “Fiction” only");
    }

    [Fact]
    public async Task Pure_noop_stage_returns_no_change_and_keeps_draft_empty()
    {
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var createExisting = await InvokeAsync(h, draft, "library_create_collection", new { name = "Fiction" });
        createExisting.Success.Should().BeTrue();
        createExisting.Data!.Value.GetProperty("status").GetString().Should().Be("no_change");
        draft.IsEmpty.Should().BeTrue();

        var addAlreadyMember = await InvokeAsync(h, draft, "library_add_books_to_collection", new
        {
            collection = "Fiction",
            bookIds = new[] { h.Dune },
        });
        addAlreadyMember.Success.Should().BeTrue();
        addAlreadyMember.Data!.Value.GetProperty("status").GetString().Should().Be("no_change");
        draft.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_book_or_collection_ids_are_still_rejected_by_preview()
    {
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var badBook = await InvokeAsync(h, draft, "library_add_books_to_collection", new
        {
            collection = h.Fiction,
            bookIds = new[] { Guid.NewGuid() },
        });
        badBook.Success.Should().BeFalse();
        badBook.ErrorCode.Should().Be(LibraryChangesetErrorCodes.BookNotFound);

        var badCollection = await InvokeAsync(h, draft, "library_delete_collection", new
        {
            collection = Guid.NewGuid(),
        });
        badCollection.Success.Should().BeFalse();
        badCollection.ErrorCode.Should().Be(LibraryChangesetErrorCodes.CollectionNotFound);
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private sealed record Harness(
        IDbContextFactory<NostosDbContext> Factory,
        LibraryService Library,
        AssistantCapabilityRegistry Registry,
        Guid Fiction,
        Guid Essays,
        Guid Craft,
        Guid? Russian,
        Guid Dune,
        Guid Anna,
        Guid QuietMornings,
        Guid WoodenBoats,
        Guid Granary,
        Guid ValleyPaper);

    private static Task<AssistantToolResult> InvokeAsync(Harness h, AssistantChangesetDraft draft, string tool, object args)
    {
        var element = JsonSerializer.SerializeToElement(args);
        return h.Registry.InvokeAsync(tool, element, new AssistantToolContext("client", "stage", draft));
    }

    private async Task<Harness> SeedAsync(bool withSubcollection = false)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={fixture.CreateDatabasePath()}")
            .Options;
        await using (var bootstrap = new NostosDbContext(options))
            await bootstrap.Database.EnsureCreatedAsync();

        // Deterministic IDs so DiffHash matches across freshly seeded harnesses.
        var fiction = new CollectionModel { Id = Guid.Parse("c1111111-1111-4111-8111-111111111111"), Name = "Fiction" };
        var essays = new CollectionModel { Id = Guid.Parse("c2222222-2222-4222-8222-222222222222"), Name = "Essays" };
        var craft = new CollectionModel { Id = Guid.Parse("c3333333-3333-4333-8333-333333333333"), Name = "Craft" };
        var russian = withSubcollection
            ? new CollectionModel { Id = Guid.Parse("c4444444-4444-4444-8444-444444444444"), Name = "Russian", ParentId = fiction.Id }
            : null;

        var dune = new PhysicalBookModel { Id = Guid.Parse("a1111111-1111-4111-8111-111111111111"), Title = "Dune", Author = "Frank Herbert" };
        var anna = new PhysicalBookModel { Id = Guid.Parse("a2222222-2222-4222-8222-222222222222"), Title = "Anna Karenina", Author = "Leo Tolstoy" };
        var quiet = new PhysicalBookModel { Id = Guid.Parse("a3333333-3333-4333-8333-333333333333"), Title = "A Field Guide to Quiet Mornings", Author = "E. Solberg" };
        var boats = new PhysicalBookModel { Id = Guid.Parse("a4444444-4444-4444-8444-444444444444"), Title = "On Repairing Wooden Boats", Author = "A. Ek" };
        var granary = new PhysicalBookModel { Id = Guid.Parse("a5555555-5555-4555-8555-555555555555"), Title = "The Granary Ledger", Author = "M. Lindqvist" };
        var valley = new PhysicalBookModel { Id = Guid.Parse("a6666666-6666-4666-8666-666666666666"), Title = "The Valley Road", Author = "H. Berg" };

        await using (var db = new NostosDbContext(options))
        {
            db.Collections.AddRange(fiction, essays, craft);
            await db.SaveChangesAsync();
            if (russian is not null)
            {
                db.Collections.Add(russian);
                await db.SaveChangesAsync();
            }

            db.Books.AddRange(dune, anna, quiet, boats, granary, valley);
            await db.SaveChangesAsync();

            db.BookCollections.AddRange(
                new BookCollectionModel { BookId = dune.Id, CollectionId = fiction.Id },
                new BookCollectionModel { BookId = anna.Id, CollectionId = fiction.Id },
                new BookCollectionModel { BookId = quiet.Id, CollectionId = essays.Id },
                new BookCollectionModel { BookId = boats.Id, CollectionId = craft.Id },
                new BookCollectionModel { BookId = granary.Id, CollectionId = craft.Id });
            if (russian is not null)
                db.BookCollections.Add(new BookCollectionModel { BookId = anna.Id, CollectionId = russian.Id });
            await db.SaveChangesAsync();
        }

        var factory = new TestContextFactory(options);
        var library = LibraryServiceGraph.Create(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance));
        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(notes: null!, library: library, topics: null!));

        return new Harness(
            factory, library, registry,
            fiction.Id, essays.Id, craft.Id, russian?.Id,
            dune.Id, anna.Id, quiet.Id, boats.Id, granary.Id, valley.Id);
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

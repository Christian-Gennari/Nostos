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
/// Diff-based canonical plan derivation for Ask Nostos changesets (#1740):
/// equivalent regroup plans — whether the model deletes-and-recreates existing
/// collections, redundantly creates ones that exist, uses set_book_collections
/// vs add/remove, splits adds, or calls tools in a different order — collapse
/// to the same minimal steps, step order, and diffHash.
/// </summary>
public sealed class AssistantChangesetNormalizationTests(SqliteTestFixture fixture)
    : IClassFixture<SqliteTestFixture>
{
    [Fact]
    public async Task Equivalent_regroup_plans_collapse_to_identical_steps_order_and_diffHash()
    {
        var h1 = await SeedAsync();
        var h2 = await SeedAsync();
        var h3 = await SeedAsync();
        var h4 = await SeedAsync();

        // Plan A: delete all three collections, recreate Fiction and Essays, and re-add every book.
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

        // Plan B: delete Craft and Essays, redundantly create Fiction (still alive) and Essays,
        // and split/reverse the book additions.
        var draftB = new AssistantChangesetDraft(h2.Library, h2.Library);
        await InvokeAsync(h2, draftB, "library_delete_collection", new { collection = h2.Craft });
        await InvokeAsync(h2, draftB, "library_delete_collection", new { collection = h2.Essays });
        await InvokeAsync(h2, draftB, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h2, draftB, "library_create_collection", new { name = "Essays" });
        await InvokeAsync(h2, draftB, "library_add_books_to_collection", new
        {
            collection = "Essays",
            bookIds = new[] { h2.Granary },
        });
        await InvokeAsync(h2, draftB, "library_add_books_to_collection", new
        {
            collection = "Essays",
            bookIds = new[] { h2.WoodenBoats, h2.QuietMornings },
        });
        await InvokeAsync(h2, draftB, "library_add_books_to_collection", new
        {
            collection = "Fiction",
            bookIds = new[] { h2.ValleyPaper, h2.Anna, h2.Dune },
        });

        // Plan C: direct delete of Craft and minimal adds.
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

        // Plan D: delete Craft and replace memberships via library_set_book_collections.
        var draftD = new AssistantChangesetDraft(h4.Library, h4.Library);
        await InvokeAsync(h4, draftD, "library_delete_collection", new { collection = h4.Craft });
        await InvokeAsync(h4, draftD, "library_set_book_collections", new
        {
            updates = new object[]
            {
                new { bookId = h4.ValleyPaper, collections = new[] { h4.Fiction } },
                new { bookId = h4.Granary, collections = new[] { h4.Essays } },
                new { bookId = h4.WoodenBoats, collections = new[] { h4.Essays } },
                new { bookId = h4.Dune, collections = new[] { h4.Fiction } },
            },
        });

        foreach (var draft in new[] { draftA, draftB, draftC, draftD })
        {
            draft.Diff!.Counts.CollectionsDeleted.Should().Be(1, "only Craft is deleted; Fiction and Essays are reused");
            draft.Diff.Counts.CollectionsCreated.Should().Be(0);
            draft.Steps.Select(s => s.Summary).Should().Equal(
                "Delete collection “Craft”",
                "Add 2 books to “Essays”",
                "Add “The Valley Road” to “Fiction”");
            draft.DiffHash.Should().Be(draftC.DiffHash);
        }

        // Book ids inside each AddBooksToCollectionOp are also in a fixed title order.
        var essaysAddA = draftA.Steps[1].Op.Should().BeOfType<AddBooksToCollectionOp>().Subject;
        var essaysAddB = draftB.Steps[1].Op.Should().BeOfType<AddBooksToCollectionOp>().Subject;
        essaysAddA.BookIds.Should().Equal(h1.WoodenBoats, h1.Granary);
        essaysAddB.BookIds.Should().Equal(h2.WoodenBoats, h2.Granary);

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
    public async Task Move_child_out_before_delete_and_recreate_parent_keeps_and_moves_child()
    {
        // Review defect 1: moving "Sea Stories" out of Fiction into Keep before deleting and
        // recreating Fiction must move "Sea Stories" into Keep, NOT delete it.
        var h = await SeedAsync(withSubcollection: true);
        var seaStoriesId = h.Russian!.Value;
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var sub = await db.Collections.SingleAsync(c => c.Id == seaStoriesId);
            sub.Name = "Sea Stories";
            await db.SaveChangesAsync();
        }

        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        await InvokeAsync(h, draft, "library_move_collection", new { collection = seaStoriesId, newParent = h.Essays });
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Fiction });
        await InvokeAsync(h, draft, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { h.Dune, h.Anna } });

        draft.Diff!.Counts.CollectionsDeleted.Should().Be(0);
        draft.Diff.Counts.CollectionsCreated.Should().Be(0);
        draft.Steps.Select(s => s.Summary).Should().Equal("Move “Sea Stories” into “Essays”");

        var applied = await h.Library.ApplyAsync(draft.Steps.Select(s => s.Op).ToList(), draft.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var sea = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == seaStoriesId);
            sea.Should().NotBeNull("Sea Stories was moved out before Fiction was deleted");
            sea!.ParentId.Should().Be(h.Essays);
        }
    }

    [Fact]
    public async Task Move_child_out_before_delete_parent_sets_dependency_and_accurate_summary()
    {
        var h = await SeedAsync(withSubcollection: true);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        await InvokeAsync(h, draft, "library_move_collection", new { collection = h.Russian!.Value, newParent = h.Essays });
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Fiction });

        draft.Steps.Select(s => s.Summary).Should().Equal(
            "Move “Russian” into “Essays”",
            "Delete collection “Fiction”");
        draft.Steps[1].DependsOn.Should().Contain(0,
            "excluding the move out of Fiction must also exclude deleting Fiction");
    }

    [Fact]
    public async Task Move_child_into_parent_before_delete_and_recreate_parent_deletes_moved_child()
    {
        // Review defect 1 (opposite): moving a child from Essays into Fiction before deleting
        // and recreating Fiction must delete that child while reusing Fiction.
        var h = await SeedAsync();
        var seaStories = new CollectionModel
        {
            Id = Guid.Parse("c5555555-5555-4555-8555-555555555555"),
            Name = "Sea Stories",
            ParentId = h.Essays,
        };
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Collections.Add(seaStories);
            await db.SaveChangesAsync();
        }

        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        await InvokeAsync(h, draft, "library_move_collection", new { collection = seaStories.Id, newParent = h.Fiction });
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Fiction });
        await InvokeAsync(h, draft, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { h.Dune, h.Anna } });

        draft.Diff!.Counts.CollectionsDeleted.Should().Be(1);
        draft.Diff.Counts.CollectionsCreated.Should().Be(0);
        draft.Steps.Select(s => s.Summary).Should().Equal("Delete collection “Sea Stories”");

        var applied = await h.Library.ApplyAsync(draft.Steps.Select(s => s.Op).ToList(), draft.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            (await db.Collections.AsNoTracking().AnyAsync(c => c.Id == seaStories.Id)).Should().BeFalse();
            (await db.Collections.AsNoTracking().AnyAsync(c => c.Id == h.Fiction)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Child_created_under_parent_before_delete_and_recreate_does_not_survive()
    {
        // Review defect 2: a child created under Fiction this turn and cascade-deleted with
        // Fiction must NOT survive when Fiction is recreated.
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        await InvokeAsync(h, draft, "library_create_collection", new { name = "Old", parent = h.Fiction });
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Fiction });
        await InvokeAsync(h, draft, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { h.Dune, h.Anna } });

        draft.IsEmpty.Should().BeTrue("Old was cascade-deleted and Fiction ended with its original books");
        draft.Diff.Should().BeNull();
    }

    [Fact]
    public async Task Books_added_before_delete_and_recreate_do_not_stay_in_recreated_collection()
    {
        // Review defect 3: books added to Fiction BEFORE Fiction is deleted and recreated
        // were wiped by the delete and must NOT remain in Fiction unless re-added afterwards.
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = h.Fiction, bookIds = new[] { h.ValleyPaper } });
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Fiction });
        await InvokeAsync(h, draft, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { h.Dune } });

        draft.Diff!.Counts.CollectionsDeleted.Should().Be(0);
        draft.Diff.Counts.CollectionsCreated.Should().Be(0);
        draft.Steps.Select(s => s.Summary).Should().Equal("Remove “Anna Karenina” from “Fiction”");

        var applied = await h.Library.ApplyAsync(draft.Steps.Select(s => s.Op).ToList(), draft.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        await using var db = await h.Factory.CreateDbContextAsync();
        var fictionBooks = await db.BookCollections.AsNoTracking()
            .Where(bc => bc.CollectionId == h.Fiction)
            .Select(bc => bc.BookId)
            .ToListAsync();
        fictionBooks.Should().Equal(h.Dune);
    }

    [Fact]
    public async Task Failed_operation_in_multi_item_addition_after_collapsed_steps_reports_exact_part_number()
    {
        // Review defect 4: even after earlier raw operations collapse to 0 canonical steps,
        // a failure inside a new multi-item batch must report the exact 1-based part of the batch.
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        // Raw ops = 3, canonical steps = 0 (net no-op).
        await InvokeAsync(h, draft, "library_delete_collection", new { collection = h.Fiction });
        await InvokeAsync(h, draft, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = "Fiction", bookIds = new[] { h.Dune, h.Anna } });
        draft.IsEmpty.Should().BeTrue();

        var bad = await InvokeAsync(h, draft, "library_set_book_collections", new
        {
            updates = new object[]
            {
                new { bookId = h.Dune, collections = new[] { "Fiction" } },
                new { bookId = Guid.NewGuid(), collections = new[] { "Fiction" } },
                new { bookId = h.Anna, collections = new[] { "Fiction" } },
            },
        });

        bad.Success.Should().BeFalse();
        bad.ErrorCode.Should().Be(LibraryChangesetErrorCodes.BookNotFound);
        bad.ErrorMessage.Should().Contain("(in the change you just asked for, part 2)");
    }

    [Fact]
    public async Task Deleted_named_collection_alias_is_cleared_and_cannot_be_referenced_later()
    {
        // Review defect 5: a name alias must not keep pointing to a collection after it is deleted.
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var created = await InvokeAsync(h, draft, "library_create_collection", new { name = "Temp" });
        created.Success.Should().BeTrue();
        var deleted = await InvokeAsync(h, draft, "library_delete_collection", new { collection = "Temp" });
        deleted.Success.Should().BeTrue();
        draft.IsEmpty.Should().BeTrue();

        var staleUse = await InvokeAsync(h, draft, "library_add_books_to_collection", new
        {
            collection = "Temp",
            bookIds = new[] { h.Dune },
        });
        staleUse.Success.Should().BeFalse();
        staleUse.ErrorCode.Should().Be(LibraryChangesetErrorCodes.CollectionNotFound);

        // Recreating "Temp" after deleting it binds the alias to the new live collection.
        var recreated = await InvokeAsync(h, draft, "library_create_collection", new { name = "Temp" });
        recreated.Success.Should().BeTrue();
        var freshUse = await InvokeAsync(h, draft, "library_add_books_to_collection", new
        {
            collection = "Temp",
            bookIds = new[] { h.Dune },
        });
        freshUse.Success.Should().BeTrue();
        draft.Steps.Select(s => s.Summary).Should().Equal(
            "Create collection “Temp”",
            "Add “Dune” to “Temp”");

        // Also covers an alias created by reusing an existing collection that is later deleted by id.
        var draft2 = new AssistantChangesetDraft(h.Library, h.Library);
        await InvokeAsync(h, draft2, "library_create_collection", new { name = "Fiction" });
        await InvokeAsync(h, draft2, "library_delete_collection", new { collection = h.Fiction });
        var staleExisting = await InvokeAsync(h, draft2, "library_add_books_to_collection", new
        {
            collection = "Fiction",
            bookIds = new[] { h.Dune },
        });
        staleExisting.Success.Should().BeFalse();
        staleExisting.ErrorCode.Should().Be(LibraryChangesetErrorCodes.CollectionNotFound);
    }

    [Fact]
    public async Task Duplicate_creates_deletes_adds_and_set_book_collections_are_canonicalized()
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
            "Delete collection “Craft”",
            "Create collection “Sea Stories”",
            "Add 2 books to “Sea Stories”");
        draft.Steps[2].DependsOn.Should().Equal(1);
    }

    [Fact]
    public async Task Set_book_collections_drops_noops_and_emits_canonical_per_collection_ops()
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
            "Add “The Valley Road” to “Fiction”");
    }

    [Fact]
    public async Task Book_edits_are_coalesced_and_preserved_alongside_collection_changes()
    {
        var h = await SeedAsync();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        await InvokeAsync(h, draft, "library_update_book", new { bookId = h.Dune, rating = 4, isFavorite = true });
        await InvokeAsync(h, draft, "library_update_book", new { bookId = h.Dune, rating = 5 });
        await InvokeAsync(h, draft, "library_add_books_to_collection", new { collection = h.Essays, bookIds = new[] { h.ValleyPaper } });

        draft.Steps.Select(s => s.Summary).Should().Equal(
            "Add “The Valley Road” to “Essays”",
            "Edit “Dune”: rating, mark favourite");

        var editOp = draft.Steps[1].Op.Should().BeOfType<UpdateBookOp>().Subject;
        editOp.Rating.Should().Be(5);
        editOp.IsFavorite.Should().BeTrue();
        editOp.Title.Should().BeNull();
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

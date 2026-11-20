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

    [Fact]
    public async Task Undo_restores_collections_and_memberships_after_delete_and_recreate_with_same_names()
    {
        var h = await SeedAsync();
        var original = await StateAsync(h);

        // Regroup plan that deletes existing collections and recreates them with the same names (#1739).
        var applied = await h.Service.ApplyAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Philosophy)),
            new CreateCollectionOp("fiction", "Fiction"),
            new CreateCollectionOp("philosophy", "Philosophy"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("fiction"), [h.AnnaKarenina, h.Dune]),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("philosophy"), [h.Meditations, h.Letters]),
        ], expectedDiffHash: null);

        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var afterApply = await StateAsync(h);
        afterApply.Collections.Select(c => c.Name).Should().BeEquivalentTo("Fiction", "Philosophy");
        afterApply.Collections.Select(c => c.Id).Should().NotContain(h.Fiction);
        afterApply.Collections.Select(c => c.Id).Should().NotContain(h.Philosophy);

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().BeEmpty();
        (await StateAsync(h)).Should().BeEquivalentTo(original,
            "undo must remove the recreated collections first and bring back the original ids, hierarchy, and memberships");
    }

    [Fact]
    public async Task Undo_restores_nested_collections_deleted_and_recreated_with_same_names()
    {
        var h = await SeedAsync();
        var original = await StateAsync(h);

        var applied = await h.Service.ApplyAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new CreateCollectionOp("fiction", "Fiction"),
            new CreateCollectionOp("russian", "Russian", LibraryCollectionRef.Created("fiction")),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("fiction"), [h.Dune]),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("russian"), [h.AnnaKarenina]),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().BeEmpty();
        (await StateAsync(h)).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Undo_restores_existing_child_reparented_under_recreated_same_name_parent()
    {
        var h = await SeedAsync();
        var original = await StateAsync(h);

        var applied = await h.Service.ApplyAsync(
        [
            new MoveCollectionOp(LibraryCollectionRef.Existing(h.Russian), null),
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new CreateCollectionOp("fiction", "Fiction"),
            new MoveCollectionOp(LibraryCollectionRef.Existing(h.Russian), LibraryCollectionRef.Created("fiction")),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("fiction"), [h.AnnaKarenina, h.Dune]),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().BeEmpty();
        (await StateAsync(h)).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Undo_restores_collections_whose_names_were_swapped()
    {
        var h = await SeedAsync();
        var original = await StateAsync(h);

        var applied = await h.Service.ApplyAsync(
        [
            new RenameCollectionOp(LibraryCollectionRef.Existing(h.Fiction), "Temp"),
            new RenameCollectionOp(LibraryCollectionRef.Existing(h.Philosophy), "Fiction"),
            new RenameCollectionOp(LibraryCollectionRef.Existing(h.Fiction), "Philosophy"),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().BeEmpty();
        (await StateAsync(h)).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Undo_keeps_recreated_collection_when_user_added_a_book_to_it_after_apply()
    {
        var h = await SeedAsync();

        var applied = await h.Service.ApplyAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new CreateCollectionOp("fiction", "Fiction"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("fiction"), [h.Dune]),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var recreatedFiction = applied.Diff!.Collections.Single(c => c.Kind == "created").Id;

        // After apply, the user places another book into the recreated collection.
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.BookCollections.Add(new BookCollectionModel { BookId = h.Letters, CollectionId = recreatedFiction });
            await db.SaveChangesAsync();
        }

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().Contain(c => c.Contains("Fiction") && c.Contains("books were added"));
        reverted.Conflicts.Should().Contain(c => c.Contains("Did not restore \"Fiction\""));
        reverted.Conflicts.Should().Contain(c => c.Contains("Dune"));
        var state = await StateAsync(h);
        state.Collections.Should().Contain(c => c.Id == recreatedFiction);
        state.MembershipNames(h.Letters).Should().Equal("Fiction");
        state.MembershipNames(h.Dune).Should().Equal("Fiction");
        state.Memberships.Should().Contain((h.Dune, recreatedFiction),
            "partial undo must keep Dune in its still-existing after collection rather than stranding it");
    }

    [Fact]
    public async Task Undo_keeps_created_collection_when_user_renamed_it_after_apply()
    {
        var h = await SeedAsync();

        var applied = await h.Service.ApplyAsync(
        [
            new CreateCollectionOp("sf", "Science fiction"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("sf"), [h.Dune]),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var createdId = applied.Diff!.Collections.Single(c => c.Kind == "created").Id;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var model = await db.Collections.SingleAsync(c => c.Id == createdId);
            model.Name = "Speculative fiction";
            await db.SaveChangesAsync();
        }

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().Contain(c => c.Contains("Speculative fiction") && c.Contains("changed after"));
        var state = await StateAsync(h);
        state.Collections.Should().Contain(c => c.Id == createdId && c.Name == "Speculative fiction");
        state.MembershipNames(h.Dune).Should().Equal("Fiction");
    }

    [Fact]
    public async Task Undo_keeps_created_collection_when_user_moved_it_after_apply()
    {
        var h = await SeedAsync();

        var applied = await h.Service.ApplyAsync(
        [
            new CreateCollectionOp("sf", "Science fiction"),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var createdId = applied.Diff!.Collections.Single(c => c.Kind == "created").Id;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var model = await db.Collections.SingleAsync(c => c.Id == createdId);
            model.ParentId = h.Fiction;
            await db.SaveChangesAsync();
        }

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().Contain(c => c.Contains("Science fiction") && c.Contains("changed after"));
        var state = await StateAsync(h);
        state.Collections.Should().Contain(c => c.Id == createdId && c.ParentId == h.Fiction);
    }

    [Fact]
    public async Task Undo_keeps_every_book_in_its_created_collection_when_the_old_name_was_taken_after_apply()
    {
        var h = await SeedAsync();

        // Plan: delete Fiction, create "Novels", put Dune and Anna in it (#1760).
        var applied = await h.Service.ApplyAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Russian)),
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new CreateCollectionOp("novels", "Novels"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("novels"), [h.Dune, h.AnnaKarenina]),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var novels = applied.Diff!.Collections.Single(c => c.Kind == "created").Id;

        // The user makes a new, empty "Fiction" before undoing.
        var userFiction = Guid.NewGuid();
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Collections.Add(new CollectionModel { Id = userFiction, Name = "Fiction" });
            await db.SaveChangesAsync();
        }

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        var state = await StateAsync(h);
        state.Collections.Should().Contain(c => c.Id == novels, "Dune and Anna cannot go back to Fiction, so Novels must stay");
        state.Collections.Should().Contain(c => c.Id == userFiction, "a collection the user made is never removed");
        state.Collections.Should().NotContain(c => c.Id == h.Fiction);
        state.MembershipNames(h.Dune).Should().Equal("Novels");
        state.MembershipNames(h.AnnaKarenina).Should().Equal("Novels");
        reverted.Conflicts.Should().Contain(c => c.Contains("\"Dune\"") && c.Contains("could not be restored"));
        reverted.Conflicts.Should().Contain(c => c.Contains("\"Anna Karenina\"") && c.Contains("could not be restored"));
        reverted.Conflicts.Should().Contain(c => c.Contains("Novels"));
        reverted.Conflicts.Should().NotContain(c => c.Contains("books were added"), "nobody added a book after the apply");
    }

    [Fact]
    public async Task Undo_removes_the_created_collection_only_when_no_book_is_stranded_by_it()
    {
        var h = await SeedAsync();

        // Dune and Anna move to "Novels"; Meditations moves to "Essays". Only Fiction's name is taken later.
        var applied = await h.Service.ApplyAsync(
        [
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Russian)),
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Fiction)),
            new DeleteCollectionOp(LibraryCollectionRef.Existing(h.Philosophy)),
            new CreateCollectionOp("novels", "Novels"),
            new CreateCollectionOp("essays", "Essays"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("novels"), [h.Dune, h.AnnaKarenina]),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("essays"), [h.Meditations]),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var novels = applied.Diff!.Collections.Single(c => c.Kind == "created" && c.Name == "Novels").Id;
        var essays = applied.Diff.Collections.Single(c => c.Kind == "created" && c.Name == "Essays").Id;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Collections.Add(new CollectionModel { Id = Guid.NewGuid(), Name = "Fiction" });
            await db.SaveChangesAsync();
        }

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        var state = await StateAsync(h);
        state.Collections.Should().Contain(c => c.Id == novels);
        state.Collections.Should().NotContain(c => c.Id == essays, "Meditations went back to Philosophy, so Essays is empty and removed");
        state.MembershipNames(h.Dune).Should().Equal("Novels");
        state.MembershipNames(h.AnnaKarenina).Should().Equal("Novels");
        state.Memberships.Should().Contain((h.Meditations, h.Philosophy));
        state.MembershipNames(h.Meditations).Should().Equal("Philosophy");
        reverted.Conflicts.Should().NotContain(c => c.Contains("Meditations"));
        foreach (var book in new[] { h.Dune, h.AnnaKarenina, h.Meditations })
            state.Memberships.Should().Contain(m => m.BookId == book, "undo never leaves a book in no collection");
    }

    [Fact]
    public async Task Undo_reports_conflict_if_step_2e_restored_collection_still_has_a_same_name_sibling()
    {
        var h = await SeedAsync();

        // Set up a post-apply state where a recreated Fiction holds a reparented child "Russian"
        // while a second moved collection also wants to return to old Fiction as "Russian",
        // blocking the first child from vacating recreated Fiction after step 2e restores old Fiction.
        var recreatedFictionId = Guid.NewGuid();
        var secondRussianId = Guid.NewGuid();
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var russian = await db.Collections.SingleAsync(c => c.Id == h.Russian);
            var oldFiction = await db.Collections.SingleAsync(c => c.Id == h.Fiction);
            var recreatedFiction = new CollectionModel { Id = recreatedFictionId, Name = "Fiction", ParentId = null };
            var secondRussian = new CollectionModel { Id = secondRussianId, Name = "Russian", ParentId = h.Philosophy };
            db.Collections.AddRange(recreatedFiction, secondRussian);
            await db.SaveChangesAsync();

            russian.ParentId = recreatedFictionId;
            await db.SaveChangesAsync();

            db.BookCollections.RemoveRange(db.BookCollections.Where(bc => bc.CollectionId == h.Fiction));
            db.Collections.Remove(oldFiction);
            await db.SaveChangesAsync();
        }

        var undo = new LibraryUndoSnapshot(
            Collections:
            [
                new CollectionSnapshot(h.Fiction, new CollectionState("Fiction", null), null),
                new CollectionSnapshot(recreatedFictionId, null, new CollectionState("Fiction", null)),
                new CollectionSnapshot(secondRussianId, new CollectionState("Russian", h.Fiction), new CollectionState("Russian", h.Philosophy)),
                new CollectionSnapshot(h.Russian, new CollectionState("Russian", h.Fiction), new CollectionState("Russian", recreatedFictionId)),
            ],
            Memberships: [],
            Books: []);

        var reverted = await h.Service.RevertAsync(undo);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().Contain(c => c.Contains("Fiction") && c.Contains("same name"));
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
        var service = LibraryServiceGraph.Create(factory,
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

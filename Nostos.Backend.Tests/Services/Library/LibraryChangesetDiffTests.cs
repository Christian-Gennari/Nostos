using FluentAssertions;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// The diff fingerprint a reviewed changeset is pinned to (issue #1611): the
/// same change hashes the same, and any change to what it would do changes the
/// hash.
/// </summary>
public sealed class LibraryChangesetDiffTests
{
    private static readonly LibraryChangesetCountsDto Counts = new(
        CollectionsCreated: 1,
        CollectionsDeleted: 0,
        CollectionsUpdated: 0,
        BooksTouched: 1,
        MembershipsAdded: 1,
        MembershipsRemoved: 0,
        BooksLosingMembership: 0,
        BookEdits: 1);

    [Fact]
    public void The_same_diff_hashes_the_same()
    {
        LibraryChangesetDiff.Hash(Diff()).Should().Be(LibraryChangesetDiff.Hash(Diff()));
    }

    [Fact]
    public void A_created_collection_is_identified_by_name_not_by_its_new_id()
    {
        var first = Diff(createdCollectionId: Guid.NewGuid());
        var second = Diff(createdCollectionId: Guid.NewGuid());

        LibraryChangesetDiff.Hash(first).Should().Be(LibraryChangesetDiff.Hash(second));
    }

    [Fact]
    public void Changing_a_membership_changes_the_hash()
    {
        var other = Diff();
        other = other with
        {
            Memberships =
            [
                new LibraryMembershipChangeDto(Guid.Empty, "A Book", null, ["History"], []),
            ],
        };

        LibraryChangesetDiff.Hash(Diff()).Should().NotBe(LibraryChangesetDiff.Hash(other));
    }

    [Fact]
    public void Changing_a_book_field_changes_the_hash()
    {
        var other = Diff() with
        {
            BookEdits =
            [
                new LibraryBookEditDto(Guid.Empty, "A Book", [new LibraryFieldChangeDto("Rating", "0", "5")]),
            ],
        };

        LibraryChangesetDiff.Hash(Diff()).Should().NotBe(LibraryChangesetDiff.Hash(other));
    }

    [Fact]
    public void A_reader_claiming_a_staged_topic_changes_the_hash()
    {
        var kindId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var assignment = new LibraryTopicKindAssignmentDto(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Freire",
            kindId,
            "Idea",
            "Idea",
            "assistant",
            StepIndex: 0,
            KindColorKey: "kind-1",
            PreviousSource: "assistant");

        var beforeClaim = Diff() with { TopicKindAssignments = [assignment] };
        var afterClaim = Diff() with
        {
            TopicKindAssignments = [assignment with { PreviousSource = "reader" }],
        };

        // The kind and its after-source are identical; only who set it before
        // moved. An apply must notice, or it overwrites the reader's claim.
        LibraryChangesetDiff.Hash(beforeClaim).Should().NotBe(LibraryChangesetDiff.Hash(afterClaim));
    }

    [Fact]
    public void A_diff_from_a_host_without_the_appended_fields_is_empty_and_does_not_throw()
    {
        var diff = new LibraryChangesetDiffDto([], [], [], Counts, null!, null!);

        diff.IsEmpty.Should().BeTrue();
    }

    private static LibraryChangesetDiffDto Diff(Guid? createdCollectionId = null) =>
        new(
            [
                new LibraryCollectionChangeDto(
                    createdCollectionId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "created", "Fiction", null, null, null, false, 1),
            ],
            [new LibraryMembershipChangeDto(Guid.Empty, "A Book", null, ["Fiction"], [])],
            [new LibraryBookEditDto(Guid.Empty, "A Book", [new LibraryFieldChangeDto("Title", "Old", "A Book")])],
            Counts,
            [],
            []);
}

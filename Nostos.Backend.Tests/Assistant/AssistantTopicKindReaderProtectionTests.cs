using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The reader kinds protection carried by a staged changeset (issue #1770
/// review, round 2). The plan used to name every topic it touched, so a reader
/// who claimed a staged topic after the turn ended — or after the preview — had
/// their kind silently overwritten by the apply. Now an unnamed topic's op
/// carries an empty named set, which protects it at every later recompute.
/// </summary>
public sealed class AssistantTopicKindReaderProtectionTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantTopicKindReaderProtectionTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_reader_claim_made_before_the_preview_keeps_the_kind_the_reader_set()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await StageAsync(h, draft, freire, "Idea");
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-1", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue("the configured kind limit sends this to review");

        // The reader gives the staged topic the same kind, claiming it as their
        // own, between the turn and the review.
        await ClaimAsync(h, freire.Id, TopicKindModel.IdeaId, TopicKindSources.Reader);

        var preview = await h.Coordinator.PreviewAsync(
            review.Changeset!.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, []),
            default);

        preview.Success.Should().BeTrue(preview.ErrorMessage);
        preview.Changeset!.Diff.TopicKindAssignments.Should().BeEmpty(
            "the reader's kind is protected, so the preview has no assignment to show");
        // The one step now does nothing, and the review says so instead of
        // offering it: there is nothing left to apply.
        preview.Changeset.Steps.Should().ContainSingle();
        preview.Changeset.Steps[0].Included.Should().BeFalse();
        preview.Changeset.Steps[0].SkippedReason.Should().Be("kept: you set this kind yourself");

        var applied = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken, [], preview.Changeset.DiffHash),
            default);

        applied.Success.Should().BeFalse("the apply would change nothing");
        applied.ErrorCode.Should().Be(LibraryChangesetErrorCodes.Empty);
        var topic = await TopicAsync(h, freire.Id);
        topic.KindId.Should().Be(TopicKindModel.IdeaId);
        topic.KindSource.Should().Be(TopicKindSources.Reader, "the apply must not claim the reader's kind");
    }

    [Fact]
    public async Task An_untyped_topic_the_reader_claims_before_the_apply_keeps_the_readers_kind()
    {
        using var h = CreateHarness();
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await StageAsync(h, draft, montessori, "Idea");
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-2", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();

        // The preview the reader saw; then they claim the topic as Person.
        var preview = await h.Coordinator.PreviewAsync(
            review.Changeset!.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, []),
            default);
        preview.Success.Should().BeTrue(preview.ErrorMessage);
        await ClaimAsync(h, montessori.Id, TopicKindModel.PersonId, TopicKindSources.Reader);

        var applied = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken, [], preview.Changeset!.DiffHash),
            default);

        // The change would now do something else, so the apply sends a new
        // review rather than writing. What matters is that it did not write
        // over the reader's Person kind.
        applied.Success.Should().BeFalse();
        applied.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        var topic = await TopicAsync(h, montessori.Id);
        topic.KindId.Should().Be(TopicKindModel.PersonId);
        topic.KindSource.Should().Be(TopicKindSources.Reader);
    }

    [Fact]
    public async Task When_every_staged_topic_is_claimed_the_review_says_it_is_kept_and_a_second_apply_does_nothing()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await StageAsync(h, draft, freire, "Idea");
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-a", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();
        var preview = await h.Coordinator.PreviewAsync(
            review.Changeset!.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, []),
            default);
        preview.Success.Should().BeTrue(preview.ErrorMessage);
        preview.Changeset!.Diff.TopicKindAssignments.Should().ContainSingle();

        // The reader claims the only staged topic between the preview and the
        // apply, so the apply protects it and the change would now do nothing.
        await ClaimAsync(h, freire.Id, TopicKindModel.IdeaId, TopicKindSources.Reader);

        var refused = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken, [], preview.Changeset.DiffHash),
            default);

        refused.Success.Should().BeFalse();
        refused.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        var refreshed = refused.Changeset!;
        refreshed.Status.Should().Be("review");
        refreshed.CanUndo.Should().BeFalse();

        // The card is not left with a step that looks applicable: the diff is
        // empty, and the one step says why it does nothing.
        refreshed.Diff.IsEmpty.Should().BeTrue();
        refreshed.Steps.Should().ContainSingle();
        refreshed.Steps[0].Included.Should().BeFalse();
        refreshed.Steps[0].SkippedReason.Should().Be("kept: you set this kind yourself");
        refreshed.ReviewReasons.Should().NotContain(reason => reason.Contains("Creates the topic kind"));

        // A second apply cannot commit an empty diff, and never reports an undo
        // for something that did not happen.
        var second = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken,
                refreshed.Steps.Where(step => !step.Included).Select(step => step.Index).ToList(),
                refreshed.DiffHash),
            default);

        second.Success.Should().BeFalse();
        second.ErrorCode.Should().Be(LibraryChangesetErrorCodes.Empty);
        second.Changeset!.Status.Should().Be("review");
        second.Changeset.CanUndo.Should().BeFalse();
        var topic = await TopicAsync(h, freire.Id);
        topic.KindId.Should().Be(TopicKindModel.IdeaId);
        topic.KindSource.Should().Be(TopicKindSources.Reader);
    }

    [Fact]
    public async Task A_partial_claim_keeps_one_topic_and_lists_only_the_applied_one()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await StageAsync(h, draft, [freire, montessori], "Idea");
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-b", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();
        var preview = await h.Coordinator.PreviewAsync(
            review.Changeset!.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, []),
            default);
        preview.Success.Should().BeTrue(preview.ErrorMessage);
        preview.Changeset!.Diff.TopicKindAssignments.Should().HaveCount(2);

        await ClaimAsync(h, freire.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        var refused = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken, [], preview.Changeset.DiffHash),
            default);

        refused.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        var refreshed = refused.Changeset!;
        // The headline counts what would still happen, and the group says why
        // the reader's own topic is not among it.
        refreshed.Diff.TopicKindAssignments.Should().ContainSingle()
            .Which.TopicId.Should().Be(montessori.Id);
        var freireStep = refreshed.Steps.Single(step => step.TopicKind?.TopicId == freire.Id);
        freireStep.Included.Should().BeFalse();
        freireStep.SkippedReason.Should().Be("kept: you set this kind yourself");
        var montessoriStep = refreshed.Steps.Single(step => step.TopicKind?.TopicId == montessori.Id);
        montessoriStep.Included.Should().BeTrue();

        var applied = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken,
                refreshed.Steps.Where(step => !step.Included).Select(step => step.Index).ToList(),
                refreshed.DiffHash),
            default);

        applied.Success.Should().BeTrue(applied.ErrorMessage);
        applied.Changeset!.Status.Should().Be("applied");
        // The details are built from the applied diff: the protected topic was
        // kept, never applied, so it is not listed.
        applied.Changeset.Diff.TopicKindAssignments.Should().ContainSingle()
            .Which.TopicId.Should().Be(montessori.Id);
        var kept = await TopicAsync(h, freire.Id);
        kept.KindId.Should().Be(TopicKindModel.PersonId);
        kept.KindSource.Should().Be(TopicKindSources.Reader);
    }

    [Fact]
    public async Task A_created_kind_whose_only_topic_is_claimed_is_never_created_empty()
    {
        using var h = CreateHarness();
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await StageAsync(h, draft, montessori, "School of thought", createKind: true);
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-c", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();
        review.Changeset!.ReviewReasons.Should()
            .Contain(reason => reason.Contains("Creates the topic kind"));

        var preview = await h.Coordinator.PreviewAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, []),
            default);
        preview.Success.Should().BeTrue(preview.ErrorMessage);
        preview.Changeset!.Diff.TopicKinds.Should().ContainSingle();

        await ClaimAsync(h, montessori.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        var refused = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken, [], preview.Changeset.DiffHash),
            default);

        refused.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        var refreshed = refused.Changeset!;
        refreshed.Diff.IsEmpty.Should().BeTrue("the kind is only there for a topic that is already sorted");
        refreshed.Diff.TopicKinds.Should().BeEmpty();
        refreshed.Steps.Should().OnlyContain(step => !step.Included && step.SkippedReason != null);
        // The reason for the created kind is gone with the kind itself.
        refreshed.ReviewReasons.Should().NotContain(reason => reason.Contains("Creates the topic kind"));

        var second = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken,
                refreshed.Steps.Select(step => step.Index).ToList(),
                refreshed.DiffHash),
            default);

        second.ErrorCode.Should().Be(LibraryChangesetErrorCodes.Empty);
        (await KindCountAsync(h, "School of thought")).Should().Be(0,
            "an empty kind must never be created");
    }

    [Fact]
    public async Task A_topic_the_reader_claims_after_the_apply_is_not_touched_by_undo()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await StageAsync(h, draft, freire, "Idea");
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-3", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();

        var applied = await h.Coordinator.ApplyAsync(
            review.Changeset!.ChangesetId,
            new AssistantChangesetApplyRequest(review.Changeset.ActionToken, [], review.Changeset.DiffHash),
            default);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        await ClaimAsync(h, freire.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        var undone = await h.Coordinator.UndoAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetTokenRequest(review.Changeset.ActionToken),
            default);

        undone.Success.Should().BeTrue();
        undone.Conflicts.Should().ContainSingle()
            .Which.Should().Be("Kept the kind of “Freire”: it was changed after this was applied.");
        var topic = await TopicAsync(h, freire.Id);
        topic.KindId.Should().Be(TopicKindModel.PersonId);
        topic.KindSource.Should().Be(TopicKindSources.Reader);
    }

    [Fact]
    public async Task A_standalone_created_kind_stays_included_when_another_step_is_unticked()
    {
        using var h = CreateHarness();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var collection = await h.Registry.InvokeAsync(
            "library_create_collection",
            Args("""{"name":"Reading"}"""),
            new AssistantToolContext("client", "stage", draft, "Create Reading and Movement."));
        collection.Success.Should().BeTrue(collection.ErrorMessage);
        var kind = await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"Movement"}"""),
            new AssistantToolContext("client", "stage", draft, "Create Reading and Movement."));
        kind.Success.Should().BeTrue(kind.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-standalone", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();
        var collectionStep = review.Changeset!.Steps.Single(step => step.TopicKind is null);
        var kindStep = review.Changeset.Steps.Single(step => step.TopicKind?.CreatesKind == true);

        var preview = await h.Coordinator.PreviewAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, [collectionStep.Index]),
            default);

        preview.Success.Should().BeTrue(preview.ErrorMessage);
        preview.Changeset!.Diff.IsEmpty.Should().BeFalse();
        preview.Changeset.Diff.TopicKinds.Should().ContainSingle(k => k.Name == "Movement");
        var refreshedKindStep = preview.Changeset.Steps.Single(step => step.Index == kindStep.Index);
        refreshedKindStep.Included.Should().BeTrue();
        refreshedKindStep.SkippedReason.Should().BeNull();
    }

    [Fact]
    public async Task Unticking_a_topic_claiming_its_sibling_and_ticking_it_back_restores_the_topic_and_its_created_kind()
    {
        using var h = CreateHarness();
        var dewey = await SeedTopicAsync(h, "Dewey");
        var freire = await SeedTopicAsync(h, "Freire");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await StageAsync(h, draft, [dewey, freire], "School of thought", createKind: true);
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-retick", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();
        var deweyStep = review.Changeset!.Steps.Single(step => step.TopicKind?.TopicId == dewey.Id);
        var freireStep = review.Changeset.Steps.Single(step => step.TopicKind?.TopicId == freire.Id);
        var createStep = review.Changeset.Steps.Single(step => step.TopicKind?.CreatesKind == true);

        var unticked = await h.Coordinator.PreviewAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, [deweyStep.Index]),
            default);
        unticked.Success.Should().BeTrue(unticked.ErrorMessage);

        await ClaimAsync(h, freire.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        var refused = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken, [deweyStep.Index], unticked.Changeset!.DiffHash),
            default);

        refused.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        refused.Changeset!.Diff.IsEmpty.Should().BeTrue();
        refused.Changeset.Steps.Single(s => s.Index == freireStep.Index).SkippedReason
            .Should().Be("kept: you set this kind yourself");
        refused.Changeset.Steps.Single(s => s.Index == createStep.Index).SkippedReason
            .Should().Be("kept: no topic still needs this kind");

        // Tick Dewey back on: a client that passes every non-included step except Dewey
        // (including the kept sibling and empty create step) must still restore Dewey and its kind.
        var excludedAfterRetick = refused.Changeset.Steps
            .Where(step => !step.Included && step.Index != deweyStep.Index)
            .Select(step => step.Index)
            .ToList();
        var reticked = await h.Coordinator.PreviewAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, excludedAfterRetick),
            default);

        reticked.Success.Should().BeTrue(reticked.ErrorMessage);
        reticked.Changeset!.Diff.TopicKinds.Should().ContainSingle(k => k.Name == "School of thought");
        reticked.Changeset.Diff.TopicKindAssignments.Should().ContainSingle(a => a.TopicId == dewey.Id);
        reticked.Changeset.Steps.Single(s => s.Index == createStep.Index).Included.Should().BeTrue();
        reticked.Changeset.Steps.Single(s => s.Index == createStep.Index).SkippedReason.Should().BeNull();
        reticked.Changeset.Steps.Single(s => s.Index == deweyStep.Index).Included.Should().BeTrue();
        reticked.Changeset.Steps.Single(s => s.Index == freireStep.Index).Included.Should().BeFalse();
        reticked.Changeset.Steps.Single(s => s.Index == freireStep.Index).SkippedReason
            .Should().Be("kept: you set this kind yourself");

        var applied = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(
                review.Changeset.ActionToken, [], reticked.Changeset.DiffHash),
            default);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var appliedFreire = applied.Changeset!.Steps.Single(s => s.Index == freireStep.Index);
        appliedFreire.Included.Should().BeFalse();
        appliedFreire.SkippedReason.Should().Be("kept: you set this kind yourself");
        (await KindCountAsync(h, "School of thought")).Should().Be(1);
        (await TopicAsync(h, dewey.Id)).KindId.Should().NotBeNull();
        (await TopicAsync(h, freire.Id)).KindId.Should().Be(TopicKindModel.PersonId);
    }

    [Fact]
    public async Task Toggling_an_unrelated_step_preserves_earlier_kept_reasons()
    {
        using var h = CreateHarness();
        var dewey = await SeedTopicAsync(h, "Dewey");
        var freire = await SeedTopicAsync(h, "Freire");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        (await h.Registry.InvokeAsync(
            "library_create_collection",
            Args("""{"name":"Reading"}"""),
            new AssistantToolContext("client", "stage", draft, "Create Reading and sort topics."))).Success.Should().BeTrue();
        (await StageAsync(h, draft, [dewey, freire], "Idea")).Success.Should().BeTrue();

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-toggle-keep", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();
        var collectionStep = review.Changeset!.Steps.Single(step => step.TopicKind is null);
        var freireStep = review.Changeset.Steps.Single(step => step.TopicKind?.TopicId == freire.Id);

        await ClaimAsync(h, freire.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        var afterClaim = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(review.Changeset.ActionToken, [], review.Changeset.DiffHash),
            default);
        afterClaim.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        afterClaim.Changeset!.Steps.Single(s => s.Index == freireStep.Index).SkippedReason
            .Should().Be("kept: you set this kind yourself");

        var afterToggle = await h.Coordinator.PreviewAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, [collectionStep.Index, freireStep.Index]),
            default);

        afterToggle.Success.Should().BeTrue(afterToggle.ErrorMessage);
        var keptFreire = afterToggle.Changeset!.Steps.Single(s => s.Index == freireStep.Index);
        keptFreire.Included.Should().BeFalse();
        keptFreire.SkippedReason.Should().Be("kept: you set this kind yourself");
    }

    [Fact]
    public async Task A_second_claim_before_the_second_apply_keeps_both_reasons()
    {
        using var h = CreateHarness();
        var dewey = await SeedTopicAsync(h, "Dewey");
        var freire = await SeedTopicAsync(h, "Freire");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        (await StageAsync(h, draft, [dewey, freire], "Idea")).Success.Should().BeTrue();

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-two-claims", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();
        var deweyStep = review.Changeset!.Steps.Single(step => step.TopicKind?.TopicId == dewey.Id);
        var freireStep = review.Changeset.Steps.Single(step => step.TopicKind?.TopicId == freire.Id);

        await ClaimAsync(h, freire.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        var first = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(review.Changeset.ActionToken, [], review.Changeset.DiffHash),
            default);
        first.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);

        await ClaimAsync(h, dewey.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        var second = await h.Coordinator.ApplyAsync(
            review.Changeset.ChangesetId,
            new AssistantChangesetApplyRequest(review.Changeset.ActionToken, [], first.Changeset!.DiffHash),
            default);

        second.ErrorCode.Should().Be(LibraryChangesetErrorCodes.DiffChanged);
        var keptFreire = second.Changeset!.Steps.Single(s => s.Index == freireStep.Index);
        var keptDewey = second.Changeset.Steps.Single(s => s.Index == deweyStep.Index);
        keptFreire.Included.Should().BeFalse();
        keptFreire.SkippedReason.Should().Be("kept: you set this kind yourself");
        keptDewey.Included.Should().BeFalse();
        keptDewey.SkippedReason.Should().Be("kept: you set this kind yourself");
    }

    [Fact]
    public async Task An_auto_apply_claim_during_the_turn_reconciles_against_the_staged_diff()
    {
        // Default AutoApplyTopicKinds is 10: a small classification tries to
        // auto-apply, and a claim during the turn sends it to review.
        using var h = CreateHarness(autoApplyTopicKinds: 10);
        var freire = await SeedTopicAsync(h, "Freire");
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        (await StageAsync(h, draft, [freire, montessori], "Idea")).Success.Should().BeTrue();

        await ClaimAsync(h, freire.Id, TopicKindModel.PersonId, TopicKindSources.Reader);

        var outcome = await h.Coordinator.FinishTurnAsync("conversation", "turn-auto-claim", draft, false, false, default);

        outcome.AwaitingReview.Should().BeTrue();
        var review = outcome.Changeset!;
        review.ReviewReasons.Should().Contain(AssistantChangesetCoordinator.LibraryMovedReason);
        review.Diff.TopicKindAssignments.Should().ContainSingle(a => a.TopicId == montessori.Id);

        var freireStep = review.Steps.Single(step => step.TopicKind?.TopicId == freire.Id);
        freireStep.Included.Should().BeFalse();
        freireStep.SkippedReason.Should().Be("kept: you set this kind yourself");

        var montessoriStep = review.Steps.Single(step => step.TopicKind?.TopicId == montessori.Id);
        montessoriStep.Included.Should().BeTrue();
        montessoriStep.SkippedReason.Should().BeNull();
    }

    [Fact]
    public async Task A_protected_clear_reports_that_the_reader_set_the_kind_themselves()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.IdeaId, TopicKindSources.Assistant);
        var alreadyClear = await SeedTopicAsync(h, "Montessori", TopicKindModel.IdeaId, TopicKindSources.Assistant);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id, alreadyClear.Id },
                clear = true,
            })),
            new AssistantToolContext("client", "stage", draft, "Clear those topics."));
        staged.Success.Should().BeTrue(staged.ErrorMessage);

        var review = await h.Coordinator.FinishTurnAsync("conversation", "turn-clear", draft, false, false, default);
        review.AwaitingReview.Should().BeTrue();

        await ClaimAsync(h, freire.Id, TopicKindModel.PersonId, TopicKindSources.Reader);
        await ClaimAsync(h, alreadyClear.Id, null, null);

        var preview = await h.Coordinator.PreviewAsync(
            review.Changeset!.ChangesetId,
            new AssistantChangesetPreviewRequest(review.Changeset.ActionToken, []),
            default);

        preview.Success.Should().BeTrue(preview.ErrorMessage);
        var freireStep = preview.Changeset!.Steps.Single(step => step.TopicKind?.TopicId == freire.Id);
        freireStep.Included.Should().BeFalse();
        freireStep.SkippedReason.Should().Be("kept: you set this kind yourself");

        var clearStep = preview.Changeset.Steps.Single(step => step.TopicKind?.TopicId == alreadyClear.Id);
        clearStep.Included.Should().BeFalse();
        clearStep.SkippedReason.Should().Be("kept: the topic already has no kind");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static Task<AssistantToolResult> StageAsync(
        Harness h,
        AssistantChangesetDraft draft,
        TopicModel topic,
        string kind,
        bool createKind = false) =>
        StageAsync(h, draft, [topic], kind, createKind);

    /// <summary>
    /// Stages one kind for every topic, creating the kind first when asked. The
    /// request never names a topic, so every reader kind is protected.
    /// </summary>
    private static async Task<AssistantToolResult> StageAsync(
        Harness h,
        AssistantChangesetDraft draft,
        IReadOnlyList<TopicModel> topics,
        string kind,
        bool createKind = false)
    {
        if (createKind)
        {
            var created = await h.Registry.InvokeAsync(
                "topics_create_kind",
                Args(JsonSerializer.Serialize(new { name = kind })),
                new AssistantToolContext("client", "stage", draft, "Give my unkinded topics a kind."));
            if (!created.Success)
                return created;
        }

        return await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = topics.Select(topic => topic.Id).ToArray(),
                kind,
            })),
            new AssistantToolContext("client", "stage", draft, "Give my unkinded topics a kind."));
    }

    private static async Task<int> KindCountAsync(Harness h, string name)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.TopicKinds.CountAsync(kind => kind.Name == name);
    }

    private static async Task ClaimAsync(Harness h, Guid topicId, Guid? kindId, string? source)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        var topic = await db.Topics.SingleAsync(candidate => candidate.Id == topicId);
        topic.KindId = kindId;
        topic.KindSource = source;
        await db.SaveChangesAsync();
    }

    private static async Task<TopicModel> TopicAsync(Harness h, Guid topicId)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Topics.AsNoTracking().SingleAsync(topic => topic.Id == topicId);
    }

    private Harness CreateHarness(int autoApplyTopicKinds = 0)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_fixture.CreateDatabasePath()}")
            .Options;
        using (var bootstrap = new NostosDbContext(options))
            bootstrap.Database.EnsureCreated();

        var factory = new TestContextFactory(options);
        var db = new NostosDbContext(options);
        var topics = new TopicRepository(db);
        var library = LibraryServiceGraph.Create(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance));
        var registry = new AssistantCapabilityRegistry(AssistantCapabilities.Build(
            notes: null!,
            library,
            topics));

        // Any kind change on a turn sends it to review, so the tests control
        // when the preview and the apply happen.
        var coordinator = new AssistantChangesetCoordinator(
            new AssistantChangesetStore(),
            library,
            new AssistantOptions { AutoApplyTopicKinds = autoApplyTopicKinds });

        return new Harness(db, factory, library, registry, coordinator);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<TopicModel> SeedTopicAsync(
        Harness h,
        string name,
        Guid? kindId = null,
        string? kindSource = null)
    {
        var topic = new TopicModel { Topic = name, KindId = kindId, KindSource = kindSource };
        h.Db.Topics.Add(topic);
        await h.Db.SaveChangesAsync();
        return topic;
    }

    private sealed record Harness(
        NostosDbContext Db,
        IDbContextFactory<NostosDbContext> Factory,
        LibraryService Library,
        AssistantCapabilityRegistry Registry,
        AssistantChangesetCoordinator Coordinator) : IDisposable
    {
        public void Dispose() => Db.Dispose();
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

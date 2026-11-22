using FluentAssertions;
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
/// Topic kinds through the changeset (issue #1770): staged and previewed like
/// every other library change, applied with the assistant as the source, and
/// taken back by undo, including removing a kind the changeset created once no
/// topic uses it.
/// </summary>
public sealed class LibraryChangesetTopicKindTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public LibraryChangesetTopicKindTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_preview_reports_a_new_kind_and_the_topics_it_classifies_without_changing_anything()
    {
        var h = await SeedAsync();

        var preview = await h.Service.PreviewAsync(
        [
            new CreateTopicKindOp("school", "School of thought"),
            new SetTopicKindOp([h.Freire, h.Montessori], TopicKindRef.Created("school")),
        ]);

        preview.Success.Should().BeTrue(preview.ErrorMessage);
        preview.Committed.Should().BeFalse();
        var diff = preview.Diff!;
        diff.TopicKinds.Should().ContainSingle()
            .Which.Should().Match<LibraryTopicKindChangeDto>(kind =>
                kind.Kind == "created"
                && kind.Name == "School of thought"
                && kind.ColorKey == "kind-7"
                && kind.TopicCount == 2);
        diff.TopicKindAssignments.Select(a => (a.TopicName, a.KindName, a.Source))
            .Should().BeEquivalentTo(new[]
            {
                ("Freire", "School of thought", TopicKindSources.Assistant),
                ("Montessori", "School of thought", TopicKindSources.Assistant),
            });
        diff.Counts.TopicKindsCreated.Should().Be(1);
        diff.Counts.TopicKindChanges.Should().Be(2);

        await AssertUnchangedAsync(h);
    }

    [Fact]
    public async Task An_unknown_topic_is_refused_at_staging()
    {
        var h = await SeedAsync();

        var result = await h.Service.PreviewAsync(
        [
            new SetTopicKindOp([h.Freire, Guid.NewGuid()], TopicKindRef.Existing(TopicKindModel.PersonId)),
        ]);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(LibraryChangesetErrorCodes.TopicNotFound);
        result.FailedOperation.Should().Be(0);
    }

    [Fact]
    public async Task A_thirteenth_kind_is_refused_at_staging()
    {
        var h = await SeedAsync(extraKinds: 6);

        var result = await h.Service.PreviewAsync([new CreateTopicKindOp("extra", "Extra kind")]);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(LibraryChangesetErrorCodes.TopicKindLimit);
    }

    [Fact]
    public async Task A_duplicate_kind_name_is_refused_at_staging()
    {
        var h = await SeedAsync();

        var result = await h.Service.PreviewAsync([new CreateTopicKindOp("person", "person")]);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(LibraryChangesetErrorCodes.TopicKindDuplicate);
        result.ErrorMessage.Should().Contain("already exists");
    }

    [Fact]
    public async Task Applying_marks_the_kind_as_set_by_the_assistant()
    {
        var h = await SeedAsync();

        var applied = await h.Service.ApplyAsync(
        [
            new CreateTopicKindOp("school", "School of thought"),
            new SetTopicKindOp([h.Freire], TopicKindRef.Created("school")),
        ], expectedDiffHash: null);

        applied.Success.Should().BeTrue(applied.ErrorMessage);
        applied.Committed.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        var topic = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == h.Freire);
        topic.KindId.Should().NotBeNull();
        topic.KindSource.Should().Be(TopicKindSources.Assistant);
        (await db.TopicKinds.AsNoTracking().SingleAsync(k => k.Id == topic.KindId)).Name
            .Should().Be("School of thought");
    }

    [Fact]
    public async Task A_kind_the_reader_set_is_kept_unless_the_changeset_was_allowed_to_replace_it()
    {
        var h = await SeedAsync();

        var kept = await h.Service.ApplyAsync(
            [new SetTopicKindOp([h.Person], TopicKindRef.Existing(TopicKindModel.PlaceId))],
            expectedDiffHash: null);

        kept.Success.Should().BeTrue(kept.ErrorMessage);
        kept.Diff!.TopicKindAssignments.Should().BeEmpty();

        var replaced = await h.Service.ApplyAsync(
            [new SetTopicKindOp([h.Person], TopicKindRef.Existing(TopicKindModel.PlaceId), OverwriteReaderKinds: true)],
            expectedDiffHash: null);

        replaced.Success.Should().BeTrue(replaced.ErrorMessage);
        await using var db = await h.Factory.CreateDbContextAsync();
        var topic = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == h.Person);
        topic.KindId.Should().Be(TopicKindModel.PlaceId);
        topic.KindSource.Should().Be(TopicKindSources.Assistant);
    }

    [Fact]
    public async Task Undo_restores_the_previous_kinds_and_sources_and_removes_a_created_kind()
    {
        var h = await SeedAsync();

        var applied = await h.Service.ApplyAsync(
        [
            new CreateTopicKindOp("school", "School of thought"),
            new SetTopicKindOp([h.Freire], TopicKindRef.Created("school")),
            new SetTopicKindOp([h.Montessori], null),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        applied.Undo.Should().NotBeNull();

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        reverted.Conflicts.Should().BeEmpty();

        await using var db = await h.Factory.CreateDbContextAsync();
        var freire = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == h.Freire);
        freire.KindId.Should().BeNull("Freire had no kind before");
        freire.KindSource.Should().BeNull();

        var montessori = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == h.Montessori);
        montessori.KindId.Should().Be(TopicKindModel.IdeaId);
        montessori.KindSource.Should().Be(TopicKindSources.Assistant);

        (await db.TopicKinds.AsNoTracking().AnyAsync(k => k.Name == "School of thought"))
            .Should().BeFalse("no topic uses it any more");
    }

    [Fact]
    public async Task A_created_kind_stays_when_another_topic_started_using_it()
    {
        var h = await SeedAsync();

        var applied = await h.Service.ApplyAsync(
        [
            new CreateTopicKindOp("school", "School of thought"),
            new SetTopicKindOp([h.Freire], TopicKindRef.Created("school")),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);
        var kindId = applied.Undo!.TopicKinds.Single().After!.Name == "School of thought"
            ? applied.Undo.TopicKinds.Single().Id
            : Guid.Empty;

        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var montessori = await db.Topics.SingleAsync(t => t.Id == h.Montessori);
            montessori.KindId = kindId;
            montessori.KindSource = TopicKindSources.Reader;
            await db.SaveChangesAsync();
        }

        var reverted = await h.Service.RevertAsync(applied.Undo!);

        reverted.Success.Should().BeTrue();
        await using var check = await h.Factory.CreateDbContextAsync();
        (await check.TopicKinds.AsNoTracking().AnyAsync(k => k.Id == kindId))
            .Should().BeTrue("another topic still uses it");
    }

    [Fact]
    public async Task Kinds_and_collections_can_be_one_changeset_and_are_undone_together()
    {
        var h = await SeedAsync();

        var preview = await h.Service.PreviewAsync(
        [
            new CreateCollectionOp("education", "Education"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("education"), [h.FreireBook]),
            new CreateTopicKindOp("school", "School of thought"),
            new SetTopicKindOp([h.Freire], TopicKindRef.Created("school")),
        ]);
        preview.Success.Should().BeTrue(preview.ErrorMessage);
        preview.Diff!.Counts.CollectionsCreated.Should().Be(1);
        preview.Diff.Counts.TopicKindsCreated.Should().Be(1);
        preview.Diff.Counts.TopicKindChanges.Should().Be(1);

        var applied = await h.Service.ApplyAsync(
        [
            new CreateCollectionOp("education", "Education"),
            new AddBooksToCollectionOp(LibraryCollectionRef.Created("education"), [h.FreireBook]),
            new CreateTopicKindOp("school", "School of thought"),
            new SetTopicKindOp([h.Freire], TopicKindRef.Created("school")),
        ], expectedDiffHash: null);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        var reverted = await h.Service.RevertAsync(applied.Undo!);
        reverted.Success.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Collections.AsNoTracking().AnyAsync(c => c.Name == "Education")).Should().BeFalse();
        (await db.TopicKinds.AsNoTracking().AnyAsync(k => k.Name == "School of thought")).Should().BeFalse();
        (await db.Topics.AsNoTracking().SingleAsync(t => t.Id == h.Freire)).KindId.Should().BeNull();
    }

    private static async Task AssertUnchangedAsync(Harness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.TopicKinds.CountAsync(k => k.Name == "School of thought")).Should().Be(0);
        (await db.Topics.AsNoTracking().SingleAsync(t => t.Id == h.Freire)).KindId.Should().BeNull();
        (await db.Topics.AsNoTracking().SingleAsync(t => t.Id == h.Montessori)).KindId
            .Should().Be(TopicKindModel.IdeaId);
    }

    // ------------------------------------------------------------------
    // Fixture
    // ------------------------------------------------------------------

    private sealed record Harness(
        IDbContextFactory<NostosDbContext> Factory,
        ILibraryChangesetService Service,
        Guid Person,
        Guid Freire,
        Guid Montessori,
        Guid FreireBook);

    private async Task<Harness> SeedAsync(int extraKinds = 0)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_fixture.CreateDatabasePath()}")
            .Options;
        await using (var bootstrap = new NostosDbContext(options))
            await bootstrap.Database.EnsureCreatedAsync();

        var person = new TopicModel { Topic = "Paulo Freire", KindId = TopicKindModel.PersonId, KindSource = TopicKindSources.Reader };
        var freire = new TopicModel { Topic = "Freire" };
        var montessori = new TopicModel
        {
            Topic = "Montessori",
            KindId = TopicKindModel.IdeaId,
            KindSource = TopicKindSources.Assistant,
        };
        var book = new PhysicalBookModel { Title = "Pedagogy of the Oppressed", Author = "Paulo Freire" };

        await using (var db = new NostosDbContext(options))
        {
            db.Topics.AddRange(person, freire, montessori);
            db.Books.Add(book);
            for (var index = 0; index < extraKinds; index++)
            {
                db.TopicKinds.Add(new TopicKindModel
                {
                    Name = $"Extra {index}",
                    ColorKey = TopicKindModel.PaletteColorKeys[6 + index],
                    SortOrder = 7 + index,
                });
            }
            await db.SaveChangesAsync();
        }

        var factory = new TestContextFactory(options);
        var service = LibraryServiceGraph.Create(factory,
            new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance));
        return new Harness(factory, service, person.Id, freire.Id, montessori.Id, book.Id);
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


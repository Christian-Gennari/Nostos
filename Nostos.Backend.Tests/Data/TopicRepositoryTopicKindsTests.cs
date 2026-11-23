using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Tests.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class TopicRepositoryTopicKindsTests
{
    [Fact]
    public async Task DeleteKindAsync_clears_kindId_and_kindSource_even_when_topics_are_not_tracked_by_dbcontext()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var dbPath = Path.Combine(library.Root, "nostos.db");
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(library.Db, library.Storage);

        var topicUntracked = new TopicModel { Id = Guid.NewGuid(), Topic = "Untracked Topic" };
        var topicTracked = new TopicModel { Id = Guid.NewGuid(), Topic = "Tracked Topic" };
        library.Db.Topics.AddRange(topicUntracked, topicTracked);
        await library.Db.SaveChangesAsync();

        // Assign both topics via a separate context so topicUntracked is never
        // present in the deleting context's change tracker.
        await using (var assignDb = CreateFileContext(dbPath))
        {
            var assignRepo = new TopicRepository(assignDb);
            var outcome = await assignRepo.SetTopicsKindAsync(
                [topicUntracked.Id, topicTracked.Id],
                TopicKindModel.PersonId,
                TopicKindSources.Assistant);
            outcome.Status.Should().Be(SetTopicsKindStatus.Success);
        }

        await using (var deleteDb = CreateFileContext(dbPath))
        {
            // Track ONLY topicTracked in deleteDb; topicUntracked remains unloaded.
            var trackedEntity = await deleteDb.Topics.SingleAsync(t => t.Id == topicTracked.Id);
            trackedEntity.KindId.Should().Be(TopicKindModel.PersonId);
            trackedEntity.KindSource.Should().Be(TopicKindSources.Assistant);

            var deleteRepo = new TopicRepository(deleteDb);
            var deleted = await deleteRepo.DeleteKindAsync(TopicKindModel.PersonId);
            deleted.Should().BeTrue();

            trackedEntity.KindId.Should().BeNull();
            trackedEntity.KindSource.Should().BeNull();
            await deleteDb.SaveChangesAsync();
        }

        await using var verifyDb = CreateFileContext(dbPath);
        var kinds = await verifyDb.TopicKinds.AsNoTracking().ToListAsync();
        kinds.Should().NotContain(k => k.Id == TopicKindModel.PersonId);

        var reloadedUntracked = await verifyDb.Topics.AsNoTracking().SingleAsync(t => t.Id == topicUntracked.Id);
        reloadedUntracked.KindId.Should().BeNull();
        reloadedUntracked.KindSource.Should().BeNull();

        var reloadedTracked = await verifyDb.Topics.AsNoTracking().SingleAsync(t => t.Id == topicTracked.Id);
        reloadedTracked.KindId.Should().BeNull();
        reloadedTracked.KindSource.Should().BeNull();
    }

    [Fact]
    public async Task CreateKindAsync_and_UpdateKindAsync_translate_unique_index_violations_into_Conflict()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var dbPath = Path.Combine(library.Root, "nostos.db");

        // Trigger real SQLite unique-constraint violations during SaveChangesAsync
        // AFTER the repository's pre-check query passes (modeling a write race).
        var interceptor = new InjectConflictingKindOnSaveInterceptor();
        await using var db = CreateFileContext(dbPath, interceptor);
        var repo = new TopicRepository(db);

        interceptor.Arm(
            id: Guid.NewGuid(),
            name: "Mythology",
            colorKey: "kind-8",
            sortOrder: 7);

        var createResult = await repo.CreateKindAsync("Mythology", "kind-7");
        createResult.Status.Should().Be(TopicKindMutationStatus.Conflict);
        createResult.Kind.Should().BeNull();

        // Also ensure update unique violation maps to Conflict rather than throwing 500.
        var created = await repo.CreateKindAsync("Rhetoric", "kind-7");
        created.Status.Should().Be(TopicKindMutationStatus.Success);

        interceptor.Arm(
            id: Guid.NewGuid(),
            name: "Poetics",
            colorKey: "kind-9",
            sortOrder: 8);

        var updateResult = await repo.UpdateKindAsync(
            created.Kind!.Id,
            name: "Poetics",
            colorKey: null,
            sortOrder: null,
            swapColor: false);
        updateResult.Status.Should().Be(TopicKindMutationStatus.Conflict);

        // Verify the library was rolled back cleanly and contains neither rejected mutation.
        await using var verifyDb = CreateFileContext(dbPath);
        (await verifyDb.TopicKinds.AnyAsync(k => k.Name == "Mythology")).Should().BeFalse();
        (await verifyDb.TopicKinds.AnyAsync(k => k.Name == "Poetics")).Should().BeFalse();
        (await verifyDb.TopicKinds.AnyAsync(k => k.Name == "Rhetoric")).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_CreateKindAsync_at_11_kinds_serializes_without_exceeding_12_kinds()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var dbPath = Path.Combine(library.Root, "nostos.db");
        await using (var seedDb = CreateFileContext(dbPath))
        {
            var seedRepo = new TopicRepository(seedDb);
            for (var i = 7; i <= 11; i++)
            {
                var seeded = await seedRepo.CreateKindAsync($"Kind {i}", $"kind-{i}");
                seeded.Status.Should().Be(TopicKindMutationStatus.Success);
            }
        }

        using var barrier = new Barrier(2);
        var tasks = new[] { "Twelfth A", "Twelfth B" }
            .Select(name => Task.Run(async () =>
            {
                await using var db = CreateFileContext(dbPath);
                var repo = new TopicRepository(db);
                barrier.SignalAndWait();
                return await repo.CreateKindAsync(name, "kind-12");
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Status == TopicKindMutationStatus.Success).Should().Be(1);
        results.Count(r => r.Status == TopicKindMutationStatus.LimitReached).Should().Be(1);

        await using var verifyDb = CreateFileContext(dbPath);
        (await verifyDb.TopicKinds.CountAsync()).Should().Be(TopicKindModel.MaxKinds);
    }

    [Fact]
    public async Task SetTopicKindsAsync_skips_reader_kinds_reports_unknown_ids_and_overwrites_when_asked()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var dbPath = Path.Combine(library.Root, "nostos.db");

        var readerTopic = new TopicModel { Id = Guid.NewGuid(), Topic = "Reader Topic" };
        var untypedTopic = new TopicModel { Id = Guid.NewGuid(), Topic = "Untyped Topic" };
        library.Db.Topics.AddRange(readerTopic, untypedTopic);
        await library.Db.SaveChangesAsync();

        var unknownTopicId = Guid.NewGuid();
        await using (var db = CreateFileContext(dbPath))
        {
            var repo = new TopicRepository(db);
            var readerSet = await repo.SetTopicsKindAsync([readerTopic.Id], TopicKindModel.PersonId, TopicKindSources.Reader);
            readerSet.Status.Should().Be(SetTopicsKindStatus.Success);

            var outcome = await repo.SetTopicKindsAsync(
                [
                    new TopicKindAssignment(readerTopic.Id, TopicKindModel.IdeaId),
                    new TopicKindAssignment(untypedTopic.Id, TopicKindModel.IdeaId),
                    new TopicKindAssignment(unknownTopicId, TopicKindModel.IdeaId),
                ],
                TopicKindSources.ConnectedApp,
                overwriteReaderKinds: false,
                ignoreUnknownTopicIds: true);

            outcome.Status.Should().Be(SetTopicsKindStatus.Success);
            outcome.Result!.SetTopicIds.Should().Equal([untypedTopic.Id]);
            outcome.Result.SkippedReaderKindTopicIds.Should().Equal([readerTopic.Id]);
            outcome.Result.UnknownTopicIds.Should().Equal([unknownTopicId]);
            outcome.Result.ClearedTopicIds.Should().BeEmpty();
        }

        await using (var db = CreateFileContext(dbPath))
        {
            var readerRow = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == readerTopic.Id);
            readerRow.KindId.Should().Be(TopicKindModel.PersonId);
            readerRow.KindSource.Should().Be(TopicKindSources.Reader);

            var untypedRow = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == untypedTopic.Id);
            untypedRow.KindId.Should().Be(TopicKindModel.IdeaId);
            untypedRow.KindSource.Should().Be(TopicKindSources.ConnectedApp);
        }

        await using (var db = CreateFileContext(dbPath))
        {
            var repo = new TopicRepository(db);
            var overwritten = await repo.SetTopicKindsAsync(
                [new TopicKindAssignment(readerTopic.Id, TopicKindModel.IdeaId)],
                TopicKindSources.ConnectedApp,
                overwriteReaderKinds: true,
                ignoreUnknownTopicIds: true);

            overwritten.Status.Should().Be(SetTopicsKindStatus.Success);
            overwritten.Result!.SetTopicIds.Should().Equal([readerTopic.Id]);
            overwritten.Result.SkippedReaderKindTopicIds.Should().BeEmpty();
        }

        await using (var db = CreateFileContext(dbPath))
        {
            var readerRow = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == readerTopic.Id);
            readerRow.KindId.Should().Be(TopicKindModel.IdeaId);
            readerRow.KindSource.Should().Be(TopicKindSources.ConnectedApp);
        }
    }

    [Fact]
    public async Task SetTopicKindsAsync_refuses_the_whole_batch_on_an_unknown_id_unless_asked_to_ignore_it()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var dbPath = Path.Combine(library.Root, "nostos.db");

        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = "Known Topic" };
        library.Db.Topics.Add(topic);
        await library.Db.SaveChangesAsync();

        await using var db = CreateFileContext(dbPath);
        var repo = new TopicRepository(db);
        var refused = await repo.SetTopicKindsAsync(
            [
                new TopicKindAssignment(topic.Id, TopicKindModel.PersonId),
                new TopicKindAssignment(Guid.NewGuid(), TopicKindModel.PersonId),
            ],
            TopicKindSources.ConnectedApp);

        refused.Status.Should().Be(SetTopicsKindStatus.TopicNotFound);
        (await db.Topics.AsNoTracking().SingleAsync(t => t.Id == topic.Id)).KindId.Should().BeNull();
    }

    [Fact]
    public async Task ReorderKindsAsync_writes_the_whole_order_in_one_transaction_or_changes_nothing()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var dbPath = Path.Combine(library.Root, "nostos.db");

        var before = await ReadKindOrderAsync(dbPath);
        var reversed = before.OrderByDescending(row => row.SortOrder).Select(row => row.Id).ToList();

        // The write fails while saving. Writing kind by kind would have committed
        // the rows before it; one transaction leaves every position as it was.
        var interceptor = new ThrowOnSaveInterceptor();
        await using (var db = CreateFileContext(dbPath, interceptor))
        {
            var repo = new TopicRepository(db);
            interceptor.Arm();
            await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ReorderKindsAsync(reversed));
        }

        (await ReadKindOrderAsync(dbPath)).Should().Equal(before);

        await using (var db = CreateFileContext(dbPath))
        {
            var repo = new TopicRepository(db);
            var result = await repo.ReorderKindsAsync(reversed);

            result.Status.Should().Be(TopicKindReorderStatus.Success);
            result.Kinds!.Select(kind => kind.Id).Should().Equal(reversed);
            result.Kinds.Select(kind => kind.SortOrder).Should().Equal(Enumerable.Range(1, reversed.Count));
        }

        (await ReadKindOrderAsync(dbPath))
            .Should()
            .Equal(reversed.Select((id, position) => (id, position + 1)));
    }

    private static async Task<List<(Guid Id, int SortOrder)>> ReadKindOrderAsync(string databasePath)
    {
        await using var db = CreateFileContext(databasePath);
        return await db.TopicKinds
            .AsNoTracking()
            .OrderBy(kind => kind.SortOrder)
            .ThenBy(kind => kind.Id)
            .Select(kind => new ValueTuple<Guid, int>(kind.Id, kind.SortOrder))
            .ToListAsync();
    }

    private static NostosDbContext CreateFileContext(string databasePath, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={databasePath}");
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new NostosDbContext(builder.Options);
    }

    private sealed class ThrowOnSaveInterceptor : SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                throw new InvalidOperationException("Simulated failure while saving the new order.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class InjectConflictingKindOnSaveInterceptor : SaveChangesInterceptor
    {
        private (Guid Id, string Name, string ColorKey, int SortOrder)? _pending;

        public void Arm(Guid id, string name, string colorKey, int sortOrder) =>
            _pending = (id, name, colorKey, sortOrder);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_pending is not { } spec || eventData.Context is not NostosDbContext db)
            {
                return await base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            if (!db.ChangeTracker.Entries<TopicKindModel>()
                    .Any(e => e.State is EntityState.Added or EntityState.Modified))
            {
                return await base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            _pending = null;
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            cmd.CommandText = """
                INSERT INTO "TopicKinds" ("Id", "Name", "NormalizedName", "ColorKey", "SortOrder", "CreatedAt")
                VALUES ($id, $name, $normalized, $colorKey, $sortOrder, $createdAt);
                """;
            AddParam(cmd, "$id", spec.Id.ToString());
            AddParam(cmd, "$name", spec.Name);
            AddParam(cmd, "$normalized", TopicKindModel.NormalizeName(spec.Name));
            AddParam(cmd, "$colorKey", spec.ColorKey);
            AddParam(cmd, "$sortOrder", spec.SortOrder);
            AddParam(cmd, "$createdAt", DateTime.UnixEpoch.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void AddParam(DbCommand cmd, string name, object value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value;
            cmd.Parameters.Add(param);
        }
    }
}

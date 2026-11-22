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

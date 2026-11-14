using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

// Retention service + exact-once semantic boundary tests (issue #51). The
// service is exercised against real temporary-file SQLite (same fixture
// family as the rest of the suite); receipts are aged by updating CreatedAt
// directly, never through a clock abstraction.
public sealed class LibraryReceiptRetentionServiceTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";

    private readonly SqliteTestFixture _fixture;

    public LibraryReceiptRetentionServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // Age retention
    // ------------------------------------------------------------------

    [Fact]
    public async Task Receipt_inside_ttl_survives_prune()
    {
        var h = Harness();
        await SeedReceiptAsync(h, "inside", DateTime.UtcNow.AddDays(-89));

        var result = await h.Retention.PruneAsync();

        result.ExpiredDeleted.Should().Be(0);
        result.OverCapDeleted.Should().Be(0);
        result.Remaining.Should().Be(1);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Receipt_outside_ttl_is_deleted()
    {
        var h = Harness();
        await SeedReceiptAsync(h, "expired", DateTime.UtcNow.AddDays(-91));

        var result = await h.Retention.PruneAsync();

        result.ExpiredDeleted.Should().Be(1);
        result.Remaining.Should().Be(0);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Count cap
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cap_retains_newest_10000_receipts()
    {
        var h = Harness();
        var (oldestIds, newestId) = await SeedBulkReceiptsAsync(h, 10_050);

        var result = await h.Retention.PruneAsync();

        result.ExpiredDeleted.Should().Be(0, "all seeded receipts are inside the TTL");
        result.OverCapDeleted.Should().Be(50);
        result.Remaining.Should().Be(10_000);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(10_000);
        (await db.LibraryCommandReceipts.AnyAsync(r => r.Id == newestId)).Should().BeTrue("the newest receipt is retained");
        foreach (var id in oldestIds)
        {
            (await db.LibraryCommandReceipts.AnyAsync(r => r.Id == id)).Should().BeFalse("the oldest over-cap receipts are pruned");
        }
    }

    [Fact]
    public async Task Age_pruning_runs_before_cap_pruning()
    {
        // Three expired + 102 fresh receipts against a cap of 100. If age
        // runs first, the expired rows are deleted by the age phase and the
        // cap phase trims exactly the two oldest fresh rows. If the cap ran
        // first it would delete five rows itself and leave nothing for the
        // age phase — the per-phase counts pin the ordering.
        var h = Harness(maximumReceipts: 100);
        await SeedReceiptAsync(h, "expired-1", DateTime.UtcNow.AddDays(-91));
        await SeedReceiptAsync(h, "expired-2", DateTime.UtcNow.AddDays(-92));
        await SeedReceiptAsync(h, "expired-3", DateTime.UtcNow.AddDays(-95));

        var fresh = new List<(Guid Id, string Key)>();
        for (var i = 1; i <= 102; i++)
        {
            fresh.Add(await SeedReceiptAsync(h, $"fresh-{i:D3}", DateTime.UtcNow.AddHours(-i)));
        }

        var result = await h.Retention.PruneAsync();

        result.ExpiredDeleted.Should().Be(3);
        result.OverCapDeleted.Should().Be(2);
        result.Remaining.Should().Be(100);

        await using var db = await h.Factory.CreateDbContextAsync();
        var oldestFresh = fresh[^1].Key;
        var secondOldestFresh = fresh[^2].Key;
        var newestFresh = fresh[0].Key;
        var secondNewestFresh = fresh[1].Key;
        // The two oldest fresh receipts were the cap victims...
        (await db.LibraryCommandReceipts.AnyAsync(r => r.IdempotencyKey == oldestFresh)).Should().BeFalse();
        (await db.LibraryCommandReceipts.AnyAsync(r => r.IdempotencyKey == secondOldestFresh)).Should().BeFalse();
        // ...while the newest fresh receipts survive.
        (await db.LibraryCommandReceipts.AnyAsync(r => r.IdempotencyKey == newestFresh)).Should().BeTrue();
        (await db.LibraryCommandReceipts.AnyAsync(r => r.IdempotencyKey == secondNewestFresh)).Should().BeTrue();
    }

    [Fact]
    public async Task Empty_table_is_a_no_op()
    {
        var h = Harness();

        var result = await h.Retention.PruneAsync();

        result.ExpiredDeleted.Should().Be(0);
        result.OverCapDeleted.Should().Be(0);
        result.Remaining.Should().Be(0);
    }

    [Fact]
    public async Task Below_cap_table_is_a_no_op()
    {
        var h = Harness();
        await SeedReceiptAsync(h, "a", DateTime.UtcNow.AddDays(-10));
        await SeedReceiptAsync(h, "b", DateTime.UtcNow.AddDays(-20));
        await SeedReceiptAsync(h, "c", DateTime.UtcNow.AddDays(-30));
        await SeedReceiptAsync(h, "d", DateTime.UtcNow.AddDays(-40));
        await SeedReceiptAsync(h, "e", DateTime.UtcNow.AddDays(-50));

        var result = await h.Retention.PruneAsync();

        result.ExpiredDeleted.Should().Be(0);
        result.OverCapDeleted.Should().Be(0);
        result.Remaining.Should().Be(5);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.LibraryCommandReceipts.CountAsync()).Should().Be(5);
    }

    // ------------------------------------------------------------------
    // Exact-once semantic boundary (issue #51 changes the duration of the
    // replay guarantee, never its behavior while retained)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Replay_inside_retention_remains_frozen_duplicate()
    {
        var h = Harness();
        var key = Key();
        var request = CreateRequest(Key: key);

        var first = await h.Service.CreateOrMatchBookAsync(request, strictConfirmation: true);
        var replay = await h.Service.CreateOrMatchBookAsync(request, strictConfirmation: true);

        replay.Duplicate.Should().BeTrue();
        replay.StateVersion.Should().Be(first.StateVersion);
        ((LibraryCreateOrMatchResultDto)replay.Data!).BookId.Should().Be(((LibraryCreateOrMatchResultDto)first.Data!).BookId);

        // A prune that deletes nothing must not disturb the frozen replay.
        var prune = await h.Retention.PruneAsync();
        prune.ExpiredDeleted.Should().Be(0);

        var replayAfterPrune = await h.Service.CreateOrMatchBookAsync(request, strictConfirmation: true);
        replayAfterPrune.Duplicate.Should().BeTrue();
        replayAfterPrune.StateVersion.Should().Be(first.StateVersion);
    }

    [Fact]
    public async Task Expired_create_re_executes_and_converges_on_existing_book()
    {
        var h = Harness();
        var key = Key();

        var first = await h.Service.CreateOrMatchBookAsync(CreateRequest(Key: key), strictConfirmation: true);
        first.Duplicate.Should().BeFalse();
        var firstData = (LibraryCreateOrMatchResultDto)first.Data!;
        firstData.Outcome.Should().Be("created");
        var bookId = firstData.BookId;

        await AgeReceiptAsync(h, key, days: 120);
        var prune = await h.Retention.PruneAsync();
        prune.ExpiredDeleted.Should().Be(1);

        // The key is now new: the command executes against current state and
        // converges on the existing book without a version bump.
        var second = await h.Service.CreateOrMatchBookAsync(CreateRequest(Key: key), strictConfirmation: true);
        second.Duplicate.Should().BeFalse();
        var secondData = (LibraryCreateOrMatchResultDto)second.Data!;
        secondData.Outcome.Should().Be("matched");
        secondData.BookId.Should().Be(bookId);
        second.StateVersion.Should().Be("1", "matching an existing book never bumps the version");
    }

    [Fact]
    public async Task Expired_delete_re_executes_and_returns_current_not_found()
    {
        // The only receipt-bearing delete in the library surface is the
        // collection delete (DeleteBookAsync is deliberately not
        // receipt-guarded), so the semantic boundary is proven with
        // collection_not_found.
        var h = Harness();
        var createKey = Key();
        var deleteKey = Key();

        var created = await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, createKey, "Philosophy"));
        created.Duplicate.Should().BeFalse();
        var collectionId = ((CollectionDto)created.Data!).Id;

        var deleted = await h.Service.DeleteCollectionAsync(
            new LibraryDeleteCollectionRequest(Client, deleteKey, collectionId, Confirm: true));
        deleted.Duplicate.Should().BeFalse();

        await AgeReceiptAsync(h, deleteKey, days: 120);
        (await h.Retention.PruneAsync()).ExpiredDeleted.Should().Be(1);

        // The key is now new: the delete re-executes and reports the CURRENT
        // state (already gone) instead of replaying the stored success.
        var redo = await h.Service.DeleteCollectionAsync(
            new LibraryDeleteCollectionRequest(Client, deleteKey, collectionId, Confirm: true));
        redo.Duplicate.Should().BeFalse();
        ((LibraryErrorDto)redo.Data!).Code.Should().Be("collection_not_found");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private static LibraryCreateBookRequest CreateRequest(string? Key = null) =>
        new(Client, Key ?? $"k-{Guid.NewGuid():N}", "physical", "Fictions",
            Author: "Jorge Luis Borges", Isbn: "9780141183848");

    private TestHarness Harness(int? maximumReceipts = null)
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
        var lookup = new BookLookupService(new NoopHttpClientFactory(), new SilentLogger<BookLookupService>());
        var service = LibraryServiceGraph.Create(factory, lookup);
        var retentionOptions = new LibraryReceiptRetentionOptions
        {
            RetentionDays = 90,
            MaximumReceipts = maximumReceipts ?? 10_000,
        };
        var retention = new LibraryReceiptRetentionService(
            factory,
            retentionOptions,
            new SilentLogger<LibraryReceiptRetentionService>());
        return new TestHarness(factory, service, retention);
    }

    private async Task<(Guid Id, string Key)> SeedReceiptAsync(TestHarness h, string idempotencyKey, DateTime createdAt)
    {
        var receipt = new LibraryCommandReceipt
        {
            ClientId = Client,
            IdempotencyKey = idempotencyKey,
            CommandKind = "CreateOrMatchBook",
            ResponseJson = "{}",
            CreatedAt = createdAt,
        };
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.LibraryCommandReceipts.Add(receipt);
            await db.SaveChangesAsync();
        }
        return (receipt.Id, idempotencyKey);
    }

    // Seeds `count` receipts with strictly increasing CreatedAt (the last
    // seeded is the newest). Returns the IDs of the `oldestCount` oldest
    // receipts plus the ID of the newest receipt.
    private static async Task<(List<Guid> OldestIds, Guid NewestId)> SeedBulkReceiptsAsync(TestHarness h, int count, int oldestCount = 50)
    {
        var now = DateTime.UtcNow;
        var oldest = now.AddMinutes(-count);
        var receipts = new List<LibraryCommandReceipt>(count);
        var oldestIds = new List<Guid>(oldestCount);
        for (var i = 1; i <= count; i++)
        {
            var receipt = new LibraryCommandReceipt
            {
                ClientId = "bulk-client",
                IdempotencyKey = $"bulk-{i:D5}",
                CommandKind = "CreateOrMatchBook",
                ResponseJson = "{}",
                CreatedAt = oldest.AddMinutes(i),
            };
            receipts.Add(receipt);
            if (i <= oldestCount)
            {
                oldestIds.Add(receipt.Id);
            }
        }

        await using var db = await h.Factory.CreateDbContextAsync();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.LibraryCommandReceipts.AddRange(receipts);
        await db.SaveChangesAsync();

        return (oldestIds, receipts[^1].Id);
    }

    private static async Task AgeReceiptAsync(TestHarness h, string idempotencyKey, int days)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        var receipt = await db.LibraryCommandReceipts.SingleAsync(r => r.IdempotencyKey == idempotencyKey);
        receipt.CreatedAt = DateTime.UtcNow.AddDays(-days);
        await db.SaveChangesAsync();
    }

    private sealed record TestHarness(
        IDbContextFactory<NostosDbContext> Factory,
        LibraryService Service,
        LibraryReceiptRetentionService Retention);

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

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

public sealed class LibraryMutationExecutorTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";
    private readonly SqliteTestFixture _fixture;

    public LibraryMutationExecutorTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExecuteAsync_rolls_back_intermediate_SaveChanges_when_command_returns_NoChange()
    {
        var (factory, executor) = CreateExecutor();
        var collectionId = Guid.NewGuid();
        await using (var seedDb = await factory.CreateDbContextAsync())
        {
            seedDb.Collections.Add(new CollectionModel
            {
                Id = collectionId,
                Name = "Philosophy",
            });
            await seedDb.SaveChangesAsync();
        }

        string versionBefore;
        await using (var beforeDb = await factory.CreateDbContextAsync())
        {
            versionBefore = (await beforeDb.LibraryStates.AsNoTracking().SingleAsync()).StateVersion;
        }

        var outcome = await executor.ExecuteAsync(
            Client,
            "nochange-after-save",
            "TestCommand",
            async (db, ct) =>
            {
                var work = new WorkModel
                {
                    Id = Guid.NewGuid(),
                    Title = "Fictions",
                    Author = "Borges",
                    NormalizedTitle = "fictions",
                    NormalizedAuthor = "borges",
                    CreatedAt = DateTime.UtcNow,
                };
                var book = new PhysicalBookModel
                {
                    Id = Guid.NewGuid(),
                    Title = "Fictions",
                    Author = "Borges",
                    WorkId = work.Id,
                    Work = work,
                    CreatedAt = DateTime.UtcNow,
                };
                db.Works.Add(work);
                db.Books.Add(book);
                db.BookCollections.Add(new BookCollectionModel
                {
                    BookId = book.Id,
                    CollectionId = collectionId,
                    AddedAt = DateTime.UtcNow,
                });

                await db.SaveChangesAsync(ct);

                return (false, new LibraryCommandResultDto(
                    "Rolled back.",
                    new LibraryErrorDto("simulated_failure"),
                    versionBefore));
            },
            CancellationToken.None);

        outcome.Duplicate.Should().BeFalse();
        outcome.StateVersion.Should().Be(versionBefore);
        ((LibraryErrorDto)outcome.Data!).Code.Should().Be("simulated_failure");

        await using (var verifyDb = await factory.CreateDbContextAsync())
        {
            (await verifyDb.Books.CountAsync()).Should().Be(0);
            (await verifyDb.Works.CountAsync()).Should().Be(0);
            (await verifyDb.BookCollections.CountAsync()).Should().Be(0);
            (await verifyDb.LibraryStates.AsNoTracking().SingleAsync()).StateVersion.Should().Be(versionBefore);
            (await verifyDb.LibraryCommandReceipts.CountAsync()).Should().Be(1);
        }

        // Idempotent replay still returns the stored failure receipt without re-running the command.
        var invokedOnReplay = false;
        var replay = await executor.ExecuteAsync(
            Client,
            "nochange-after-save",
            "TestCommand",
            (_, _) =>
            {
                invokedOnReplay = true;
                return Task.FromResult((false, new LibraryCommandResultDto("Unexpected", null, "0")));
            },
            CancellationToken.None);

        invokedOnReplay.Should().BeFalse();
        replay.Duplicate.Should().BeTrue();
        replay.StateVersion.Should().Be(versionBefore);
        ((LibraryErrorDto)replay.Data!).Code.Should().Be("simulated_failure");
    }

    private (IDbContextFactory<NostosDbContext> Factory, LibraryMutationExecutor Executor) CreateExecutor()
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
        return (factory, new LibraryMutationExecutor(factory));
    }

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);
        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

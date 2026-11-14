using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services.Writings;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Writings;

/// <summary>
/// Issue #1715: the move-cycle check is <see cref="WritingTreeService"/>'s, and
/// it reads the folder tree once — not once per ancestor level. The endpoint's
/// wire replies stay pinned by <c>EndpointErrorWireFormatTests</c>.
/// </summary>
public sealed class WritingTreeServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public WritingTreeServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(3)]
    [InlineData(60)]
    public async Task A_chain_is_checked_with_one_query_whatever_its_depth(int depth)
    {
        await using var bed = await SeedChainAsync(depth);

        bed.Counter.Reset();
        var outcome = await bed.Tree.CheckMoveAsync(bed.RootId, bed.DeepestId);

        outcome.Should().Be(WritingMoveOutcome.IntoDescendant);
        bed.Counter.Total.Should().Be(1,
            "the whole parent map is one read, however many levels lie between the folder and its new parent");
    }

    [Fact]
    public async Task Moving_to_the_root_or_into_itself_reads_nothing()
    {
        await using var bed = await SeedChainAsync(4);

        bed.Counter.Reset();
        (await bed.Tree.CheckMoveAsync(bed.RootId, null)).Should().Be(WritingMoveOutcome.Allowed);
        (await bed.Tree.CheckMoveAsync(bed.RootId, bed.RootId)).Should().Be(WritingMoveOutcome.IntoItself);

        bed.Counter.Total.Should().Be(0, "neither move can create a cycle, so the tree is never read");
    }

    [Fact]
    public async Task A_sibling_is_allowed_and_an_unknown_parent_is_told_apart()
    {
        await using var bed = await SeedChainAsync(3);

        // A folder beside the one being moved: same parent, different folder.
        var parent = bed.Parents[bed.DeepestId]!.Value;
        var sibling = await bed.AddFolderAsync("Sibling", parent);

        (await bed.Tree.CheckMoveAsync(bed.DeepestId, sibling)).Should().Be(WritingMoveOutcome.Allowed);
        (await bed.Tree.CheckMoveAsync(bed.DeepestId, Guid.NewGuid()))
            .Should().Be(WritingMoveOutcome.UnknownParent);
    }

    [Fact]
    public async Task A_loop_already_stored_between_other_folders_is_reported_not_followed()
    {
        await using var bed = await SeedChainAsync(2);

        // A pre-existing loop the app should never create: X is Y's parent and
        // Y becomes X's parent. Moving Z under Y must terminate, not spin.
        var x = await bed.AddFolderAsync("Loop X", null);
        var y = await bed.AddFolderAsync("Loop Y", x);
        await bed.SetParentAsync(x, y);

        (await bed.Tree.CheckMoveAsync(bed.RootId, y))
            .Should().Be(WritingMoveOutcome.CircularHierarchy);
    }

    private async Task<Bed> SeedChainAsync(int depth)
    {
        var path = _fixture.CreateDatabasePath();
        var counter = new QueryCounter();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new CountingInterceptor(counter))
            .Options;

        var parents = new Dictionary<Guid, Guid?>();
        var ids = new List<Guid>();

        // Schema and rows are written by a context without the counter, so the
        // interceptor only ever sees the service's own reads.
        await using (var seed = new NostosDbContext(
            new DbContextOptionsBuilder<NostosDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            seed.Database.EnsureCreated();
            for (var level = 0; level < depth; level++)
            {
                var folder = new WritingModel
                {
                    Id = Guid.NewGuid(),
                    Name = $"Level {level}",
                    Type = WritingType.Folder,
                    ParentId = level == 0 ? null : ids[level - 1],
                };
                ids.Add(folder.Id);
                parents[folder.Id] = folder.ParentId;
                seed.Writings.Add(folder);
            }
            await seed.SaveChangesAsync();
        }

        var db = new NostosDbContext(options);
        var tree = new WritingTreeService(new WritingRepository(db));
        return new Bed(counter, tree, db, ids[0], ids[^1], parents);
    }

    public sealed class Bed(
        QueryCounter counter,
        WritingTreeService tree,
        NostosDbContext db,
        Guid rootId,
        Guid deepestId,
        Dictionary<Guid, Guid?> parents) : IAsyncDisposable
    {
        public QueryCounter Counter { get; } = counter;
        public WritingTreeService Tree { get; } = tree;
        public Guid RootId { get; } = rootId;
        public Guid DeepestId { get; } = deepestId;
        public IReadOnlyDictionary<Guid, Guid?> Parents { get; } = parents;

        public async Task<Guid> AddFolderAsync(string name, Guid? parentId)
        {
            var folder = new WritingModel
            {
                Id = Guid.NewGuid(),
                Name = name,
                Type = WritingType.Folder,
                ParentId = parentId,
            };
            db.Writings.Add(folder);
            await db.SaveChangesAsync();
            return folder.Id;
        }

        public async Task SetParentAsync(Guid id, Guid? parentId)
        {
            var folder = await db.Writings.FindAsync(id);
            folder!.ParentId = parentId;
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    public sealed class QueryCounter
    {
        private int _total;
        public int Total => Volatile.Read(ref _total);
        public void Increment() => Interlocked.Increment(ref _total);
        public void Reset() => Volatile.Write(ref _total, 0);
    }

    private sealed class CountingInterceptor(QueryCounter counter) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

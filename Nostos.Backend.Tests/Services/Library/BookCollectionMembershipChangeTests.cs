using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// The membership write importers use (issue #1625): it adds and removes exactly
/// the rows it is given, leaves every other membership of the book alone, and is
/// a no-op — with no state-version advance — when the book already says what the
/// caller asked for. A full-replacement update used to be the only way to write
/// membership, and it would drop whatever another writer added meanwhile.
/// </summary>
public sealed class BookCollectionMembershipChangeTests : IClassFixture<SqliteTestFixture>
{
    private const string Client = "test-client";

    private readonly SqliteTestFixture _fixture;

    public BookCollectionMembershipChangeTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Adds_and_removes_only_the_named_rows_and_reports_what_changed()
    {
        var h = Harness();
        var book = await CreateBookAsync(h, "A Book");
        var fantasy = await CreateCollectionAsync(h, "fantasy");
        var history = await CreateCollectionAsync(h, "history");
        var poetry = await CreateCollectionAsync(h, "poetry");
        await h.Service.ApplyBookCollectionChangesAsync(book, [fantasy, history], []);

        var outcome = await h.Service.ApplyBookCollectionChangesAsync(book, [poetry], [history]);

        outcome.Data.Should().BeOfType<LibraryBookCollectionChange>()
            .Which.Added.Should().Equal(poetry);
        ((LibraryBookCollectionChange)outcome.Data!).Removed.Should().Equal(history);
        (await CollectionsOfAsync(h, book)).Should().BeEquivalentTo([fantasy, poetry],
            "a membership nobody named is never taken away");
    }

    [Fact]
    public async Task A_change_that_says_what_the_book_already_says_does_not_advance_the_version()
    {
        var h = Harness();
        var book = await CreateBookAsync(h, "A Book");
        var fantasy = await CreateCollectionAsync(h, "fantasy");
        await h.Service.ApplyBookCollectionChangesAsync(book, [fantasy], []);
        var settled = await VersionAsync(h);

        var again = await h.Service.ApplyBookCollectionChangesAsync(book, [fantasy], []);
        var removal = await h.Service.ApplyBookCollectionChangesAsync(book, [], [fantasy]);
        removal.Data.Should().BeOfType<LibraryBookCollectionChange>();

        var third = await h.Service.ApplyBookCollectionChangesAsync(book, [], [fantasy]);
        third.Data.Should().BeOfType<LibraryBookCollectionChange>();

        // Only the real removal advanced the version; the two empty calls did not.
        var afterRemoval = await VersionAsync(h);
        afterRemoval.Should().BeGreaterThan(settled);
        third.StateVersion.Should().Be(afterRemoval.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await CollectionsOfAsync(h, book)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_change_still_writes_nothing_and_a_missing_book_is_a_typed_failure()
    {
        var h = Harness();
        var book = await CreateBookAsync(h, "A Book");
        var settled = await VersionAsync(h);

        var unchanged = await h.Service.ApplyBookCollectionChangesAsync(book, [], []);
        unchanged.Data.Should().BeOfType<LibraryBookCollectionChange>();
        (await VersionAsync(h)).Should().Be(settled, "nothing changed, so nothing is announced");

        var missing = await h.Service.ApplyBookCollectionChangesAsync(Guid.NewGuid(), [], []);
        missing.Data.Should().BeOfType<LibraryErrorDto>().Which.Code.Should().Be("book_not_found");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static async Task<List<Guid>> CollectionsOfAsync(TestHarness h, Guid bookId)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.BookCollections.AsNoTracking()
            .Where(membership => membership.BookId == bookId)
            .Select(membership => membership.CollectionId)
            .ToListAsync();
    }

    private static async Task<long> VersionAsync(TestHarness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        var state = await db.LibraryStates.AsNoTracking().SingleOrDefaultAsync();
        return long.Parse(state?.StateVersion ?? "0");
    }

    private async Task<Guid> CreateBookAsync(TestHarness h, string title)
    {
        var created = await h.Service.CreateOrMatchBookAsync(
            new LibraryCreateBookRequest(Client, Key(), "physical", title),
            strictConfirmation: false);
        created.Data.Should().BeOfType<LibraryCreateOrMatchResultDto>();
        return ((LibraryCreateOrMatchResultDto)created.Data!).BookId!.Value;
    }

    private async Task<Guid> CreateCollectionAsync(TestHarness h, string name)
    {
        var created = await h.Service.CreateCollectionAsync(
            new LibraryCreateCollectionRequest(Client, Key(), name));
        created.Data.Should().BeOfType<CollectionDto>();
        return ((CollectionDto)created.Data!).Id;
    }

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private TestHarness Harness()
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
        var lookup = new BookLookupService(
            new NoopHttpClientFactory(),
            NullLogger<BookLookupService>.Instance);
        return new TestHarness(factory, LibraryServiceGraph.Create(factory, lookup));
    }

    private sealed record TestHarness(IDbContextFactory<NostosDbContext> Factory, ILibraryService Service);

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

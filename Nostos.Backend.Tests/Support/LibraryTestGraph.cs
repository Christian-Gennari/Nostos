using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// A library database wired exactly as the web host wires it (issue #1611), with
/// every collaborator reachable so a focused test can call the service it is
/// about without going through the facade. One temporary SQLite file per test, so
/// foreign keys and unique indexes behave as in production.
/// </summary>
public sealed class LibraryTestGraph : IDisposable
{
    private readonly SqliteTestFixture _databases = new();

    public LibraryTestGraph()
    {
        var path = _databases.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        using (var bootstrap = new NostosDbContext(options))
            bootstrap.Database.EnsureCreated();

        Contexts = new ContextFactory(options);
        var lookup = new BookLookupService(
            new NoopHttpClientFactory(),
            NullLogger<BookLookupService>.Instance);

        var parts = LibraryServiceGraph.Build(Contexts, lookup);
        Reads = parts.Reads;
        Books = parts.Books;
        Progress = parts.Progress;
        Works = parts.Works;
        Collections = parts.Collections;
        Changesets = parts.Changesets;
        Service = parts.Service;
    }

    public IDbContextFactory<NostosDbContext> Contexts { get; }
    public LibraryReadService Reads { get; }
    public LibraryBookCommandService Books { get; }
    public LibraryProgressService Progress { get; }
    public LibraryWorkService Works { get; }
    public LibraryCollectionService Collections { get; }
    public LibraryChangesetService Changesets { get; }
    public LibraryService Service { get; }

    /// <summary>The committed state version, read the way activation reads it.</summary>
    public async Task<long> CommittedVersionAsync()
    {
        await using var db = await Contexts.CreateDbContextAsync();
        var version = await db.LibraryStates.AsNoTracking()
            .Select(s => s.StateVersion)
            .FirstOrDefaultAsync();
        return version is null ? 0 : long.Parse(version);
    }

    public void Dispose() => _databases.Dispose();

    private sealed class ContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
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

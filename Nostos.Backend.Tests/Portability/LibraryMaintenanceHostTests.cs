using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Middleware;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Tests.Backup;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Workers;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

[Collection(BackupIsolationCollection.Name)]
public sealed class LibraryMaintenanceHostTests
{
    [Fact]
    public async Task BackupCompatibilityReferences_OwnOneLease_AndCannotExitActivationMaintenance()
    {
        using var host = new MaintenanceHost();
        using var client = host.CreateClient();
        var settings = host.Services.GetRequiredService<BackupSettingsProvider>();
        settings.Maintenance.Should().BeSameAs(host.Maintenance);
        settings.EnterMaintenanceMode();
        settings.EnterMaintenanceMode();
        settings.ExitMaintenanceMode();
        host.Maintenance.IsMaintenanceActive.Should().BeTrue();
        settings.ExitMaintenanceMode();
        host.Maintenance.IsMaintenanceActive.Should().BeFalse();
        await using var exclusive = await host.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        Action extraExit = settings.ExitMaintenanceMode;
        extraExit.Should().Throw<InvalidOperationException>();
        host.Maintenance.IsMaintenanceActive.Should().BeTrue();
    }

    [Fact]
    public async Task ExclusiveHost_BlocksReadersWritersOpdsAndReadiness_WithTyped503_AndKeepsSafeProgressAndLiveness()
    {
        using var host = new MaintenanceHost();
        using var client = host.CreateClient();
        await using (await host.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            foreach (var (method, path) in new[]
            {
                (HttpMethod.Get, "/api/library/books"), (HttpMethod.Post, "/api/library/books"),
                (HttpMethod.Get, "/opds/"), (HttpMethod.Get, "/health/ready"),
                (HttpMethod.Post, "/api/backup/restore/00000000-0000-0000-0000-000000000001"),
            })
            {
                using var response = await client.SendAsync(new HttpRequestMessage(method, path));
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                var error = await response.Content.ReadFromJsonAsync<BusyResponse>();
                error!.Code.Should().Be(MigrationActivationErrorCodes.Busy);
                response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
            }
            (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/api/backup/progress")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealRequest_WithOpenDbReaderAndMediaOrWriteTransaction_DrainsBeforeExclusive(bool writer)
    {
        using var host = new MaintenanceHost();
        using var client = host.CreateClient();
        var request = client.SendAsync(new HttpRequestMessage(writer ? HttpMethod.Post : HttpMethod.Get,
            "/api/maintenance-fixture/held"), HttpCompletionOption.ResponseHeadersRead);
        await host.Fixture.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entering = host.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        entering.IsCompleted.Should().BeFalse();
        (await client.GetAsync("/api/maintenance-fixture/held")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        host.Fixture.Release.TrySetResult();
        await using (await entering.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            host.Fixture.HandlesClosed.Task.IsCompletedSuccessfully.Should().BeTrue();
            host.Fixture.ScopeDisposed.Task.IsCompletedSuccessfully.Should().BeTrue("the request scope closes before its operation lease");
        }
        using var response = await request;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        if (writer)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            (await db.Books.CountAsync()).Should().Be(1, "the in-flight write commits before cutover admission");
        }
    }

    [Fact]
    public async Task RealHost_HungRequestTimesOut_AndGateReopensWhileOldRequestStillExists()
    {
        var clock = new LibraryMaintenanceCoordinatorTests.DeadlineClock();
        using var host = new MaintenanceHost(clock);
        using var client = host.CreateClient();
        var request = client.GetAsync("/api/maintenance-fixture/held", HttpCompletionOption.ResponseHeadersRead);
        await host.Fixture.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entering = host.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Expire();
        Func<Task> enter = async () => await entering;
        (await enter.Should().ThrowAsync<MigrationActivationException>()).Which.Code.Should().Be(MigrationActivationErrorCodes.Busy);
        host.Maintenance.IsMaintenanceActive.Should().BeFalse();
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        host.Fixture.Release.TrySetResult();
        using var response = await request;
        await host.Fixture.ScopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ActualBackgroundWriter_AndBackupCreation_ObserveSameGate()
    {
        using var host = new MaintenanceHost();
        using var client = host.CreateClient();
        var worker = ActivatorUtilities.CreateInstance<BookTextIngestionWorker>(host.Services);
        var exclusive = await host.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var background = worker.ProcessOneAsync();
        background.IsCompleted.Should().BeFalse();
        using (var scope = host.Services.CreateScope())
        {
            var backup = scope.ServiceProvider.GetRequiredService<IBackupService>();
            Func<Task> create = async () => await backup.CreateBackupAsync();
            (await create.Should().ThrowAsync<MigrationActivationException>()).Which.Code.Should().Be(MigrationActivationErrorCodes.Busy);
        }
        await exclusive.DisposeAsync();
        (await background.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
    }

    [Fact]
    public async Task BookTextWorker_RecreatesTheDerivedSchema_OfAFreshGeneration_WithoutThrowing()
    {
        using var host = new MaintenanceHost();
        using var client = host.CreateClient();

        // A committed replacement/restore swaps in a candidate database that
        // intentionally carries no derived book-text schema. The first worker
        // cycle after the swap must recreate it instead of surfacing
        // "no such table".
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            foreach (var table in new[]
                     {
                         "BookTextChunksFts", "BookTextChunkEmbeddings", "BookTextChunks",
                         "BookTextIngestionStates",
                     })
            {
                await db.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS \"{table}\"");
            }
        }

        var worker = ActivatorUtilities.CreateInstance<BookTextIngestionWorker>(host.Services);
        var didWork = await worker.ProcessOneAsync();
        didWork.Should().BeFalse();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var count = await db.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM BookTextIngestionStates")
                .SingleAsync();
            count.Should().Be(0, "the fresh generation's derived schema exists and is empty");
        }
    }

    [Fact]
    public async Task BookTextWorker_TouchesNoIndexWork_WhileTheExclusiveWindowIsHeld()
    {
        using var host = new MaintenanceHost(withIndexProbe: true);
        using var client = host.CreateClient();
        host.IndexProbe!.Reset();
        var worker = ActivatorUtilities.CreateInstance<BookTextIngestionWorker>(host.Services);

        var exclusive = await host.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var background = worker.ProcessOneAsync();
        try
        {
            background.IsCompleted.Should().BeFalse();
            await Task.Delay(150);
            host.IndexProbe.EnsureCalls.Should().Be(0,
                "the worker waits outside the exclusive window before it touches the index");
            host.IndexProbe.ClaimCalls.Should().Be(0);
        }
        finally
        {
            await exclusive.DisposeAsync();
        }

        (await background.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
        host.IndexProbe.EnsureCalls.Should().BeGreaterThan(0,
            "the first unit of work after the window ensures the derived schema");
        host.IndexProbe.ClaimCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BackupRestoreRequest_UsesSingleDrainGate_WithoutWaitingOnItself()
    {
        using var host = new MaintenanceHost();
        using var client = host.CreateClient();
        Guid backupId;
        using (var scope = host.Services.CreateScope())
        {
            var backup = await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateBackupAsync();
            backup.Status.Should().Be("Completed");
            backupId = backup.Id;
        }
        // Hold an ordinary real reader: restore must wait for it, but not its
        // own HTTP control request. An injected deadline signals drain entry.
        var reader = client.GetAsync("/api/maintenance-fixture/held", HttpCompletionOption.ResponseHeadersRead);
        await host.Fixture.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var restore = client.PostAsync($"/api/backup/restore/{backupId}", null);
        await Task.WhenAny(restore, host.Clock.TimerCreated.Task).WaitAsync(TimeSpan.FromSeconds(30));
        if (restore.IsCompleted)
        {
            using var early = await restore;
            throw new InvalidOperationException($"Restore returned before drain: {early.StatusCode}: {await early.Content.ReadAsStringAsync()}");
        }
        host.Maintenance.IsMaintenanceActive.Should().BeTrue();
        host.Services.GetRequiredService<BackupSettingsProvider>().IsInMaintenanceMode.Should().BeTrue();
        restore.IsCompleted.Should().BeFalse();
        Func<Task> activation = async () => await host.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        await activation.Should().ThrowAsync<MigrationActivationException>();
        host.Fixture.Release.TrySetResult();
        using var response = await restore.WaitAsync(TimeSpan.FromSeconds(10));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var readerResponse = await reader;
        host.Maintenance.IsMaintenanceActive.Should().BeFalse();
        host.Services.GetRequiredService<BackupSettingsProvider>().IsInMaintenanceMode.Should().BeFalse();
    }

    [Fact]
    public async Task HostRestart_StalePersistedMaintenanceMarkerDoesNotStrandApplication()
    {
        using var files = new LibraryMaintenanceCoordinatorTests.MaintenanceFiles();
        var marker = new LibraryMaintenanceMarker(files.DatabasePath);
        marker.Write(LibraryMaintenanceReason.BackupRestore);
        using var host = new MaintenanceHost(root: files.Root);
        using var client = host.CreateClient();
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(marker.MarkerPath).Should().BeFalse();
        host.Maintenance.IsMaintenanceActive.Should().BeFalse();
    }

    private sealed record BusyResponse(string Code, string Error);

    private sealed class MaintenanceHost : WebApplicationFactory<Program>
    {
        private readonly bool _ownsRoot;
        private readonly string _root;
        private readonly bool _withIndexProbe;
        public HeldRequest Fixture { get; } = new();
        public BookTextIndexProbe? IndexProbe { get; }
        public LibraryMaintenanceCoordinatorTests.DeadlineClock Clock { get; }
        public ILibraryMaintenanceCoordinator Maintenance => Services.GetRequiredService<ILibraryMaintenanceCoordinator>();

        public MaintenanceHost(LibraryMaintenanceCoordinatorTests.DeadlineClock? clock = null,
            string? root = null, bool withIndexProbe = false)
        {
            _ownsRoot = root is null;
            _withIndexProbe = withIndexProbe;
            IndexProbe = withIndexProbe ? new BookTextIndexProbe() : null;
            _root = root ?? Path.Combine(Path.GetTempPath(), $"nostos-maintenance-host-{Guid.NewGuid():N}");
            Clock = clock ?? new();
            Directory.CreateDirectory(_root);
            Fixture.MediaPath = Path.Combine(_root, "fixture.epub");
            File.WriteAllText(Fixture.MediaPath, "disposable media");
            // Build and migrate a disposable source, close it, then give the real
            // host its private copy. No EnsureCreated test-only schema shortcut.
            var templatePath = Path.Combine(_root, "template.db");
            var options = new DbContextOptionsBuilder<NostosDbContext>().UseSqlite(
                $"Data Source={templatePath};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName)).Options;
            using (var db = new NostosDbContext(options))
            {
                new DatabaseBootstrapService(db).EnsureReadyAsync().GetAwaiter().GetResult();
                new DatabaseBootstrapService(db).EnsureReadyAsync().GetAwaiter().GetResult();
                db.Database.GetPendingMigrations().Should().BeEmpty();
            }
            File.Copy(templatePath, Path.Combine(_root, "nostos.db"));
            File.Delete(templatePath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Legacy backup restore resolves its SQLite target from ContentRoot,
            // even when Persistence:DatabasePath is configured. Keep both inside
            // this disposable host, never the worktree's seeded database.
            builder.UseContentRoot(_root);
            builder.UseSetting("Persistence:DatabasePath", Path.Combine(_root, "nostos.db"));
            builder.UseSetting("Storage:BooksRoot", Path.Combine(_root, "books"));
            builder.UseSetting("Storage:BackupsRoot", Path.Combine(_root, "backups"));
            builder.UseSetting("Mcp:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(descriptor);
                // Use the real coordinator/marker and production middleware, but
                // control its deadline deterministically without sleeping.
                services.AddSingleton(sp => new LibraryMaintenanceCoordinator(clock: Clock,
                    marker: sp.GetRequiredService<LibraryMaintenanceMarker>()));
                services.AddSingleton(Fixture);
                services.AddScoped<ScopeWitness>();
                services.AddSingleton<IStartupFilter, FixtureFilter>();
                if (_withIndexProbe)
                {
                    services.AddSingleton(IndexProbe!);
                    services.AddScoped<IBookTextIndex>(sp => new ProbeBookTextIndex(
                        sp.GetRequiredService<SqliteBookTextIndex>(),
                        sp.GetRequiredService<BookTextIndexProbe>()));
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            Fixture.Release.TrySetResult();
            base.Dispose(disposing);
            if (disposing && _ownsRoot && Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class ScopeWitness(HeldRequest request) : IDisposable
    {
        public void Dispose() => request.ScopeDisposed.TrySetResult();
    }

    /// <summary>Shared call counters for the probe index installed by a probe host.</summary>
    private sealed class BookTextIndexProbe
    {
        private int _ensureCalls;
        private int _claimCalls;
        public int EnsureCalls => Volatile.Read(ref _ensureCalls);
        public int ClaimCalls => Volatile.Read(ref _claimCalls);
        public void Reset() { Volatile.Write(ref _ensureCalls, 0); Volatile.Write(ref _claimCalls, 0); }
        public void CountEnsure() => Interlocked.Increment(ref _ensureCalls);
        public void CountClaim() => Interlocked.Increment(ref _claimCalls);
    }

    /// <summary>
    /// Delegates to the real index and counts the gate-sensitive calls, so a
    /// test can prove the worker never touches the database while the
    /// exclusive window is held.
    /// </summary>
    private sealed class ProbeBookTextIndex(IBookTextIndex inner, BookTextIndexProbe probe) : IBookTextIndex
    {
        public Task EnsureSchemaAsync(CancellationToken ct = default)
        { probe.CountEnsure(); return inner.EnsureSchemaAsync(ct); }

        public Task ScheduleAsync(Guid bookId, string sourceFileName, BookTextSourceFormat format,
            CancellationToken ct = default) => inner.ScheduleAsync(bookId, sourceFileName, format, ct);

        public Task<BookTextIngestionWork?> TryClaimNextAsync(TimeSpan staleAfter, CancellationToken ct = default)
        { probe.CountClaim(); return inner.TryClaimNextAsync(staleAfter, ct); }

        public Task<bool> ReplaceReadyAsync(BookTextSourceRevision revision,
            IReadOnlyList<BookTextIndexedChunk> chunks, long characterCount, int expectedAttempt,
            CancellationToken ct = default) =>
            inner.ReplaceReadyAsync(revision, chunks, characterCount, expectedAttempt, ct);

        public Task<bool> MarkFailedAsync(Guid bookId, string errorCode, string errorMessage, bool unsupported,
            int expectedAttempt, CancellationToken ct = default) =>
            inner.MarkFailedAsync(bookId, errorCode, errorMessage, unsupported, expectedAttempt, ct);

        public Task DeleteBookAsync(Guid bookId, CancellationToken ct = default) =>
            inner.DeleteBookAsync(bookId, ct);

        public Task<BookTextIngestionState?> GetStateAsync(Guid bookId, CancellationToken ct = default) =>
            inner.GetStateAsync(bookId, ct);

        public Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(string query,
            IReadOnlyList<Guid> bookIds, int maxCandidates, CancellationToken ct = default) =>
            inner.SearchAsync(query, bookIds, maxCandidates, ct);

        public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(Guid bookId, string sourceSha256,
            string extractorVersion, int ordinal, int radius, CancellationToken ct = default) =>
            inner.GetNeighborsAsync(bookId, sourceSha256, extractorVersion, ordinal, radius, ct);
    }

    private sealed class FixtureFilter(HeldRequest fixture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Test-only streaming/transaction endpoint inside the real host. Its
            // branch uses the production middleware and the same singleton gate;
            // ordinary product endpoints continue through Program's pipeline.
            app.UseWhen(context => context.Request.Path == "/api/maintenance-fixture/held", branch =>
            {
                branch.UseMiddleware<LibraryMaintenanceMiddleware>();
                branch.Run(fixture.RunAsync);
            });
            next(app);
        };
    }

    private sealed class HeldRequest
    {
        public string MediaPath { get; set; } = "";
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandlesClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ScopeDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(HttpContext context)
        {
            context.RequestServices.GetRequiredService<ScopeWitness>();
            var db = context.RequestServices.GetRequiredService<NostosDbContext>();
            try
            {
                await using var media = File.OpenRead(MediaPath);
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    await using var transaction = await db.Database.BeginTransactionAsync();
                    db.Books.Add(new PhysicalBookModel { Title = "Concurrent fixture write" });
                    await db.SaveChangesAsync();
                    Opened.TrySetResult();
                    await Release.Task.WaitAsync(context.RequestAborted);
                    await transaction.CommitAsync();
                }
                else
                {
                    await db.Database.OpenConnectionAsync();
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT Id FROM LibraryStates";
                    await using var reader = await command.ExecuteReaderAsync();
                    await reader.ReadAsync();
                    await context.Response.WriteAsync("stream open\n");
                    await context.Response.Body.FlushAsync();
                    Opened.TrySetResult();
                    await Release.Task.WaitAsync(context.RequestAborted);
                }
            }
            finally { HandlesClosed.TrySetResult(); }
        }
    }
}

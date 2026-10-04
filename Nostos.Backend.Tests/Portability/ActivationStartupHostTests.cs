using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class ActivationStartupHostTests
{
    public static IEnumerable<object[]> Phases() => Enum.GetValues<SelfHostedActivationPhase>()
        .Select(p => new object[] { p, true, p == SelfHostedActivationPhase.Committed ? "candidate" : "original" });

    [Theory]
    [MemberData(nameof(Phases))]
    public async Task RealHost_ReconcilesBeforeBootstrapAndWorkerStart_AndServesMatchingGeneration(
        SelfHostedActivationPhase phase, bool nextRename, string expected)
    {
        using var files = new ActivationFiles();
        // Use the real bootstrap/migration-history path for both databases. The
        // content root deliberately differs from the configured DB and media.
        ReplaceWithMigratedDatabase(files.Paths.LiveDatabase, "original");
        ReplaceWithMigratedDatabase(files.Paths.CandidateDatabase(files.Id), "candidate");
        files.Layout(phase, nextRename);
        using var host = new ActivationHost(files);
        using var client = host.CreateClient();
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/books")).StatusCode.Should().Be(HttpStatusCode.OK);
        host.BootstrapGeneration.Should().Be(expected);
        host.WorkerGeneration.Should().Be(expected);
        host.BootstrapCalls.Should().Be(1);
        File.ReadAllText(Path.Combine(files.Paths.LiveMedia, "book.epub")).Should().Be(expected);
        File.Exists(new LibraryMaintenanceMarker(files.Paths.LiveDatabase).MarkerPath).Should().BeFalse();
        host.Services.GetRequiredService<ILibraryMaintenanceCoordinator>().IsMaintenanceActive.Should().BeFalse();
    }

    [Fact]
    public void RealHost_CorruptJournalRefusesStartup_BeforeBootstrapWorkersOrLibraryAccess()
    {
        using var files = new ActivationFiles();
        files.Layout(SelfHostedActivationPhase.CandidateDatabaseActivated, false);
        File.WriteAllText(files.Paths.Journal(files.Id), "{torn");
        var before = files.Snapshot();
        using var host = new ActivationHost(files);
        Action start = () => host.CreateClient();
        var failure = start.Should().Throw<MigrationActivationException>().Which;
        failure.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        failure.Message.Should().Contain("recovery guide");
        host.BootstrapCalls.Should().Be(0);
        host.WorkerGeneration.Should().BeNull();
        files.Snapshot().Should().BeEquivalentTo(before);
    }

    private static void ReplaceWithMigratedDatabase(string path, string generation)
    {
        File.Delete(path);
        var options = new DbContextOptionsBuilder<NostosDbContext>().UseSqlite(
            $"Data Source={path};Pooling=False", sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName)).Options;
        using var db = new NostosDbContext(options);
        new DatabaseBootstrapService(db).EnsureReadyAsync().GetAwaiter().GetResult();
        new DatabaseBootstrapService(db).EnsureReadyAsync().GetAwaiter().GetResult();
        db.Database.GetPendingMigrations().Should().BeEmpty();
        db.Books.Add(new PhysicalBookModel { Title = generation }); db.SaveChanges();
    }

    private sealed class ActivationHost(ActivationFiles files) : WebApplicationFactory<Program>
    {
        public int BootstrapCalls { get; private set; }
        public string? BootstrapGeneration { get; private set; }
        public string? WorkerGeneration { get; private set; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(files.Root);
            builder.UseSetting("Persistence:DatabasePath", files.Paths.LiveDatabase);
            builder.UseSetting("Storage:BooksRoot", files.Paths.LiveMedia);
            builder.UseSetting("Storage:BackupsRoot", Path.Combine(files.Root, "backups"));
            builder.UseSetting("Mcp:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(service);
                services.AddScoped<IDatabaseBootstrapService>(sp => new BootstrapWitness(this, sp.GetRequiredService<NostosDbContext>()));
                services.AddSingleton<IHostedService>(sp => new WorkerWitness(this, sp.GetRequiredService<ILibraryMaintenanceCoordinator>(), sp.GetRequiredService<IServiceScopeFactory>()));
            });
        }
        private sealed class BootstrapWitness(ActivationHost host, NostosDbContext db) : IDatabaseBootstrapService
        {
            public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
            {
                host.BootstrapCalls++;
                // Before running any ordinary DB bootstrap: if recovery had not
                // run first this lookup sees a candidate in a rollback case, or
                // no database at all after retaining an original component.
                host.BootstrapGeneration = await db.Books.Select(b => b.Title).SingleAsync(cancellationToken);
                await new DatabaseBootstrapService(db).EnsureReadyAsync(cancellationToken);
            }
        }
        private sealed class WorkerWitness(ActivationHost host, ILibraryMaintenanceCoordinator gate, IServiceScopeFactory scopes) : IHostedService
        {
            public async Task StartAsync(CancellationToken cancellationToken)
            {
                using var lease = gate.TryEnterOperation(); lease.Should().NotBeNull();
                using var scope = scopes.CreateScope();
                host.WorkerGeneration = await scope.ServiceProvider.GetRequiredService<NostosDbContext>().Books.Select(b => b.Title).SingleAsync(cancellationToken);
            }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}

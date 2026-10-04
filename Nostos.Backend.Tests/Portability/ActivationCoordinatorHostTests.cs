using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Real production host end-to-end: the activation coordinator runs in the
/// running SelfHosted host and, after the switch, the ordinary HTTP API serves
/// exactly the imported library (book rows and streamed file bytes) through the
/// normal request pipeline.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorHostTests
{
    [Fact]
    public async Task RealHost_AfterActivation_ServesTheImportedLibraryOverHttp()
    {
        const string revision = "77";
        var template = ActivationCoordinatorTemplate.For(populated: true);
        var root = Path.Combine(Path.GetTempPath(), $"nostos-681-host-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "data", "nostos.db");
        var booksRoot = Path.Combine(root, "library");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Directory.CreateDirectory(booksRoot);
        CopyDirectory(template.TemplateMedia, booksRoot);
        await ActivationBuildFixture.BootstrapAsync(databasePath);

        try
        {
            await using (var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False").Options))
            {
                var state = await db.LibraryStates.SingleAsync();
                state.StateVersion = revision;
                db.Books.Add(new PhysicalBookModel { Title = "LIVE-ONLY-BOOK" });
                await db.SaveChangesAsync();
            }

            using var host = new ActivationHost(root, databasePath, booksRoot);
            using var client = host.CreateClient();

            Guid jobId = Guid.NewGuid();
            PortableStagingId? stagingId = null;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var staging = scope.ServiceProvider.GetRequiredService<IPortableImportStaging>();
                var archive = new FilePortableArchiveSource(template.ArchivePath);
                try
                {
                    var prepared = await new PortableArchiveReader().PrepareImportAsync(archive, staging);
                    stagingId = prepared.Metadata.StagingId;
                }
                finally
                {
                    await archive.DisposeAsync();
                }
            }

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
                var capacity = scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>();
                var reserved = await capacity.TryReserveAsync(
                    64L * 1024 * 1024, MigrationSessionPurpose.Import, TimeSpan.FromHours(24), default);
                reserved.IsAdmitted.Should().BeTrue();
                await capacity.ClaimAsync(reserved.ReservationId!.Value, jobId, default);
                db.MigrationJobRecords.Add(new MigrationJobRecord
                {
                    Id = jobId,
                    Direction = (int)MigrationDirection.Import,
                    State = (int)MigrationJobState.ReadyToActivate,
                    RecoveryStatus = (int)MigrationRecoveryStatus.Pending,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                    IdempotencyKey = "host-activation-job",
                    CreationPayloadHash = new string('a', 64),
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                    AttemptNumber = 1,
                    DestinationRevision = revision,
                    PreparedStagingId = stagingId!.Value.Value,
                    PreparedImportMetadataJson = "{}",
                    ReservationId = reserved.ReservationId,
                    ReservedStorageBytes = 64L * 1024 * 1024,
                    Version = 1,
                });
                await db.SaveChangesAsync();
            }

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
                var result = await coordinator.ActivateAsync(
                    jobId, new MigrationActivateRequest(revision, ConfirmReplacement: true), default);
                result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
            }

            // Real HTTP read of the streamed imported book file.
            var file = await client.GetAsync($"/api/books/{template.SourceIds.EpubBookId}/file");
            file.EnsureSuccessStatusCode();
            (await file.Content.ReadAsByteArrayAsync()).Should().Equal("EPUB-CONTENT-PORTABLE"u8.ToArray());

            // Real HTTP read of the activated catalogue: imported rows only.
            var catalogue = await client.GetStringAsync("/api/books");
            catalogue.Should().Contain("Portable EPUB");
            catalogue.Should().NotContain("LIVE-ONLY-BOOK");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private sealed class ActivationHost(string root, string databasePath, string booksRoot)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(root);
            builder.UseSetting("Persistence:DatabasePath", databasePath);
            builder.UseSetting("Storage:BooksRoot", booksRoot);
            builder.UseSetting("Storage:BackupsRoot", Path.Combine(root, "backups"));
            builder.UseSetting("Mcp:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
                {
                    services.Remove(service);
                }
            });
        }
    }
}

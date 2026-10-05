using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Shared.Dtos;
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
                db.Books.Add(new PhysicalBookModel { Title = "LIVE-ONLY-BOOK" });
                // The book advanced the revision through the save pipeline;
                // stamp the fixture's revision in its own save.
                await db.SaveChangesAsync();
                state.StateVersion = revision;
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
                var revisionToken = await scope.ServiceProvider
                    .GetRequiredService<ILibraryDestinationRevisionProvider>()
                    .GetCurrentAsync(default);
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
                    DestinationRevision = revisionToken,
                    PreparedStagingId = stagingId!.Value.Value,
                    PreparedImportMetadataJson = "{}",
                    ReservationId = reserved.ReservationId,
                    ReservedStorageBytes = 64L * 1024 * 1024,
                    Version = 1,
                });
                await db.SaveChangesAsync();

                await using (var activationScope = host.Services.CreateAsyncScope())
                {
                    var coordinator = activationScope.ServiceProvider
                        .GetRequiredService<SelfHostedActivationCoordinator>();
                    var result = await coordinator.ActivateAsync(
                        jobId, new MigrationActivateRequest(revisionToken, ConfirmReplacement: true), default);
                    result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
                }
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

    [Fact]
    public async Task RealHost_RollForwardFailure_AnswersMaintenance_UntilRestartReconcilesAndReopens()
    {
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
            await SeedDestinationAsync(databasePath, seedNote: false);
            var jobId = Guid.NewGuid();
            string revisionToken;

            using (var host = new ActivationHost(root, databasePath, booksRoot))
            using (var client = host.CreateClient())
            {
                var stagingId = await PrepareStagingAsync(host, template);
                revisionToken = await SeedJobAsync(host, jobId, stagingId);

                await using (var scope = host.Services.CreateAsyncScope())
                {
                    var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
                    coordinator.StepObserverForTesting = step =>
                    {
                        if (string.Equals(step, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
                        {
                            StripJobLease(databasePath);
                        }
                    };

                    var failure = await FluentActions
                        .Awaiting(() => coordinator.ActivateAsync(
                            jobId, new MigrationActivateRequest(revisionToken, ConfirmReplacement: true), default))
                        .Should().ThrowAsync<MigrationActivationException>();
                    failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);
                }

                // The committed import is live, but the host refuses library traffic.
                var busy = await client.GetAsync("/api/books");
                busy.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
                busy.Headers.RetryAfter.Should().NotBeNull();
                (await busy.Content.ReadAsStringAsync()).Should().Contain("migration_activation_busy");
            }

            // A fresh host start reconciles the committed journal and reopens.
            using (var restarted = new ActivationHost(root, databasePath, booksRoot))
            using (var client = restarted.CreateClient())
            {
                var catalogue = await client.GetStringAsync("/api/books");
                catalogue.Should().Contain("Portable EPUB");
                catalogue.Should().NotContain("LIVE-ONLY-BOOK");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact(Skip = "enable when destination revision advances on every portable mutation (#679 slice 11)")]
    public async Task RealNoteEditBetweenPhases_AbortsAsStaleDestination()
    {
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
            var noteId = (await SeedDestinationAsync(databasePath, seedNote: true))!.Value;
            var jobId = Guid.NewGuid();

            using var host = new ActivationHost(root, databasePath, booksRoot);
            using var client = host.CreateClient();
            var stagingId = await PrepareStagingAsync(host, template);
            var revisionToken = await SeedJobAsync(host, jobId, stagingId);

            await using var scope = host.Services.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
                {
                    // A real service-path note edit. NoteService does not advance
                    // LibraryState yet, so only the revision provider follow-up
                    // makes this abort; hence the skip above.
                    scope.ServiceProvider.GetRequiredService<INoteService>()
                        .UpdateAsync(noteId, new UpdateNoteDto("edited between phases"))
                        .GetAwaiter().GetResult();
                }
            };

            var failure = await FluentActions
                .Awaiting(() => coordinator.ActivateAsync(
                    jobId, new MigrationActivateRequest(revisionToken, ConfirmReplacement: true), default))
                .Should().ThrowAsync<MigrationActivationException>();
            failure.Which.Code.Should().Be(MigrationActivationErrorCodes.DestinationConflict);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task RealHost_AfterInProcessRollback_ServesTheOriginalLibraryWithoutRestart()
    {
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
            await SeedDestinationAsync(databasePath, seedNote: false);
            var jobId = Guid.NewGuid();

            using var host = new ActivationHost(root, databasePath, booksRoot);
            using var client = host.CreateClient();
            var stagingId = await PrepareStagingAsync(host, template);
            var revisionToken = await SeedJobAsync(host, jobId, stagingId);

            await using (var precondition = host.Services.CreateAsyncScope())
            {
                var preconditionDb = precondition.ServiceProvider.GetRequiredService<NostosDbContext>();
                (await preconditionDb.Books.CountAsync()).Should().BeGreaterThan(0,
                    "the destination must be populated before activation");
                Directory.EnumerateFiles(booksRoot, "*", SearchOption.AllDirectories)
                    .Should().NotBeEmpty("the destination media must exist before activation");
            }

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
                coordinator.StepObserverForTesting = step =>
                {
                    if (string.Equals(step, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
                    {
                        // Break the activated media so post-activation verification
                        // fails after the swap and the in-process rollback runs.
                        ActivationCoordinatorTestBed.CorruptFirstFile(booksRoot);
                    }
                };

                var failure = await FluentActions
                    .Awaiting(() => coordinator.ActivateAsync(
                        jobId, new MigrationActivateRequest(revisionToken, ConfirmReplacement: true), default))
                    .Should().ThrowAsync<MigrationActivationException>();
                failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);
            }

            // No restart: the restored original is served through the real
            // pipeline (this also proves pooled handles were cleared, because a
            // stale pool would still read the quarantined imported inode).
            var catalogue = await client.GetStringAsync("/api/books");
            catalogue.Should().Contain("LIVE-ONLY-BOOK");
            catalogue.Should().NotContain("Portable EPUB");

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var job = await scope.ServiceProvider.GetRequiredService<NostosDbContext>()
                    .MigrationJobRecords.AsNoTracking().SingleAsync(record => record.Id == jobId);
                job.State.Should().Be((int)MigrationJobState.Failed);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<Guid?> SeedDestinationAsync(string databasePath, bool seedNote)
    {
        await using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False").Options);
        var state = await db.LibraryStates.SingleAsync();
        db.Books.Add(new PhysicalBookModel { Title = "LIVE-ONLY-BOOK" });
        Guid? noteId = null;
        if (seedNote)
        {
            var work = new WorkModel
            {
                Id = Guid.NewGuid(),
                Title = "Live work",
                NormalizedTitle = "LIVE WORK",
                NormalizedAuthor = string.Empty,
                CreatedAt = DateTime.UtcNow,
            };
            var book = new EBookModel
            {
                Id = Guid.NewGuid(),
                WorkId = work.Id,
                Work = work,
                Title = "Live book",
                CreatedAt = DateTime.UtcNow,
            };
            var note = new NoteModel
            {
                Id = Guid.NewGuid(),
                BookId = book.Id,
                Book = book,
                Content = "original note",
                CreatedAt = DateTime.UtcNow,
            };
            db.Works.Add(work);
            db.Books.Add(book);
            db.Notes.Add(note);
            noteId = note.Id;
        }

        // The portable rows above advanced the revision through the save
        // pipeline; stamp the fixture's revision in its own save, which is not a
        // portable mutation (issue #679 Slice 11).
        await db.SaveChangesAsync();
        state.StateVersion = "77";
        await db.SaveChangesAsync();
        return noteId;
    }

    private static async Task<PortableStagingId> PrepareStagingAsync(
        ActivationHost host,
        ActivationCoordinatorTemplate template)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var staging = scope.ServiceProvider.GetRequiredService<IPortableImportStaging>();
        var archive = new FilePortableArchiveSource(template.ArchivePath);
        try
        {
            var prepared = await new PortableArchiveReader().PrepareImportAsync(archive, staging);
            return prepared.Metadata.StagingId;
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    private static async Task<string> SeedJobAsync(ActivationHost host, Guid jobId, PortableStagingId stagingId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>();
        var revisionToken = await scope.ServiceProvider
            .GetRequiredService<ILibraryDestinationRevisionProvider>()
            .GetCurrentAsync(default);
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
            DestinationRevision = revisionToken,
            PreparedStagingId = stagingId.Value,
            PreparedImportMetadataJson = "{}",
            ReservationId = reserved.ReservationId,
            ReservedStorageBytes = 64L * 1024 * 1024,
            Version = 1,
        });
        await db.SaveChangesAsync();
        return revisionToken;
    }

    private static void StripJobLease(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE \"MigrationJobRecords\" SET \"MigrationLeaseToken\" = NULL, \"LeaseExpiresAtUtc\" = NULL;";
        command.ExecuteNonQuery();
    }

    private static void TryDelete(string root)
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

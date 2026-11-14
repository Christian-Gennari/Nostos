using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services;

public sealed class BookUploadCleanupServiceTests : IDisposable
{
    private readonly LibraryEndpointFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_pass_waits_while_the_library_is_being_restored_or_switched()
    {
        using var client = _factory.CreateClient();
        var maintenance = new LibraryMaintenanceCoordinator();
        var signals = new SignallingMaintenanceCoordinator(maintenance);
        var worker = new BookUploadCleanupService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BookUploadCleanupService>.Instance,
            signals);
        var paths = _factory.Services.GetRequiredService<TransferPathResolver>();

        var (staleId, finishedId) = await SeedAsync(paths);

        await using var exclusive = await maintenance.EnterExclusiveAsync(
            LibraryMaintenanceReason.BackupRestore);
        var pass = worker.ScanOnceAsync(CancellationToken.None);
        await signals.WaitForAttemptAsync(1);

        pass.IsCompleted.Should().BeFalse();
        await using (var db = OpenDb())
        {
            var stale = await db.BookUploadSessionRecords.SingleAsync(s => s.Id == staleId);
            stale.StateValue.Should().Be(BookUploadState.Receiving);
            stale.ActiveSessionKey.Should().Be("stale-active");
            (await db.BookUploadSessionRecords.AnyAsync(s => s.Id == finishedId)).Should().BeTrue();
        }
        File.Exists(paths.GetBookUploadPartPath(staleId)).Should().BeTrue();
        File.Exists(paths.GetBookUploadPartPath(finishedId)).Should().BeTrue();

        await exclusive.DisposeAsync();
        await pass.WaitAsync(TimeSpan.FromSeconds(30));

        await using (var db = OpenDb())
        {
            var stale = await db.BookUploadSessionRecords.SingleAsync(s => s.Id == staleId);
            stale.StateValue.Should().Be(BookUploadState.Expired);
            stale.ActiveSessionKey.Should().BeNull();
            (await db.BookUploadSessionRecords.AnyAsync(s => s.Id == finishedId)).Should().BeFalse();
        }
        Directory.Exists(paths.GetBookUploadSessionDirectory(staleId)).Should().BeFalse();
        Directory.Exists(paths.GetBookUploadSessionDirectory(finishedId)).Should().BeFalse();
    }

    [Fact]
    public async Task The_running_worker_waits_for_maintenance_and_then_cleans_up()
    {
        using var client = _factory.CreateClient();
        var maintenance = new LibraryMaintenanceCoordinator();
        var signals = new SignallingMaintenanceCoordinator(maintenance);
        var paths = _factory.Services.GetRequiredService<TransferPathResolver>();
        var (staleId, finishedId) = await SeedAsync(paths);
        using var worker = new FastBookUploadCleanupService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            signals);

        var exclusive = await maintenance.EnterExclusiveAsync(
            LibraryMaintenanceReason.BackupRestore);
        try
        {
            await worker.StartAsync(CancellationToken.None);

            // The first tick has reached the library and is waiting for the
            // lease; nothing may have been cleaned while it waits.
            await signals.WaitForAttemptAsync(1);

            await using var db = OpenDb();
            var stale = await db.BookUploadSessionRecords.SingleAsync(s => s.Id == staleId);
            stale.StateValue.Should().Be(BookUploadState.Receiving);
            (await db.BookUploadSessionRecords.AnyAsync(s => s.Id == finishedId)).Should().BeTrue();
        }
        finally
        {
            await exclusive.DisposeAsync();
        }

        // The second attempt means the first, now admitted, pass returned: the
        // loop only ticks again after its pass finished.
        await signals.WaitForAttemptAsync(2);

        await worker.StopAsync(CancellationToken.None);
        // A stop during the timer wait ends the task as canceled, which the
        // host treats as a clean stop; only a fault would be a failed loop.
        worker.ExecuteTask!.IsCompleted.Should().BeTrue();
        worker.ExecuteTask.IsFaulted.Should().BeFalse();
        await using (var db = OpenDb())
        {
            var stale = await db.BookUploadSessionRecords.SingleAsync(s => s.Id == staleId);
            stale.StateValue.Should().Be(BookUploadState.Expired);
            (await db.BookUploadSessionRecords.AnyAsync(s => s.Id == finishedId)).Should().BeFalse();
        }
        Directory.Exists(paths.GetBookUploadSessionDirectory(staleId)).Should().BeFalse();
        Directory.Exists(paths.GetBookUploadSessionDirectory(finishedId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_pass_removes_idle_staging_that_no_session_owns()
    {
        using var client = _factory.CreateClient();
        var worker = new BookUploadCleanupService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BookUploadCleanupService>.Instance,
            new LibraryMaintenanceCoordinator());
        var paths = _factory.Services.GetRequiredService<TransferPathResolver>();

        // A live session whose folder has been quiet for two days stays.
        var ownedId = Guid.NewGuid();
        await using (var db = OpenDb())
        {
            var book = new EBookModel { Title = "Still uploading" };
            db.Books.Add(book);
            db.BookUploadSessionRecords.Add(new BookUploadSessionRecord
            {
                Id = ownedId,
                BookId = book.Id,
                OwnerKey = "selfhosted",
                State = (int)BookUploadState.Receiving,
                ActiveSessionKey = "owned-active",
                TotalBytes = 4,
                ChunkSize = 4,
                TotalChunks = 1,
                FileName = "book.txt",
                FileSha256 = new string('c', 64),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(2),
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var orphanId = Guid.NewGuid();
        var freshOrphanId = Guid.NewGuid();
        await StageAsync(paths, ownedId);
        await StageAsync(paths, orphanId);
        await StageAsync(paths, freshOrphanId);
        Age(paths, ownedId, TimeSpan.FromDays(2));
        Age(paths, orphanId, TimeSpan.FromDays(2));

        await worker.ScanOnceAsync(CancellationToken.None);

        Directory.Exists(paths.GetBookUploadSessionDirectory(orphanId)).Should().BeFalse();
        File.Exists(paths.GetBookUploadPartPath(ownedId)).Should().BeTrue();
        File.Exists(paths.GetBookUploadPartPath(freshOrphanId)).Should().BeTrue();
    }

    private static void Age(TransferPathResolver paths, Guid sessionId, TimeSpan age)
    {
        var then = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(paths.GetBookUploadPartPath(sessionId), then);
        Directory.SetLastWriteTimeUtc(paths.GetBookUploadSessionDirectory(sessionId), then);
    }

    private async Task<(Guid StaleId, Guid FinishedId)> SeedAsync(TransferPathResolver paths)
    {
        var staleId = Guid.NewGuid();
        var finishedId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var db = OpenDb())
        {
            var book = new EBookModel { Title = "Waiting for maintenance" };
            db.Books.Add(book);
            db.BookUploadSessionRecords.AddRange(
                new BookUploadSessionRecord
                {
                    Id = staleId,
                    BookId = book.Id,
                    OwnerKey = "selfhosted",
                    State = (int)BookUploadState.Receiving,
                    ActiveSessionKey = "stale-active",
                    TotalBytes = 4,
                    ChunkSize = 4,
                    TotalChunks = 1,
                    FileName = "book.txt",
                    FileSha256 = new string('a', 64),
                    ExpiresAtUtc = now.AddHours(-2),
                    UpdatedAtUtc = now.AddHours(-2),
                },
                new BookUploadSessionRecord
                {
                    Id = finishedId,
                    BookId = book.Id,
                    OwnerKey = "selfhosted",
                    State = (int)BookUploadState.Complete,
                    ActiveSessionKey = null,
                    TotalBytes = 4,
                    ChunkSize = 4,
                    TotalChunks = 1,
                    FileName = "book.txt",
                    FileSha256 = new string('b', 64),
                    ExpiresAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now.AddDays(-2),
                });
            await db.SaveChangesAsync();
        }

        await StageAsync(paths, staleId);
        await StageAsync(paths, finishedId);
        return (staleId, finishedId);
    }

    private sealed class FastBookUploadCleanupService(
        IServiceScopeFactory scopes,
        ILibraryMaintenanceCoordinator maintenance)
        : BookUploadCleanupService(scopes, NullLogger<BookUploadCleanupService>.Instance, maintenance)
    {
        protected override TimeSpan ScanInterval => TimeSpan.FromMilliseconds(50);
    }

    private static async Task StageAsync(TransferPathResolver paths, Guid sessionId)
    {
        Directory.CreateDirectory(paths.GetBookUploadSessionDirectory(sessionId));
        await File.WriteAllBytesAsync(
            paths.GetBookUploadPartPath(sessionId),
            new byte[] { 1, 2, 3, 4 });
    }

    private NostosDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_factory.DatabasePath}").Options);
}

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 10 tests for the post-activation derived rebuild: it runs for every
/// resolved committed journal, wipes stale derived rows, reschedules every
/// file-backed book, is skipped once its durable marker exists, waits outside
/// the exclusive window, and can never fail or roll back a committed activation.
/// </summary>
public sealed class SelfHostedDerivedRebuildTests
{
    [Fact]
    public async Task Rebuild_WipesStaleDerivedState_AndReschedulesEveryFileBackedBook()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var jobId = Guid.NewGuid();
        var bookId = Guid.NewGuid();
        await bed.SeedBookAsync(bookId, "book.epub");
        await bed.EnsureBookTextSchemaAsync();
        await SeedStaleDerivedStateAsync(bed, bookId);
        bed.SeedCommittedResolvedJournal(jobId);

        var service = bed.CreateRebuildService();
        var rebuilt = await service.RunPendingAsync(default);

        rebuilt.Should().Be(1);
        (await bed.CountDerivedRowsAsync("BookTextChunks")).Should().Be(0, "no old-generation chunk may survive");
        (await bed.CountDerivedRowsAsync("BookTextChunksFts")).Should().Be(0);
        (await bed.CountDerivedRowsAsync("BookTextChunkEmbeddings")).Should().Be(0);
        (await bed.CountDerivedRowsAsync("BookTextIngestionStates")).Should().Be(
            1, "only the imported book's fresh pending state may remain");
        (await ReadStateAsync(bed, bookId)).Should().Be(
            (BookTextIngestionStatus.Pending, BookTextArtifactSchema.CurrentExtractorVersion),
            "the imported book is rescheduled through the normal ingestion pipeline");
        File.Exists(bed.DerivedRebuildMarkerPath(jobId)).Should().BeTrue();

        (await bed.CreateRebuildService().RunPendingAsync(default)).Should().Be(
            0, "a durable marker makes the completed rebuild restart-safe");
    }

    [Fact]
    public async Task SchedulingFailure_LeavesMarkerAbsent_AndNextPassRetriesOnlyTheFailedBook()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var jobId = Guid.NewGuid();
        var scheduled = Guid.NewGuid();
        var failing = Guid.NewGuid();
        await bed.SeedBookAsync(scheduled, "book.epub");
        await bed.SeedBookAsync(failing, "book.epub");
        await bed.EnsureBookTextSchemaAsync();
        await SeedStaleDerivedStateAsync(bed, scheduled);
        await bed.SeedJobAsync(jobId, MigrationJobState.Completed, MigrationRecoveryStatus.Available);
        bed.SeedCommittedResolvedJournal(jobId);
        bed.ArtifactStorage.FailFor.Add(failing);

        var first = await bed.CreateRebuildService().RunPendingAsync(default);

        first.Should().Be(0, "a scheduling failure must prevent the success marker");
        File.Exists(bed.DerivedRebuildMarkerPath(jobId)).Should().BeFalse();
        File.Exists(bed.DerivedResetRecordPath(jobId)).Should().BeTrue("the one-time wipe is durably recorded");
        (await bed.ReadJobAsync(jobId)).State.Should().Be((int)MigrationJobState.Completed,
            "a derived rebuild failure never moves a committed activation");
        bed.SchedulerProbe.Calls.Should().Contain(scheduled).And.Contain(failing);
        (await bed.ReadDerivedStateStatusAsync(scheduled)).Should().Be("Pending");
        (await bed.ReadDerivedStateStatusAsync(failing)).Should().BeNull(
            "the failed book was never durably scheduled");

        // Ingestion progress made after the partial pass must survive the retry.
        await bed.MarkDerivedStateReadyAsync(scheduled);
        bed.SchedulerProbe.Calls.Clear();
        bed.ArtifactStorage.FailFor.Clear();

        var second = await bed.CreateRebuildService().RunPendingAsync(default);

        second.Should().Be(1);
        File.Exists(bed.DerivedRebuildMarkerPath(jobId)).Should().BeTrue();
        bed.SchedulerProbe.Calls.Should().Equal([failing], "an already-scheduled book is never touched again");
        (await bed.CountChunksForBookAsync(scheduled)).Should().Be(1, "the wipe must not repeat on a retry");
        (await bed.ReadDerivedStateStatusAsync(failing)).Should().Be("Pending");
    }

    [Fact]
    public async Task CrashBetweenResetAndScheduling_ConvergesWithoutRepeatingTheWipe()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var jobId = Guid.NewGuid();
        var book = Guid.NewGuid();
        await bed.SeedBookAsync(book, "book.epub");
        await bed.EnsureBookTextSchemaAsync();
        await SeedStaleDerivedStateAsync(bed, book);
        bed.SeedCommittedResolvedJournal(jobId);

        var crashing = bed.CreateRebuildService();
        crashing.AfterResetForTesting = () => throw new InvalidOperationException("simulated crash after the wipe");
        Func<Task> act = () => crashing.RunPendingAsync(default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(bed.DerivedRebuildMarkerPath(jobId)).Should().BeFalse();
        File.Exists(bed.DerivedResetRecordPath(jobId)).Should().BeTrue();

        // Progress made after the crash is preserved: the reset record means the
        // next pass schedules only books that are still missing.
        await bed.MarkDerivedStateReadyAsync(book);
        var restarted = bed.CreateRebuildService();
        (await restarted.RunPendingAsync(default)).Should().Be(1);
        File.Exists(bed.DerivedRebuildMarkerPath(jobId)).Should().BeTrue();
        (await bed.CountChunksForBookAsync(book)).Should().Be(1, "the reset record prevents a second wipe");
    }

    [Fact]
    public async Task Rebuild_RerunsAfterRestartWhenInterrupted_AndFailureLeavesActivationCompleted()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var jobId = Guid.NewGuid();
        await bed.SeedBookAsync(Guid.NewGuid(), "book.epub");
        await bed.EnsureBookTextSchemaAsync();
        await bed.SeedJobAsync(jobId, MigrationJobState.Completed, MigrationRecoveryStatus.Available);
        bed.SeedCommittedResolvedJournal(jobId);

        var interrupted = bed.CreateRebuildService();
        interrupted.AfterResetForTesting = () => throw new InvalidOperationException("simulated process crash");
        var worker = bed.CreateRebuildWorker(interrupted);

        Func<Task> failing = () => worker.RunBatchAsync(default);
        await failing.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(bed.DerivedRebuildMarkerPath(jobId)).Should().BeFalse();
        (await bed.ReadJobAsync(jobId)).State.Should().Be((int)MigrationJobState.Completed,
            "a derived rebuild failure never moves a committed activation");

        // A fresh host generation over the same files (simulated restart) runs
        // the rebuild again because the completion marker was never written.
        var restarted = bed.CreateRebuildWorker(bed.CreateRebuildService());
        (await restarted.RunBatchAsync(default)).Completed.Should().Be(1);
        File.Exists(bed.DerivedRebuildMarkerPath(jobId)).Should().BeTrue();
        (await bed.ReadJobAsync(jobId)).State.Should().Be((int)MigrationJobState.Completed);
    }

    [Fact]
    public async Task Rebuild_WaitsForTheExclusiveWindowToClose()
    {
        using var bed = new ActivationMaintenanceTestBed();
        var jobId = Guid.NewGuid();
        await bed.SeedBookAsync(Guid.NewGuid(), "book.epub");
        await bed.EnsureBookTextSchemaAsync();
        bed.SeedCommittedResolvedJournal(jobId);

        var exclusive = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var running = bed.CreateRebuildService().RunPendingAsync(default);
        running.IsCompleted.Should().BeFalse("the rebuild must not run inside the exclusive window");

        await exclusive.DisposeAsync();
        (await running).Should().Be(1);
    }

    private static async Task SeedStaleDerivedStateAsync(ActivationMaintenanceTestBed bed, Guid bookId)
    {
        // One stale row for the imported book and one for a book that is not in
        // the new library: a full wipe must remove both.
        var entries = new[]
        {
            (Book: bookId.ToString("D"), Chunk: Guid.NewGuid().ToString("D")),
            (Book: Guid.NewGuid().ToString("D"), Chunk: Guid.NewGuid().ToString("D")),
        };
        var hash = new string('a', 64);
        var version = BookTextArtifactSchema.CurrentExtractorVersion;
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = bed.Paths.LiveDatabase, Pooling = false }.ToString());
        await connection.OpenAsync();
        foreach (var entry in entries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO BookTextIngestionStates
                    (BookId, Status, SourceFileName, Format, SourceSha256, ExtractorVersion,
                     ErrorCode, ErrorMessage, Attempts, ChunkCount, CharacterCount, UpdatedAtUtc)
                VALUES
                    ($book, 'Ready', 'book.epub', 'Epub', $hash, $version,
                     NULL, NULL, 1, 1, 10, '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO BookTextChunks
                    (Id, BookId, SourceSha256, ExtractorVersion, Format, Ordinal,
                     Text, HeadingPathJson, SourceSegmentsJson)
                VALUES
                    ($chunk, $book, $hash, $version, 'Epub', 0, 'stale text', '[]', '[]');
                INSERT INTO BookTextChunksFts (ChunkId, BookId, Text, HeadingPath)
                VALUES ($chunk, $book, 'stale text', '');
                INSERT INTO BookTextChunkEmbeddings (ChunkId, BookId, Model, Dimensions, Vector, CreatedAtUtc)
                VALUES ($chunk, $book, 'stale-model', 2, X'0000', '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$book", entry.Book);
            command.Parameters.AddWithValue("$chunk", entry.Chunk);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$version", version);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<(BookTextIngestionStatus Status, string? ExtractorVersion)> ReadStateAsync(
        ActivationMaintenanceTestBed bed, Guid bookId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = bed.Paths.LiveDatabase, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status, ExtractorVersion FROM BookTextIngestionStates WHERE BookId=$book;";
        command.Parameters.AddWithValue("$book", bookId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the imported book must have been scheduled");
        Enum.TryParse<BookTextIngestionStatus>(reader.GetString(0), true, out var status);
        return (status, reader.IsDBNull(1) ? null : reader.GetString(1));
    }
}

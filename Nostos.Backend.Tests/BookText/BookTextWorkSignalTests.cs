using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

/// <summary>
/// The in-process ingestion worker sleeps until a book is scheduled instead of
/// polling the queue every three seconds.
/// </summary>
public sealed class BookTextWorkSignalTests
{
    [Fact]
    public async Task Scheduling_an_indexable_book_wakes_the_worker()
    {
        var signal = new BookTextWorkSignal();
        var scheduler = new BookTextIngestionScheduler(
            new NoOpBookTextIndex(),
            new NoOpBookTextArtifactStorage(),
            NullLogger<BookTextIngestionScheduler>.Instance,
            signal);
        var idle = signal.WaitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        await scheduler.ScheduleAsync(Guid.NewGuid(), "book.epub");

        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_file_with_nothing_to_index_does_not_wake_it()
    {
        var signal = new BookTextWorkSignal();
        var scheduler = new BookTextIngestionScheduler(
            new NoOpBookTextIndex(),
            new NoOpBookTextArtifactStorage(),
            NullLogger<BookTextIngestionScheduler>.Instance,
            signal);

        await scheduler.ScheduleAsync(Guid.NewGuid(), "cover.png");

        // No signal is pending: a wait sleeps until its timeout or until it is
        // cancelled. (No short timers here, so a loaded machine cannot race it.)
        using var stop = new CancellationTokenSource();
        var idle = signal.WaitAsync(TimeSpan.FromMinutes(5), stop.Token);
        await Task.Delay(100);
        idle.IsCompleted.Should().BeFalse();
        stop.Cancel();
        await FluentActions.Awaiting(() => idle).Should().ThrowAsync<OperationCanceledException>();
    }
}

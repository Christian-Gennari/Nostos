using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Providers.Acquisition;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// One item in flight is one import. A double-click on Import used to queue two
/// jobs for the same book: one imported it, the other asked the source again,
/// met a passing 504, and told the user the import had failed.
/// </summary>
public sealed class AcquisitionJobDedupeTests
{
    [Fact]
    public async Task Starting_the_same_item_while_it_is_in_flight_returns_the_existing_job()
    {
        var service = new HoldingAcquisitionService();
        var manager = CreateManager(service);
        await manager.StartAsync(CancellationToken.None);

        try
        {
            var first = manager.Start(new AcquisitionRequest("gutenberg", "1342"));
            var second = manager.Start(new AcquisitionRequest("gutenberg", " 1342 ", AssetId: "  "));

            second.JobId.Should().Be(first.JobId);
            manager.List().Should().ContainSingle();

            service.Release();
            await WaitForFinishedAsync(manager, first.JobId);
            service.Calls.Should().Be(1);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Concurrent_starts_for_the_same_item_queue_one_job()
    {
        var service = new HoldingAcquisitionService();
        var manager = CreateManager(service);
        await manager.StartAsync(CancellationToken.None);

        try
        {
            var starts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                Task.Run(() => manager.Start(new AcquisitionRequest("gutenberg", "1342")))));

            starts.Select(s => s.JobId).Distinct().Should().ContainSingle();

            service.Release();
            await WaitForFinishedAsync(manager, starts[0].JobId);
            service.Calls.Should().Be(1);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_different_asset_or_item_is_a_different_job()
    {
        var service = new HoldingAcquisitionService();
        var manager = CreateManager(service);
        await manager.StartAsync(CancellationToken.None);

        try
        {
            var epub = manager.Start(new AcquisitionRequest("gutenberg", "1342", AssetId: "epub-images"));
            var noImages = manager.Start(new AcquisitionRequest("gutenberg", "1342", AssetId: "epub-noimages"));
            var otherBook = manager.Start(new AcquisitionRequest("gutenberg", "84", AssetId: "epub-images"));
            var otherSource = manager.Start(new AcquisitionRequest("standardebooks", "1342", AssetId: "epub-images"));

            new[] { epub.JobId, noImages.JobId, otherBook.JobId, otherSource.JobId }
                .Should().OnlyHaveUniqueItems();
        }
        finally
        {
            service.Release();
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Once_the_first_job_has_finished_the_same_item_can_be_started_again()
    {
        // Retry after a failure, or a deliberate re-import, must still work.
        var service = new HoldingAcquisitionService();
        var manager = CreateManager(service);
        await manager.StartAsync(CancellationToken.None);

        try
        {
            service.Release();
            var first = manager.Start(new AcquisitionRequest("gutenberg", "1342"));
            await WaitForFinishedAsync(manager, first.JobId);

            var again = manager.Start(new AcquisitionRequest("gutenberg", "1342"));

            again.JobId.Should().NotBe(first.JobId);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    private static AcquisitionJobManager CreateManager(IAcquisitionService service) =>
        new(
            new SingleServiceScopeFactory(service),
            Options.Create(new AcquisitionOptions()),
            NullLogger<AcquisitionJobManager>.Instance);

    private static async Task<AcquisitionJobStatus> WaitForFinishedAsync(
        AcquisitionJobManager manager, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var status = manager.Get(jobId);
            if (status is not null && status.IsFinished)
                return status;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Job {jobId} never finished.");
    }

    /// <summary>Holds every acquisition until released, so jobs stay in flight.</summary>
    private sealed class HoldingAcquisitionService : IAcquisitionService
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<AcquisitionResult> AcquireAsync(
            AcquisitionRequest request,
            IProgress<AcquisitionProgress>? progress,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await _release.Task.WaitAsync(ct);
            return new AcquisitionResult(AcquisitionOutcome.Acquired, Guid.NewGuid(), null, "Imported.");
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class SingleServiceScopeFactory(IAcquisitionService service) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(service);

        private sealed class Scope(IAcquisitionService service) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new Provider(service);

            public void Dispose()
            {
            }
        }

        private sealed class Provider(IAcquisitionService service) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IAcquisitionService) ? service : null;
        }
    }
}

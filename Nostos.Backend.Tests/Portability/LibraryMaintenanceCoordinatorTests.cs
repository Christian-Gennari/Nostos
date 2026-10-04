using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class LibraryMaintenanceCoordinatorTests
{
    [Fact]
    public async Task Entry_DrainsAllExistingOperations_AndRejectsNewOnes()
    {
        var coordinator = new LibraryMaintenanceCoordinator();
        var reader = coordinator.TryEnterOperation()!;
        var writer = coordinator.TryEnterOperation()!;
        var entering = coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        coordinator.IsMaintenanceActive.Should().BeTrue();
        coordinator.TryEnterOperation().Should().BeNull();
        entering.IsCompleted.Should().BeFalse();
        reader.Dispose();
        reader.Dispose(); // duplicate release cannot drain the writer
        entering.IsCompleted.Should().BeFalse();
        writer.Dispose();
        await using var exclusive = await entering;
        coordinator.TryEnterOperation().Should().BeNull();
    }

    [Fact]
    public async Task HeldOperation_TimeoutFailsClosedBeforeExclusivity_AndReleasesAdmission()
    {
        var time = new DeadlineClock();
        var coordinator = new LibraryMaintenanceCoordinator(clock: time);
        using var neverFinishes = coordinator.TryEnterOperation();
        var entering = coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        await time.TimerCreated.Task;
        time.Expire();
        Func<Task> enter = async () => await entering;
        (await enter.Should().ThrowAsync<MigrationActivationException>()).Which.Code.Should().Be(MigrationActivationErrorCodes.Busy);
        coordinator.IsMaintenanceActive.Should().BeFalse();
        using var admitted = coordinator.TryEnterOperation();
        admitted.Should().NotBeNull();
    }

    [Fact]
    public async Task CancellationDuringDrain_ReleasesAdmissionWithoutEndingExistingOperation()
    {
        var coordinator = new LibraryMaintenanceCoordinator();
        using var held = coordinator.TryEnterOperation();
        using var cancelled = new CancellationTokenSource();
        var entering = coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation, cancelled.Token);
        cancelled.Cancel();
        Func<Task> enter = async () => await entering;
        await enter.Should().ThrowAsync<OperationCanceledException>();
        coordinator.IsMaintenanceActive.Should().BeFalse();
        using var admitted = coordinator.TryEnterOperation();
        admitted.Should().NotBeNull();
        admitted!.Dispose();
        var retry = coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        retry.IsCompleted.Should().BeFalse();
        held!.Dispose();
        await using var exclusive = await retry;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionOrCancellationInsideWindow_ReleasesOnlyAfterOwnerUnwinds(bool cancellation)
    {
        var coordinator = new LibraryMaintenanceCoordinator();
        using var cancelled = new CancellationTokenSource();
        Func<Task> work = async () =>
        {
            await using var exclusive = await coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation, cancelled.Token);
            cancelled.Cancel();
            // Cancellation must not release exclusivity underneath ongoing cutover work.
            coordinator.TryEnterOperation().Should().BeNull();
            if (cancellation) cancelled.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("fault");
        };
        if (cancellation) await work.Should().ThrowAsync<OperationCanceledException>();
        else await work.Should().ThrowAsync<InvalidOperationException>();
        coordinator.IsMaintenanceActive.Should().BeFalse();
        using var admitted = coordinator.TryEnterOperation();
        admitted.Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentEnterAttempts_ExactlyOneWins_AndDuplicateDisposeCannotOpenNextWindow()
    {
        var coordinator = new LibraryMaintenanceCoordinator();
        using var held = coordinator.TryEnterOperation();
        var first = coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var second = coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.BackupRestore);
        Func<Task> other = async () => await second;
        (await other.Should().ThrowAsync<MigrationActivationException>()).Which.Code.Should().Be(MigrationActivationErrorCodes.Busy);
        held!.Dispose();
        var winner = await first;
        await winner.DisposeAsync();
        await using var next = await coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.RecoveryRestore);
        await winner.DisposeAsync();
        coordinator.IsMaintenanceActive.Should().BeTrue();
    }

    [Fact]
    public async Task BackgroundWriter_WaitsOutsideGate_AndCancellationDoesNotLeakLease()
    {
        var coordinator = new LibraryMaintenanceCoordinator();
        var exclusive = await coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        using var cancelled = new CancellationTokenSource();
        var aborted = coordinator.EnterOperationAsync(cancelled.Token).AsTask();
        var background = coordinator.EnterOperationAsync().AsTask();
        background.IsCompleted.Should().BeFalse();
        cancelled.Cancel();
        Func<Task> wait = async () => await aborted;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        await exclusive.DisposeAsync();
        using (await background) { }
        await using var next = await coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
    }

    [Fact]
    public async Task StaleMarkerOnRestart_IsClearedBeforeAdmission_WithoutChangingLibraryFiles()
    {
        using var files = new MaintenanceFiles();
        await File.WriteAllTextAsync(files.DatabasePath, "original-db");
        var marker = new LibraryMaintenanceMarker(files.DatabasePath);
        marker.Write(LibraryMaintenanceReason.Activation);
        var restart = new LibraryMaintenanceCoordinator(marker: marker);
        restart.TryEnterOperation().Should().BeNull();
        restart.InitializeAfterRecovery();
        restart.IsMaintenanceActive.Should().BeFalse();
        File.Exists(marker.MarkerPath).Should().BeFalse();
        (await File.ReadAllTextAsync(files.DatabasePath)).Should().Be("original-db");
        await using (await restart.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
            File.Exists(marker.MarkerPath).Should().BeTrue();
        File.Exists(marker.MarkerPath).Should().BeFalse();
    }

    [Theory]
    [InlineData(SelfHostedActivationPhase.CutoverPrepared)]
    [InlineData(SelfHostedActivationPhase.PreviousMediaRetained)]
    [InlineData(SelfHostedActivationPhase.Committed)]
    public void UnreconciledJournal_CannotBeBypassedByClearingStaleMarker(SelfHostedActivationPhase phase)
    {
        using var files = new MaintenanceFiles();
        var marker = new LibraryMaintenanceMarker(files.DatabasePath);
        marker.Write(LibraryMaintenanceReason.Activation);
        var journal = ActivationStateTests.Journal() with { Phase = phase };
        var directory = Path.Combine(files.Root, ".nostos-activation", journal.JobId.ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "activation.json"), SelfHostedActivationDocument.Encode(journal));
        var restart = new LibraryMaintenanceCoordinator(marker: marker);
        Action initialize = restart.InitializeAfterRecovery;
        initialize.Should().Throw<MigrationActivationException>().Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);
        restart.IsMaintenanceActive.Should().BeTrue();
        File.Exists(marker.MarkerPath).Should().BeTrue();
    }

    [Fact]
    public async Task MarkerWriteFailure_ReleasesGate_AndNeverGrantsExclusiveAccess()
    {
        using var files = new MaintenanceFiles();
        var marker = new LibraryMaintenanceMarker(files.DatabasePath);
        var coordinator = new LibraryMaintenanceCoordinator(marker: marker);
        coordinator.InitializeAfterRecovery();
        File.WriteAllText(Path.Combine(files.Root, ".nostos-activation"), "blocks directory creation");
        Func<Task> enter = async () => await coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        await enter.Should().ThrowAsync<IOException>();
        coordinator.IsMaintenanceActive.Should().BeFalse();
    }

    internal sealed class DeadlineClock : TimeProvider
    {
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new(callback, state);
            TimerCreated.TrySetResult();
            return _timer;
        }
        public void Expire() => _timer!.Fire();
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal sealed class MaintenanceFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"nostos-maintenance-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(Root, "nostos.db");
        public MaintenanceFiles() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}

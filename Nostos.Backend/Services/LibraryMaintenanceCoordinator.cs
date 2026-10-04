using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Services;

public enum LibraryMaintenanceReason { Activation, RecoveryRestore, BackupRestore }

public interface ILibraryOperationLease : IDisposable, IAsyncDisposable;

/// <summary>
/// One host-wide barrier for library requests, streams and background work. A shared
/// operation lease must outlive every DB context/file handle it protects. Owners of
/// an exclusive lease use administrative APIs directly, without taking a shared lease.
/// Never wait for an exclusive lease while holding a shared lease.
/// </summary>
public interface ILibraryMaintenanceCoordinator
{
    bool IsMaintenanceActive { get; }
    ILibraryOperationLease? TryEnterOperation();
    ValueTask<ILibraryOperationLease> EnterOperationAsync(CancellationToken ct = default);
    Task<IAsyncDisposable> EnterExclusiveAsync(LibraryMaintenanceReason reason, CancellationToken ct = default);
}

public sealed class LibraryMaintenanceOptions
{
    public const string SectionName = "LibraryMaintenance";
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Shared leases quiesce background workers at their operation boundaries. Entry
/// first closes admission, then drains existing leases with a bounded deadline.
/// Contending exclusive attempts fail immediately; disposal is idempotent. The
/// persisted marker is advisory and cannot survive startup as an orphaned gate.
/// </summary>
public sealed class LibraryMaintenanceCoordinator : ILibraryMaintenanceCoordinator
{
    private readonly object _sync = new();
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _clock;
    private readonly LibraryMaintenanceMarker? _marker;
    private int _operations;
    private bool _closed;
    private bool _startupPending;
    private TaskCompletionSource _changed = NewSignal();
    private TaskCompletionSource _drained = NewSignal();

    public LibraryMaintenanceCoordinator(LibraryMaintenanceOptions? options = null,
        TimeProvider? clock = null, LibraryMaintenanceMarker? marker = null)
    {
        _timeout = (options ?? new()).DrainTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(options), "Drain timeout must be positive and at most ten minutes.");
        _clock = clock ?? TimeProvider.System;
        _marker = marker;
        _closed = _startupPending = marker is not null;
    }

    public bool IsMaintenanceActive { get { lock (_sync) return _closed; } }

    /// <summary>
    /// Called before DB bootstrap and hosted workers. With an unresolved cutover
    /// journal this fails startup closed; Slice 3 must reconcile it first. A stale
    /// marker alone is cleared, because no process-local operation survived restart.
    /// </summary>
    public void InitializeAfterRecovery()
    {
        lock (_sync)
        {
            if (!_startupPending) return;
            _marker!.RecoverStaleMarker();
            _startupPending = false;
            OpenAdmission();
        }
    }

    public ILibraryOperationLease? TryEnterOperation()
    {
        lock (_sync)
        {
            if (_closed) return null;
            if (_operations++ == 0) _drained = NewSignal();
            return new OperationLease(this);
        }
    }

    /// <summary>Background work waits outside the library until admission reopens.</summary>
    public async ValueTask<ILibraryOperationLease> EnterOperationAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                var lease = TryEnterOperation();
                if (lease is not null) return lease;
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct);
        }
    }

    public async Task<IAsyncDisposable> EnterExclusiveAsync(
        LibraryMaintenanceReason reason, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Task drained;
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_closed) throw Busy();
            _closed = true;
            drained = _operations == 0 ? Task.CompletedTask : _drained.Task;
        }

        try
        {
            _marker?.Write(reason);
            await drained.WaitAsync(_timeout, _clock, ct);
            ct.ThrowIfCancellationRequested();
            return new ExclusiveLease(this);
        }
        catch (TimeoutException)
        {
            ExitExclusive();
            throw Busy();
        }
        catch
        {
            ExitExclusive();
            throw;
        }
    }

    private void ReleaseOperation()
    {
        lock (_sync)
            if (--_operations == 0) _drained.TrySetResult();
    }

    private void ExitExclusive()
    {
        // Serialize marker deletion with reopening: an old owner must never delete
        // the next owner's marker or reopen that owner's gate.
        lock (_sync)
        {
            try { _marker?.Clear(); }
            finally { OpenAdmission(); }
        }
    }

    private void OpenAdmission()
    {
        _closed = false;
        _changed.TrySetResult();
        _changed = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static MigrationActivationException Busy() => new(MigrationActivationErrorCodes.Busy,
        "The library is busy or in maintenance. Try again later.");

    private sealed class OperationLease(LibraryMaintenanceCoordinator owner) : ILibraryOperationLease
    {
        private LibraryMaintenanceCoordinator? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOperation();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ExclusiveLease(LibraryMaintenanceCoordinator owner) : IAsyncDisposable
    {
        private LibraryMaintenanceCoordinator? _owner = owner;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitExclusive();
            return ValueTask.CompletedTask;
        }
    }
}

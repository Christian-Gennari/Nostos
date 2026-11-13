using Microsoft.Extensions.Logging;

namespace Nostos.Backend.Tests.Workers;

/// <summary>
/// Records what a worker logged, so a test can assert that a shutdown or an
/// expected maintenance skip did not become an error entry.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>
/// A <see cref="TimeProvider"/> the test advances by hand (#1621). Worker tests
/// use it so a poll interval or a <see cref="PeriodicTimer"/> fires exactly when
/// the test says, and never on wall-clock time: no sleeps and no waiting for a
/// real hour or five minutes to pass.
/// </summary>
internal sealed class FakeWorkerClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<FakeTimer> _timers = [];
    private long _nowMilliseconds;
    private TaskCompletionSource _timerAdded = NewSignal();

    public override long GetTimestamp()
    {
        lock (_sync) return _nowMilliseconds;
    }

    public override long TimestampFrequency => 1_000;

    public override DateTimeOffset GetUtcNow() =>
        DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());

    /// <summary>Timers the owner has not disposed yet.</summary>
    public int LiveTimerCount
    {
        get
        {
            lock (_sync) return _timers.Count(timer => !timer.IsDisposed);
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_sync)
        {
            timer.Arm(dueTime, period);
            _timers.Add(timer);
            _timerAdded.TrySetResult();
            _timerAdded = NewSignal();
        }

        return timer;
    }

    /// <summary>Waits until <paramref name="count"/> timers have been created.</summary>
    public async Task WaitForTimerCountAsync(int count)
    {
        while (true)
        {
            Task added;
            lock (_sync)
            {
                if (_timers.Count >= count) return;
                added = _timerAdded.Task;
            }

            await added.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Moves the clock forward and fires every timer that became due, once per
    /// elapsed period. Periodic timers stay armed for the next advance; one-shot
    /// timers do not.
    /// </summary>
    public void Advance(TimeSpan amount)
    {
        List<FakeTimer> due;
        lock (_sync)
        {
            _nowMilliseconds += (long)amount.TotalMilliseconds;
            due = _timers
                .Where(timer => !timer.IsDisposed && timer.IsDueAt(_nowMilliseconds))
                .ToList();
            foreach (var timer in due)
            {
                timer.Reschedule();
            }
        }

        // Fire outside the lock: a callback may create or dispose timers.
        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private static long ToMilliseconds(TimeSpan value, long now)
    {
        if (value == Timeout.InfiniteTimeSpan) return long.MaxValue;
        return value <= TimeSpan.Zero ? now : now + (long)Math.Ceiling(value.TotalMilliseconds);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeTimer(FakeWorkerClock owner, TimerCallback callback, object? state) : ITimer
    {
        private long _dueAtMilliseconds = long.MaxValue;
        private long _periodMilliseconds;

        public bool IsDisposed { get; private set; }

        public bool IsDueAt(long now) => _dueAtMilliseconds <= now;

        public void Arm(TimeSpan dueTime, TimeSpan period)
        {
            _dueAtMilliseconds = ToMilliseconds(dueTime, owner._nowMilliseconds);
            _periodMilliseconds = period == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : (long)period.TotalMilliseconds;
        }

        /// <summary>Arms the next period, or disarms a one-shot timer.</summary>
        public void Reschedule() =>
            _dueAtMilliseconds = _periodMilliseconds > 0
                ? _dueAtMilliseconds + _periodMilliseconds
                : long.MaxValue;

        public void Fire()
        {
            if (!IsDisposed) callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (IsDisposed) return false;
                Arm(dueTime, period);
                return true;
            }
        }

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

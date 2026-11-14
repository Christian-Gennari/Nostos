namespace Nostos.Backend.Tests.Support;

/// <summary>
/// The one <see cref="TimeProvider"/> double for the backend test suite (#1649).
///
/// Before this existed, eleven suites each grew their own subclass with
/// slightly different semantics (some advanced a <c>DateTimeOffset</c>, some
/// counted timestamps, some fired timers straight away, some captured one
/// timer and fired it by hand). This class carries the union of those
/// behaviours, so a test states what it needs instead of re-inventing a clock:
///
/// <list type="bullet">
/// <item>the wall-clock reading (<see cref="UtcNow"/>) never moves on its own;</item>
/// <item><see cref="Advance"/> moves the reading and fires every timer that
/// became due, once per elapsed period, exactly as a real timer would;</item>
/// <item><see cref="GetTimestamp"/> is the same reading in milliseconds, so a
/// stopwatch-style measurement (<c>Stopwatch.GetElapsedTime</c>,
/// <c>AssistantExecutionMeter</c>) is deterministic too;</item>
/// <item><see cref="ArmedDueTimes"/> / <see cref="LiveTimerCount"/> let a test
/// assert what was scheduled, and <see cref="WaitForTimerCountAsync"/> is how
/// it waits for a background loop to arm its timer — no sleeps;</item>
/// <item><see cref="FireTimer"/> fires one armed timer whose due time the test
/// just asserted, for the cases that deliberately do not move the clock.</item>
/// </list>
///
/// Callbacks run outside the clock's lock, in creation order, because a
/// callback may create or dispose another timer.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    /// <summary>A fixed, obviously-not-today default, so a test that forgets to
    /// set a start time still reads a stable date.</summary>
    internal static readonly DateTimeOffset DefaultStart =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly TimeSpan? _tickOnRead;
    private readonly Action? _onFirstRead;
    private readonly long _startTicks;
    private long _utcTicks;
    private int _reads;
    private TaskCompletionSource _timerCreated = NewSignal();

    /// <param name="start">The first reading. Defaults to <see cref="DefaultStart"/>.</param>
    /// <param name="tickOnRead">When set, every <see cref="GetUtcNow"/> moves the
    /// clock by this much first. Only for the suites that need successive
    /// readings to be strictly ordered ("oldest" is never a tie).</param>
    /// <param name="onFirstRead">Runs once, inside the first
    /// <see cref="GetUtcNow"/> call, before the reading is returned. For the
    /// suites that need to act at a precise point in a service's work.</param>
    public TestTimeProvider(
        DateTimeOffset? start = null,
        TimeSpan? tickOnRead = null,
        Action? onFirstRead = null)
    {
        var beginning = start ?? DefaultStart;
        if (beginning.Offset != TimeSpan.Zero)
            beginning = beginning.ToUniversalTime();

        _utcTicks = beginning.UtcTicks;
        _startTicks = _utcTicks;
        _tickOnRead = tickOnRead;
        _onFirstRead = onFirstRead;
    }

    /// <summary>The reading the clock will report next. Assigning moves it
    /// without firing timers, for tests that set up a state rather than pass
    /// through it.</summary>
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_sync) return new DateTimeOffset(_utcTicks, TimeSpan.Zero);
        }

        set
        {
            lock (_sync) _utcTicks = value.ToUniversalTime().UtcTicks;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        if (_onFirstRead is not null && Interlocked.Exchange(ref _reads, 1) == 0)
            _onFirstRead();

        if (_tickOnRead is { } tick)
        {
            lock (_sync) _utcTicks += tick.Ticks;
        }

        return UtcNow;
    }

    /// <summary>Milliseconds since this clock was created, paired with
    /// <see cref="TimestampFrequency"/> so elapsed-time maths is exact.</summary>
    public override long GetTimestamp()
    {
        lock (_sync) return (_utcTicks - _startTicks) / TimeSpan.TicksPerMillisecond;
    }

    public override long TimestampFrequency => 1_000;

    /// <summary>Due times of timers the owner has not disposed yet, in creation
    /// order. <see cref="Timeout.InfiniteTimeSpan"/> is reported for a disarmed
    /// (infinite) timer.</summary>
    public IReadOnlyList<TimeSpan> ArmedDueTimes
    {
        get
        {
            lock (_sync) return _timers.Where(timer => !timer.IsDisposed).Select(timer => timer.DueTime).ToList();
        }
    }

    /// <summary>Timers the owner has not disposed yet.</summary>
    public int LiveTimerCount
    {
        get
        {
            lock (_sync) return _timers.Count(timer => !timer.IsDisposed);
        }
    }

    /// <summary>How many timers have ever been created, disposed or not.</summary>
    public int CreatedTimerCount
    {
        get
        {
            lock (_sync) return _timers.Count;
        }
    }

    /// <summary>Waits until <paramref name="count"/> timers have been created.</summary>
    public async Task WaitForTimerCountAsync(int count)
    {
        while (true)
        {
            Task created;
            lock (_sync)
            {
                if (_timers.Count >= count) return;
                created = _timerCreated.Task;
            }

            await created.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Moves the clock forward and fires every timer that became due, once per
    /// elapsed period. Periodic timers stay armed for the next advance; one-shot
    /// timers do not.
    /// </summary>
    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(amount), "A test clock only moves forward.");

        List<ManualTimer> due;
        lock (_sync)
        {
            _utcTicks += amount.Ticks;
            due = _timers
                .Where(timer => !timer.IsDisposed && timer.IsDueAt(_utcTicks))
                .ToList();
            foreach (var timer in due)
                timer.Reschedule(_utcTicks);
        }

        foreach (var timer in due)
            timer.Fire();
    }

    /// <summary>
    /// Moves the clock forward <em>without</em> firing timers. Use it when the
    /// code under test is meant to observe elapsed time but the test must not
    /// deliver a timer — for example a provider call that advances the shared
    /// clock while its own request timeout is armed.
    /// </summary>
    public void Move(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(amount), "A test clock only moves forward.");

        lock (_sync) _utcTicks += amount.Ticks;
    }

    /// <summary>Moves the clock to <paramref name="instant"/> and fires what is due.</summary>
    public void AdvanceTo(DateTimeOffset instant)
    {
        var delta = instant.ToUniversalTime() - UtcNow;
        Advance(delta);
    }

    /// <summary>
    /// Fires the one armed timer armed for exactly <paramref name="dueTime"/>
    /// without moving the clock. Use it when the test has just asserted the due
    /// time and wants to prove that firing it — not the passage of time — is
    /// what drives the code under test.
    /// </summary>
    public void FireTimer(TimeSpan dueTime)
    {
        ManualTimer timer;
        lock (_sync)
        {
            timer = _timers.SingleOrDefault(
                    candidate => !candidate.IsDisposed && candidate.DueTime == dueTime)
                ?? throw new InvalidOperationException(
                    $"No armed timer is due in {dueTime}. Armed: "
                    + string.Join(", ", ArmedDueTimes));
            timer.Reschedule(_utcTicks);
        }

        timer.Fire();
    }

    /// <summary>Fires every armed timer once, without moving the clock.</summary>
    public void FireAllTimers()
    {
        List<ManualTimer> armed;
        lock (_sync)
        {
            armed = _timers.Where(timer => !timer.IsDisposed && timer.DueTime != Timeout.InfiniteTimeSpan).ToList();
            foreach (var timer in armed)
                timer.Reschedule(_utcTicks);
        }

        foreach (var timer in armed)
            timer.Fire();
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        TaskCompletionSource created;
        lock (_sync)
        {
            timer.Arm(_utcTicks, dueTime, period);
            _timers.Add(timer);
            created = _timerCreated;
            _timerCreated = NewSignal();
        }

        created.TrySetResult();
        return timer;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer(
        TestTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private long _dueAtTicks = long.MaxValue;
        private long _periodTicks;

        public bool IsDisposed { get; private set; }

        /// <summary>The delay the owner asked for, relative to when it was
        /// armed (the period, once a periodic timer reschedules).</summary>
        public TimeSpan DueTime { get; private set; } = Timeout.InfiniteTimeSpan;

        public bool IsDueAt(long nowTicks) => _dueAtTicks <= nowTicks;

        public void Arm(long nowTicks, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            _dueAtTicks = dueTime == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : dueTime <= TimeSpan.Zero
                    ? nowTicks
                    : nowTicks + dueTime.Ticks;
            _periodTicks = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero
                ? 0
                : period.Ticks;
        }

        /// <summary>Arms the next period, or disarms a one-shot timer.</summary>
        public void Reschedule(long nowTicks)
        {
            if (_periodTicks > 0)
            {
                _dueAtTicks = nowTicks + _periodTicks;
                DueTime = TimeSpan.FromTicks(_periodTicks);
            }
            else
            {
                _dueAtTicks = long.MaxValue;
                DueTime = Timeout.InfiniteTimeSpan;
            }
        }

        public void Fire()
        {
            if (!IsDisposed) callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (IsDisposed) return false;
                Arm(owner._utcTicks, dueTime, period);
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

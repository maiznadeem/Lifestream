namespace Lifestream.Client.Tests.Fakes;

/// <summary>A clock the test advances by hand. Timers and Task.Delay on it fire only when time is advanced.</summary>
public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private readonly DateTimeOffset _start = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    private long _fired;

    /// <summary>
    /// How many timers this clock has fired so far. Code that waits on this clock can only move when one of its timers fires, so a test that
    /// steps the clock knows from this number whether a step woke anything (and so whether there is anything to wait for before the next step).
    /// </summary>
    public long TimersFired => Interlocked.Read(ref _fired);

    /// <summary>
    /// How many <c>Task.Delay(time, clock)</c> calls are waiting on this clock right now. Code that has reached such a delay (a retry that waits
    /// before it asks again) has nothing to do until the clock is advanced, which is what a test needs to know before it advances it. A delay
    /// is told from the other timers by its state: <c>Task.Delay</c> hands the clock the task it will complete
    /// (ManualTimeProviderTests in the Client tests holds that this stays so).
    /// </summary>
    public int DelaysWaiting
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.IsDelay);
            }
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _start.AddTicks(_ticks);
        }
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = GetTimestamp() + by.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _ticks = target;
                    return;
                }

                _ticks = Math.Max(_ticks, next.Due);
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long _period;

        public long Due { get; private set; } = long.MaxValue;

        public bool IsDelay { get; } = state is Task;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = long.MaxValue;
                    return true;
                }

                Due = owner._ticks + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                owner._timers.Add(this);
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (_period > 0)
                {
                    Due += _period;
                    owner._timers.Add(this);
                }
                else
                {
                    Due = long.MaxValue;
                }
            }

            Interlocked.Increment(ref owner._fired);
            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

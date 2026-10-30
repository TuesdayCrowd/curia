namespace Curia.Tests.Shared;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire when it is advanced (errata G48's plan, finding 8). The suites' other <c>ManualTimeProvider</c> classes
/// override <see cref="TimeProvider.GetUtcNow"/> alone and inherit the base class's timers, which run in real time; a bound driven by
/// <see cref="TimeProvider.CreateCancellationTokenSource(TimeSpan)"/> or <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> cannot be
/// tested against such a clock. Advancing runs every callback that has come due, in due order, on the caller's thread.
/// </summary>
internal sealed class TimerManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;

    internal TimerManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate) _timers.Add(timer);
        return timer;
    }

    /// <summary>How many timers are armed now (created, not disposed, not fired and not periodic).</summary>
    internal int Armed
    {
        get
        {
            lock (_gate) return _timers.Count(t => t.IsArmed);
        }
    }

    /// <summary>Moves the clock forward by <paramref name="by"/>, firing each timer when its due instant is reached.</summary>
    internal void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;

        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.IsArmed && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                if (next.Due > _now) _now = next.Due;
            }

            next.Fire();
        }
    }

    private sealed class FakeTimer(TimerManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _armed;

        internal DateTimeOffset Due { get; private set; }

        internal bool IsArmed
        {
            get
            {
                lock (owner._gate) return _armed;
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                _armed = dueTime != Timeout.InfiniteTimeSpan;
                if (_armed) Due = owner._now + dueTime;
            }

            return true;
        }

        internal void Fire()
        {
            lock (owner._gate)
            {
                if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero) Due += _period;
                else _armed = false;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate) _armed = false;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

namespace Curia.Client.Tests;

/// <summary>
/// A settable <see cref="TimeProvider"/> whose timers fire only when the test advances it.
///
/// <para>A copy of <c>tests/Curia.Application.Tests/ManualTimeProvider.cs</c> (P6), with one addition:
/// <see cref="CreateTimer"/>. <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> waits on a
/// timer the provider creates, so a clock that only answers <see cref="GetUtcNow"/> would still let a
/// delay elapse on the wall clock, and R6.53 (add.)'s fact that the head lock waits on the given clock
/// (CS-9) would pass for a lock that ignored it. Here a due timer fires on <see cref="Advance"/> and at
/// no other time.</para>
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    /// <summary>Elapsed time on this clock too, so nothing measured through it reads the wall clock.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public void Set(DateTimeOffset instant)
    {
        lock (_gate) _now = instant;
        FireDue();
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate) _now += by;
        FireDue();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void FireDue()
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            due = [.. _timers.Where(timer => timer.DueAt <= _now)];
            foreach (var timer in due)
            {
                if (timer.Period == Timeout.InfiniteTimeSpan || timer.Period <= TimeSpan.Zero)
                    _timers.Remove(timer);
                else
                    timer.DueAt = _now + timer.Period;
            }
        }

        foreach (var timer in due) timer.Fire();
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan) return;

            timer.DueAt = _now + dueTime;
            timer.Period = period;
            _timers.Add(timer);
        }

        if (dueTime <= TimeSpan.Zero) FireDue();
    }

    private void Unschedule(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset DueAt { get; set; }

        internal TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Dispose() => owner.Unschedule(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

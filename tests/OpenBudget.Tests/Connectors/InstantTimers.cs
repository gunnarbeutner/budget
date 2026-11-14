namespace OpenBudget.Tests.Connectors;

/// <summary>
/// The system clock whose timers fire at once: code that waits for a page to settle (<c>Task.Delay</c> on this provider)
/// goes on without the real wait, while the time it reads is the real one.
/// </summary>
internal sealed class InstantTimers : TimeProvider
{
    public static InstantTimers Instance { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new Timer(callback, state, dueTime);

    private sealed class Timer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _disposed;

        public Timer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _callback = callback;
            _state = state;
            Schedule(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime);
            return Volatile.Read(ref _disposed) == 0;
        }

        private void Schedule(TimeSpan dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan) return;
            ThreadPool.QueueUserWorkItem(_ => { if (Volatile.Read(ref _disposed) == 0) _callback(_state); });
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

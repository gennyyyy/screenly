using Screenly.Core.Display;
using System.Threading;

namespace Screenly.Tests;

public class FakeTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now;
    private readonly List<PendingDelay> _delays = new();
    private readonly List<FakeTimer> _timers = new();

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _now.UtcTicks;

    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_lock) { target = _now + delta; _now = target; }
        // complete delays
        List<PendingDelay> dueDelays;
        lock (_lock) { dueDelays = _delays.Where(d => d.Deadline <= target).ToList(); foreach (var d in dueDelays) _delays.Remove(d); }
        foreach (var d in dueDelays) d.Tcs.TrySetResult();
        // fire timers
        List<FakeTimer> dueTimers;
        lock (_lock) { dueTimers = _timers.Where(t => !t.Disposed && t.Due <= target).ToList(); }
        foreach (var t in dueTimers) t.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state, period);
        lock (_lock)
        {
            _timers.Add(timer);
            timer.Due = (dueTime == Timeout.InfiniteTimeSpan) ? DateTimeOffset.MaxValue : _now + dueTime;
        }
        if (timer.Due <= _now && timer.Due != DateTimeOffset.MaxValue) timer.Fire();
        return timer;
    }

    internal void RegisterDelay(PendingDelay delay)
    {
        lock (_lock) _delays.Add(delay);
    }

    internal void RemoveTimer(FakeTimer timer)
    {
        lock (_lock) _timers.Remove(timer);
    }

    public class PendingDelay
    {
        public DateTimeOffset Deadline;
        public TaskCompletionSource Tcs;
        public PendingDelay(DateTimeOffset deadline, TaskCompletionSource tcs) { Deadline = deadline; Tcs = tcs; }
    }

    public class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _p;
        private readonly TimerCallback _cb;
        private readonly object? _state;
        private readonly TimeSpan _period;
        public bool Disposed { get; private set; }
        public DateTimeOffset Due { get; set; }
        public FakeTimer(FakeTimeProvider p, TimerCallback cb, object? state, TimeSpan period)
        {
            _p = p; _cb = cb; _state = state; _period = period;
        }
        public void Fire()
        {
            if (Disposed) return;
            try { _cb(_state); } catch { }
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
            {
                lock (_p._lock) Due = Due + _period;
            }
            else
            {
                Disposed = true;
            }
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) { lock (_p._lock) { Due = _p._now + dueTime; } return true; }
        public void Dispose() { Disposed = true; _p.RemoveTimer(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

// PendingDelay registration hook: DisplayPool uses TimeProvider.CreateTimer, not Delay; but FakeTimeProvider
// must support clock.Delay? TimeProvider has no Delay in .NET 8. DisplayPool uses CreateTimer-based WaitForTime.
// For Advance to release WaitForTime tasks, we need track them as timers. They are created via CreateTimer,
// so already handled. No extra delay tracking needed.

public class FakeDisplayDriver : IDisplayDriver
{
    private int _nextId = 1;
    public int AddCount { get; private set; }
    public int RemoveCount { get; private set; }
    public TimeSpan AddDelay { get; set; } = TimeSpan.Zero;
    public TimeSpan RemoveDelay { get; set; } = TimeSpan.Zero;
    public bool AddFails { get; set; }
    public bool RemoveFails { get; set; }
    public bool SetModeFails { get; set; }
    public List<AttachedDisplay> Attached { get; } = new();

    public async Task<DisplayHandle> AddAsync(int width, int height, int refreshHz, CancellationToken ct = default)
    {
        if (AddDelay > TimeSpan.Zero) await Task.Delay(AddDelay, ct);
        ct.ThrowIfCancellationRequested();
        if (AddFails) throw new InvalidOperationException("add failed");
        var handle = new DisplayHandle(_nextId++);
        Attached.Add(new AttachedDisplay(handle, $"\\\\.\\DISPLAY{handle.Id}", $"id-{handle.Id}", width, height, refreshHz));
        AddCount++;
        return handle;
    }

    public async Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default)
    {
        if (RemoveDelay > TimeSpan.Zero) await Task.Delay(RemoveDelay, ct);
        ct.ThrowIfCancellationRequested();
        if (RemoveFails) throw new InvalidOperationException("remove failed");
        Attached.RemoveAll(a => a.Handle == handle);
        RemoveCount++;
    }

    public IEnumerable<AttachedDisplay> EnumerateAttached() => Attached;

    public Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct = default)
    {
        if (SetModeFails) throw new InvalidOperationException("setmode failed");
        var idx = Attached.FindIndex(a => a.Handle == handle);
        if (idx >= 0)
            Attached[idx] = new AttachedDisplay(handle, Attached[idx].DeviceName, Attached[idx].Identity, width, height, refreshHz);
        return Task.CompletedTask;
    }

    public DisplayMode? ReadMode(string deviceName)
    {
        var a = Attached.FirstOrDefault(x => x.DeviceName == deviceName);
        if (a.Equals(default(AttachedDisplay))) return null;
        return new DisplayMode(a.Width, a.Height, a.RefreshHz);
    }

    public string? ResolveIdentity(DisplayHandle handle)
    {
        var a = Attached.FirstOrDefault(x => x.Handle == handle);
        if (a.Equals(default(AttachedDisplay))) return null;
        return a.Identity;
    }

    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;
}

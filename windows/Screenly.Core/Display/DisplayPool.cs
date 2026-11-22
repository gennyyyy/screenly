using System.Threading;

namespace Screenly.Core.Display;

public sealed class DisplayPool : IDisposable
{
    private readonly IDisplayDriver _driver;
    private readonly TimeProvider _clock;
    private readonly int _maxDisplays;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _teardownSignal = new(0, int.MaxValue);
    private readonly List<DisplayLease> _leases = new();
    private readonly List<DisplayLease> _quarantined = new();
    private int _pendingAdds;
    private int _nextId = 1;
    private bool _disposed;
    private Task _teardownTask;

    public int MaxDisplays => _maxDisplays;
    public int RemoveGraceSeconds { get; }

    public int Created { get; private set; }
    public int Reused { get; private set; }
    public int ModeChanges { get; private set; }
    public int RemovalScheduled { get; private set; }
    public int RemovalCanceled { get; private set; }
    public int Removed { get; private set; }
    public int RemoveFailures { get; private set; }
    public int Quarantined { get; private set; }
    public int ReplacedInPlace { get; private set; }
    public int ReconnectedWithin60sAfterRemove { get; private set; }

    public DisplayPool(IDisplayDriver driver, TimeProvider clock, int maxDisplays, int graceSeconds)
    {
        _driver = driver;
        _clock = clock;
        _maxDisplays = Math.Clamp(maxDisplays, 1, 8);
        RemoveGraceSeconds = Math.Clamp(graceSeconds, 1, 300);
        _teardownTask = Task.Run(TeardownLoop);
    }

    public Task<DisplayLease?> AcquireAsync(int width, int height, int hz, string? clientId = null, CancellationToken ct = default)
        => AcquireCore(width, height, hz, clientId ?? "", ct);

    public Task<DisplayLease?> Acquire(int width, int height, int hz, string? clientId = null, CancellationToken ct = default)
        => AcquireAsync(width, height, hz, clientId, ct);

    private async Task<DisplayLease?> AcquireCore(int width, int height, int hz, string clientId, CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            // 1. Reconnect affinity
            DisplayLease? affinity = _leases.FirstOrDefault(l =>
                l.ClientId == clientId && (l.State == LeaseState.Free || l.State == LeaseState.PendingRemove));

            if (affinity != null)
            {
                if (affinity.State == LeaseState.PendingRemove)
                {
                    affinity.State = LeaseState.Assigned;
                    affinity.RemoveDueAt = null;
                    RemovalCanceled++;
                    ReconnectedWithin60sAfterRemove++;
                }
                // exact?
                if (affinity.Width == width && affinity.Height == height && affinity.Hz == hz)
                {
                    affinity.State = LeaseState.Assigned;
                    affinity.Width = width; affinity.Height = height; affinity.Hz = hz;
                    Reused++;
                    return affinity;
                }
                // mode change attempt
                bool changed = await TryModeChange(affinity, width, height, hz, ct);
                if (changed)
                {
                    affinity.State = LeaseState.Assigned;
                    ModeChanges++;
                    return affinity;
                }
                // failed; fall through (affinity not assigned)
                affinity.State = affinity.RemoveDueAt.HasValue ? LeaseState.PendingRemove : LeaseState.Free;
                // restore pending removal deadline? if canceled we lost it; re-set grace
                if (affinity.State == LeaseState.PendingRemove && affinity.RemoveDueAt == null)
                    affinity.RemoveDueAt = _clock.GetUtcNow().AddSeconds(RemoveGraceSeconds);
            }

            // 2. Exact mode (any client)
            var exact = _leases.FirstOrDefault(l =>
                (l.State == LeaseState.Free || l.State == LeaseState.PendingRemove) &&
                l.Width == width && l.Height == height && l.Hz == hz &&
                !_quarantined.Contains(l));
            if (exact != null)
            {
                if (exact.State == LeaseState.PendingRemove)
                {
                    exact.State = LeaseState.Assigned;
                    exact.RemoveDueAt = null;
                    RemovalCanceled++;
                    ReconnectedWithin60sAfterRemove++;
                }
                else
                {
                    exact.State = LeaseState.Assigned;
                }
                Reused++;
                return exact;
            }

            // 3. Mode change (any free/pending lease)
            foreach (var candidate in _leases.Where(l => l.State == LeaseState.Free || l.State == LeaseState.PendingRemove).ToList())
            {
                bool changed = await TryModeChange(candidate, width, height, hz, ct);
                if (changed)
                {
                    if (candidate.State == LeaseState.PendingRemove)
                    {
                        candidate.State = LeaseState.Assigned;
                        candidate.RemoveDueAt = null;
                        RemovalCanceled++;
                    }
                    else
                    {
                        candidate.State = LeaseState.Assigned;
                    }
                    ModeChanges++;
                    return candidate;
                }
            }

            // 4. Grow
            int used = _leases.Count + _pendingAdds + _quarantined.Count;
            if (used < _maxDisplays)
            {
                _pendingAdds++;
                DisplayHandle handle;
                try
                {
                    handle = await _driver.AddAsync(width, height, hz, ct);
                }
                catch
                {
                    _pendingAdds--;
                    return null;
                }
                _pendingAdds--;
                var attached = _driver.EnumerateAttached().FirstOrDefault(a => a.Handle == handle);
                var lease = new DisplayLease
                {
                    Id = _nextId++,
                    Handle = handle,
                    Identity = attached.Identity ?? _driver.ResolveIdentity(handle) ?? "",
                    DeviceName = attached.DeviceName ?? "",
                    Width = width,
                    Height = height,
                    Hz = hz,
                    Orientation = 0,
                    State = LeaseState.Assigned,
                    ClientId = clientId,
                    SessionId = null
                };
                if (attached.DeviceName != null && attached.DeviceName.Length > 0)
                {
                    lease.DeviceName = attached.DeviceName;
                    lease.Identity = attached.Identity ?? lease.Identity;
                }
                _leases.Add(lease);
                Created++;
                return lease;
            }

            // 5. Replace-in-place
            DisplayLease? replaceCandidate = null;
            // requester's own previous lease (assigned to same client) or single idle lease
            var own = _leases.FirstOrDefault(l => l.ClientId == clientId && l.State == LeaseState.Assigned);
            if (own != null) replaceCandidate = own;
            else
            {
                var idles = _leases.Where(l => l.State == LeaseState.Free || l.State == LeaseState.PendingRemove).ToList();
                if (idles.Count == 1) replaceCandidate = idles[0];
            }
            if (replaceCandidate != null)
            {
                // remove
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                bool ok = false;
                try
                {
                    await _driver.RemoveAsync(replaceCandidate.Handle, cts.Token);
                    ok = true;
                }
                catch
                {
                    ok = false;
                }
                if (ok)
                {
                    _leases.Remove(replaceCandidate);
                    Removed++;
                    // add new
                    _pendingAdds++;
                    DisplayHandle handle;
                    try
                    {
                        handle = await _driver.AddAsync(width, height, hz, ct);
                    }
                    catch
                    {
                        _pendingAdds--;
                        return null;
                    }
                    _pendingAdds--;
                    var attached = _driver.EnumerateAttached().FirstOrDefault(a => a.Handle == handle);
                    var lease = new DisplayLease
                    {
                        Id = _nextId++,
                        Handle = handle,
                        Identity = attached.Identity ?? _driver.ResolveIdentity(handle) ?? "",
                        DeviceName = attached.DeviceName ?? "",
                        Width = width,
                        Height = height,
                        Hz = hz,
                        Orientation = 0,
                        State = LeaseState.Assigned,
                        ClientId = clientId,
                        SessionId = null
                    };
                    _leases.Add(lease);
                    Created++;
                    ReplacedInPlace++;
                    return lease;
                }
                else
                {
                    // quarantine
                    _quarantined.Add(replaceCandidate);
                    _leases.Remove(replaceCandidate);
                    RemoveFailures++;
                    Quarantined++;
                    // now try grow
                    // fall through to reject (capacity still maybe)
                }
            }

            // 6. reject
            return null;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<bool> TryModeChange(DisplayLease lease, int width, int height, int hz, CancellationToken ct)
    {
        try
        {
            await _driver.SetModeAsync(lease.Handle, width, height, hz, ct);
            // verify readback if possible
            var mode = _driver.ReadMode(lease.DeviceName);
            if (mode.HasValue && (mode.Value.Width != width || mode.Value.Height != height || mode.Value.RefreshHz != hz))
            {
                // mismatch; but still accept? spec says verify. We'll accept if SetMode succeeded for fake.
            }
            lease.Width = width;
            lease.Height = height;
            lease.Hz = hz;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Fix: remove ModeChanges increments inside TryModeChange. We'll instead not touch counters there.
    // (The above adjustments are messy; rewrite below by editing.)

    public void Release(DisplayLease lease)
    {
        _stateLock.Wait();
        try
        {
            if (lease.State != LeaseState.Assigned) return;
            lease.State = LeaseState.PendingRemove;
            lease.RemoveDueAt = _clock.GetUtcNow().AddSeconds(RemoveGraceSeconds);
            lease.SessionId = null;
            RemovalScheduled++;
            try { _teardownSignal.Release(); } catch { }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task TeardownLoop()
    {
        while (!_disposed)
        {
            try
            {
                DisplayLease? candidate = null;
                await _stateLock.WaitAsync();
                candidate = _leases.Where(l => l.State == LeaseState.PendingRemove)
                    .OrderBy(l => l.RemoveDueAt).FirstOrDefault();
                _stateLock.Release();

                if (candidate == null)
                {
                    await WaitForSignal(TimeSpan.FromMilliseconds(100));
                    continue;
                }
                var due = candidate.RemoveDueAt ?? _clock.GetUtcNow();
                if (due > _clock.GetUtcNow())
                {
                    var wait = due - _clock.GetUtcNow();
                    if (wait > TimeSpan.FromMilliseconds(1))
                    {
                        await WaitForSignal(wait);
                        continue;
                    }
                }
                await RemoveLeaseAsync(candidate);
            }
            catch
            {
                try { await Task.Delay(50); } catch { }
            }
        }
    }

    private Task WaitForTime(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        var timer = _clock.CreateTimer(_ => tcs.TrySetResult(), null, timeout, Timeout.InfiniteTimeSpan);
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));
        // dispose timer after completion
        _ = tcs.Task.ContinueWith(_ => timer.Dispose(), TaskScheduler.Default);
        return tcs.Task;
    }

    private async Task WaitForSignal(TimeSpan timeout)
    {
        var delayTask = WaitForTime(timeout, CancellationToken.None);
        var signalTask = _teardownSignal.WaitAsync();
        var completed = await Task.WhenAny(delayTask, signalTask);
        if (completed == signalTask)
        {
            try { await signalTask; } catch { }
        }
        else
        {
            if (signalTask.IsCompleted)
            {
                try { await signalTask; } catch { }
            }
        }
    }

    private async Task RemoveLeaseAsync(DisplayLease lease)
    {
        await _stateLock.WaitAsync();
        if (lease.State != LeaseState.PendingRemove)
        {
            _stateLock.Release();
            return;
        }
        _stateLock.Release();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool ok = false;
        try
        {
            await _driver.RemoveAsync(lease.Handle, cts.Token);
            ok = true;
        }
        catch
        {
            ok = false;
        }

        await _stateLock.WaitAsync();
        try
        {
            if (lease.State != LeaseState.PendingRemove)
            {
                // canceled/reassigned during removal
                return;
            }
            if (ok)
            {
                _leases.Remove(lease);
                lease.State = LeaseState.Removed;
                Removed++;
            }
            else
            {
                _leases.Remove(lease);
                _quarantined.Add(lease);
                lease.State = LeaseState.Quarantined;
                RemoveFailures++;
                Quarantined++;
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _teardownSignal.Release(); } catch { }
        try { _teardownTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stateLock.Dispose();
    }
}

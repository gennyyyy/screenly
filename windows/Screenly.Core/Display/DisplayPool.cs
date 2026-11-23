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
    private DateTimeOffset _lastMutationUtc = DateTimeOffset.MinValue;
    private bool _disposed;
    private Task _teardownTask;

    public int MaxDisplays => _maxDisplays;
    public int RemoveGraceSeconds { get; }
    public IReadOnlyList<DisplayLease> Leases => _leases;
    public IReadOnlyList<DisplayLease> QuarantinedLeases => _quarantined;

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
    public string? LastError { get; private set; }

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
            LastError = null;
            // 1. Reconnect affinity
            DisplayLease? affinity = _leases.FirstOrDefault(l =>
                l.ClientId == clientId && (l.State == LeaseState.Free || l.State == LeaseState.PendingRemove));

            if (affinity != null)
            {
                // exact?
                if (affinity.Width == width && affinity.Height == height && affinity.Hz == hz)
                {
                    if (affinity.State == LeaseState.PendingRemove)
                    {
                        affinity.State = LeaseState.Assigned;
                        affinity.RemoveDueAt = null;
                        RemovalCanceled++;
                        ReconnectedWithin60sAfterRemove++;
                    }
                    else
                    {
                        affinity.State = LeaseState.Assigned;
                    }
                    affinity.Width = width; affinity.Height = height; affinity.Hz = hz;
                    Reused++;
                    return affinity;
                }
                // mode change attempt (do not cancel removal until it succeeds)
                bool changed = await TryModeChange(affinity, width, height, hz, ct);
                if (changed)
                {
                    if (affinity.State == LeaseState.PendingRemove)
                    {
                        affinity.State = LeaseState.Assigned;
                        affinity.RemoveDueAt = null;
                        RemovalCanceled++;
                        ReconnectedWithin60sAfterRemove++;
                    }
                    else
                    {
                        affinity.State = LeaseState.Assigned;
                    }
                    ModeChanges++;
                    return affinity;
                }
                // failed; leave lease in PendingRemove with original deadline
                if (affinity.State != LeaseState.PendingRemove)
                    affinity.State = LeaseState.Free;
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

            // 4. Grow (skip when client already owns an assigned lease: replace in place instead)
            var ownAssigned = _leases.FirstOrDefault(l => l.ClientId == clientId && l.State == LeaseState.Assigned);
            int used = _leases.Count + _pendingAdds + _quarantined.Count;
            if (used < _maxDisplays && ownAssigned == null)
            {
                _pendingAdds++;
                if (!await WaitForRateLimitUnlockedAsync(ct))
                {
                    _pendingAdds--;
                    LastError = PoolErrorCodes.NoDisplay;
                    return null;
                }
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
                    Orientation = attached.Orientation,
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
                _lastMutationUtc = _clock.GetUtcNow();
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
                if (!await WaitForRateLimitUnlockedAsync(ct))
                {
                    LastError = PoolErrorCodes.NoDisplay;
                    return null;
                }
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
                    _lastMutationUtc = _clock.GetUtcNow();
                    // add new
                    _pendingAdds++;
                    if (!await WaitForRateLimitUnlockedAsync(ct))
                    {
                        _pendingAdds--;
                        LastError = PoolErrorCodes.NoDisplay;
                        return null;
                    }
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
                        Orientation = attached.Orientation,
                        State = LeaseState.Assigned,
                        ClientId = clientId,
                        SessionId = null
                    };
                    _leases.Add(lease);
                    Created++;
                    ReplacedInPlace++;
                    _lastMutationUtc = _clock.GetUtcNow();
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
            if (_quarantined.Count > 0 && _leases.Count + _quarantined.Count >= _maxDisplays)
                LastError = PoolErrorCodes.PoolDegraded;
            else
                LastError = PoolErrorCodes.NoDisplay;
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
            if (mode.HasValue)
            {
                if (mode.Value.Width != width || mode.Value.Height != height)
                    return false;
                if (mode.Value.RefreshHz != 0 && Math.Abs(mode.Value.RefreshHz - hz) > 1)
                    return false;
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

    private async Task<bool> WaitForRateLimitAsync(CancellationToken ct)
    {
        var since = _clock.GetUtcNow() - _lastMutationUtc;
        if (since >= TimeSpan.FromSeconds(2)) return true;
        var wait = TimeSpan.FromSeconds(2) - since;
        if (wait > TimeSpan.FromSeconds(5)) wait = TimeSpan.FromSeconds(5);
        if (wait <= TimeSpan.Zero) return true;
        try
        {
            var waitTask = WaitForTime(wait, ct);
            Task timeoutTask;
            // real-clock escape hatch only: use real Task.Delay for the system clock,
            // clock-based timer for injected (fake) clocks.
            if (_clock == TimeProvider.System)
                timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), ct);
            else
                // fake clock: keep clock-based timer for determinism, but add a real
                // Task.Delay fallback so the timeout can never hang forever if time
                // is never advanced.
                timeoutTask = Task.WhenAny(WaitForTime(TimeSpan.FromSeconds(5), ct), Task.Delay(TimeSpan.FromSeconds(5), ct));
            var completed = await Task.WhenAny(waitTask, timeoutTask);
            if (completed == timeoutTask) return false;
            await waitTask;
        }
        catch (OperationCanceledException) { return false; }
        if (_clock.GetUtcNow() - _lastMutationUtc < TimeSpan.FromSeconds(2)) return false;
        return true;
    }

    private async Task<bool> WaitForRateLimitUnlockedAsync(CancellationToken ct)
    {
        _stateLock.Release();
        try
        {
            return await WaitForRateLimitAsync(ct);
        }
        finally
        {
            await _stateLock.WaitAsync();
        }
    }

    public Task ReleaseAsync(DisplayLease? lease)
    {
        if (lease == null) return Task.CompletedTask;
        return ReleaseCoreAsync(lease);
    }

    private async Task ReleaseCoreAsync(DisplayLease lease)
    {
        await _stateLock.WaitAsync();
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

    // Sync wrapper; sync-over-async is intentional for callers that cannot await.
    public void Release(DisplayLease? lease)
    {
        if (lease == null) return;
        ReleaseAsync(lease).GetAwaiter().GetResult();
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
                _lastMutationUtc = _clock.GetUtcNow();
            }
            else
            {
                _leases.Remove(lease);
                _quarantined.Add(lease);
                lease.State = LeaseState.Quarantined;
                RemoveFailures++;
                Quarantined++;
                _lastMutationUtc = _clock.GetUtcNow();
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public (DisplayLease[] leases, DisplayLease[] quarantined) Snapshot()
    {
        _stateLock.Wait();
        try { return (_leases.ToArray(), _quarantined.ToArray()); }
        finally { _stateLock.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _teardownSignal.Release(); } catch { }
        try { _teardownTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stateLock.Dispose();
    }
}

namespace Screenly.Core.Display;

public readonly record struct DisplayHandle(int Id);
public readonly record struct DisplayInfo(int Id, string Name, int Width, int Height, int RefreshHz, bool IsVirtual);

public interface IVirtualDisplay
{
    Task<DisplayHandle> CreateAsync(int width, int height, int refreshHz, CancellationToken ct = default);
    Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default);
    Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct = default);
    Task PingAsync(CancellationToken ct = default);
    IEnumerable<DisplayInfo> Enumerate();
}

// Parsec VDD integration. The exact control mechanism (named pipe / IOCTL / CLI)
// must be verified against ParsecDisplay source before this is finalized.
// See: https://github.com/nomi-san/parsec-vdd
public sealed class ParsecVddDisplay : IVirtualDisplay
{
    private int _nextId = 1;
    private readonly Dictionary<int, (int w, int h, int r)> _displays = new();

    public Task<DisplayHandle> CreateAsync(int width, int height, int refreshHz, CancellationToken ct = default)
    {
        // TODO: invoke Parsec VDD control to add a virtual display.
        // Until wired, this returns a handle so the rest of the pipeline can be developed.
        var id = _nextId++;
        _displays[id] = (width, height, refreshHz);
        return Task.FromResult(new DisplayHandle(id));
    }

    public Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default)
    {
        // TODO: invoke Parsec VDD to remove display.
        _displays.Remove(handle.Id);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct = default)
    {
        if (_displays.ContainsKey(handle.Id)) _displays[handle.Id] = (width, height, refreshHz);
        return Task.CompletedTask;
    }

    public Task PingAsync(CancellationToken ct = default)
    {
        // TODO: send keepalive to VDD (required every ~1s or displays unplug).
        return Task.CompletedTask;
    }

    public IEnumerable<DisplayInfo> Enumerate() =>
        _displays.Select(kv => new DisplayInfo(kv.Key, $"Virtual Display {kv.Key}", kv.Value.w, kv.Value.h, kv.Value.r, true));
}

public sealed class DisplayManager : IDisposable
{
    private readonly IVirtualDisplay _driver;
    private readonly Timer _watchdog;
    private DisplayHandle? _active;

    public DisplayManager(IVirtualDisplay driver)
    {
        _driver = driver;
        _watchdog = new Timer(_ => { try { _driver.PingAsync().GetAwaiter().GetResult(); } catch { } }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public DisplayHandle? Active => _active;

    public async Task<DisplayHandle> CreateAsync(int w, int h, int r, CancellationToken ct = default)
    {
        _active = await _driver.CreateAsync(w, h, r, ct);
        StartWatchdog();
        return _active.Value;
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        if (_active is null) return;
        StopWatchdog();
        await _driver.RemoveAsync(_active.Value, ct);
        _active = null;
    }

    public async Task SetModeAsync(int w, int h, int r, CancellationToken ct = default)
    {
        if (_active is null) return;
        await _driver.SetModeAsync(_active.Value, w, h, r, ct);
    }

    private void StartWatchdog() => _watchdog.Change(500, 500);
    private void StopWatchdog() => _watchdog.Change(Timeout.Infinite, Timeout.Infinite);

    public void Dispose() => _watchdog.Dispose();
}

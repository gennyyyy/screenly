namespace Screenly.Core.Display;

public sealed class DisplayManager : IDisposable
{
    private readonly IVirtualDisplay _driver;
    private readonly Timer _watchdog;
    private DisplayHandle? _active;

    public DisplayManager(IVirtualDisplay driver)
    {
        _driver = driver;
        _watchdog = new Timer(_ => { _ = Task.Run(async () => { try { await _driver.PingAsync(); } catch { } }); }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public DisplayHandle? Active => _active;

    public async Task<DisplayHandle> CreateAsync(int w, int h, int r, CancellationToken ct = default)
    {
        _active = await _driver.CreateAsync(w, h, r, ct);
        _watchdog.Change(500, 500);
        return _active.Value;
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        if (_active is null) return;
        _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        await _driver.RemoveAsync(_active.Value, ct);
        _active = null;
    }

    public async Task SetModeAsync(int w, int h, int r, CancellationToken ct = default)
    {
        if (_active is null) return;
        await _driver.SetModeAsync(_active.Value, w, h, r, ct);
    }

    public void Dispose() => _watchdog.Dispose();
}

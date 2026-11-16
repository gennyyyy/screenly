namespace Screenly.Core.Display;

public sealed class DisplayManager : IDisposable
{
    private readonly IVirtualDisplay _driver;
    private DisplayHandle? _active;

    public DisplayManager(IVirtualDisplay driver)
    {
        _driver = driver;
    }

    public DisplayHandle? Active => _active;
    public int DisplayCount => _active is null ? 0 : 1;

    public async Task<DisplayHandle> CreateAsync(int w, int h, int r, CancellationToken ct = default)
    {
        _active = await _driver.CreateAsync(w, h, r, ct);
        return _active.Value;
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        var active = _active;
        if (active is null) return;
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _driver.RemoveAsync(active.Value, ct);
                _active = null;
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { await Task.Delay(500 * (attempt + 1), ct); } catch { }
            }
        }
        // failed: keep handle for retry; driver persisted orphaned index for cleanup on next startup
        try { Console.WriteLine($"DisplayManager.RemoveAsync failed after 3 retries: {last?.Message}"); } catch { }
    }

    public async Task SetModeAsync(int w, int h, int r, CancellationToken ct = default)
    {
        if (_active is null) return;
        await _driver.SetModeAsync(_active.Value, w, h, r, ct);
    }

    public void Dispose() { }
}

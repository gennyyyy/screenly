namespace Screenly.Core.Display;

public interface IDisplayDriver
{
    Task<DisplayHandle> AddAsync(int width, int height, int refreshHz, CancellationToken ct = default);
    Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default);
    IEnumerable<AttachedDisplay> EnumerateAttached();
    Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct = default);
    DisplayMode? ReadMode(string deviceName);
    string? ResolveIdentity(DisplayHandle handle);
    Task PingAsync(CancellationToken ct = default);
}
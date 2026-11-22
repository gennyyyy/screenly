namespace Screenly.Core.Display;

public readonly record struct DisplayHandle(int Id);
public readonly record struct DisplayInfo(int Id, string Name, int Width, int Height, int RefreshHz, bool IsVirtual);
public readonly record struct AttachedDisplay(DisplayHandle Handle, string DeviceName, string Identity, int Width, int Height, int RefreshHz);
public readonly record struct DisplayMode(int Width, int Height, int RefreshHz);

public interface IVirtualDisplay
{
    Task<DisplayHandle> CreateAsync(int width, int height, int refreshHz, CancellationToken ct = default);
    Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default);
    Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct = default);
    Task PingAsync(CancellationToken ct = default);
    IEnumerable<DisplayInfo> Enumerate();
}

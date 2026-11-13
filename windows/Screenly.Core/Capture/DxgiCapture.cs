namespace Screenly.Core.Capture;

public sealed record CapturedFrame(IntPtr Texture, int Width, int Height, ulong TimestampUs);

public interface ICapture : IDisposable
{
    void Start(int displayId);
    bool TryGetFrame(out CapturedFrame frame);
    void Stop();
}

// DXGI Desktop Duplication capture. Native interop (D3D11/DXGI) required;
// implement against the proven pattern in deskstream (C#/.NET 8).
// https://github.com/erdo-enes/deskstream
public sealed class DxgiCapture : ICapture
{
    private int _displayId;
    private bool _running;

    public void Start(int displayId)
    {
        _displayId = displayId;
        _running = true;
        // TODO: create D3D11 device, DuplicateOutput for target display, start AcquireNextFrame loop on a background thread.
    }

    public bool TryGetFrame(out CapturedFrame frame)
    {
        frame = new CapturedFrame(IntPtr.Zero, 0, 0, 0);
        // TODO: return latest frame from single-slot buffer; null if none ready.
        return false;
    }

    public void Stop() => _running = false;

    public void Dispose() => Stop();
}

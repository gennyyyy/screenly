using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenly.Core.Capture;

public sealed record CapturedFrame(byte[] JpegData, int Width, int Height, ulong TimestampUs);

public interface ICapture : IDisposable
{
    void Start(int displayId);
    bool TryGetFrame(out CapturedFrame frame);
    void Stop();
}

// GDI BitBlt capture (works on any display; captures primary screen by default).
// Lower performance than DXGI Desktop Duplication but needs no native interop beyond GDI.
public sealed class GdiCapture : ICapture
{
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;

    private int _width;
    private int _height;
    private bool _running;
    private readonly int _quality;

    public GdiCapture(int quality = 70) { _quality = quality; }

    public void Start(int displayId)
    {
        _width = GetSystemMetrics(SM_CXSCREEN);
        _height = GetSystemMetrics(SM_CYSCREEN);
        _running = true;
    }

    public bool TryGetFrame(out CapturedFrame frame)
    {
        frame = default!;
        if (!_running) return false;
        try
        {
            var hdcSrc = GetDC(IntPtr.Zero);
            var hdcMem = CreateCompatibleDC(hdcSrc);
            var hBitmap = CreateCompatibleBitmap(hdcSrc, _width, _height);
            SelectObject(hdcMem, hBitmap);
            BitBlt(hdcMem, 0, 0, _width, _height, hdcSrc, 0, 0, SRCCOPY);

            using var bmp = Image.FromHbitmap(hBitmap);
            using var ms = new MemoryStream();
            var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_quality);
            bmp.Save(ms, jpegCodec, encParams);

            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcSrc);

            frame = new CapturedFrame(ms.ToArray(), _width, _height, (ulong)DateTime.UtcNow.Ticks);
            return true;
        }
        catch { return false; }
    }

    public void Stop() => _running = false;
    public void Dispose() => Stop();
}

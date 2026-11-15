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

// GDI BitBlt capture. Captures a specific monitor by device name, or the
// primary screen if no device name is given. Used for the virtual (extended)
// display created by Parsec VDD.
public sealed class GdiCapture : ICapture
{
    [DllImport("gdi32.dll", CharSet = CharSet.Auto)] private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int HORZRES = 8;
    private const int VERTRES = 10;
    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

    private string? _deviceName;
    private IntPtr _hdcSrc = IntPtr.Zero;
    private int _width;
    private int _height;
    private bool _running;
    private readonly int _quality;

    public GdiCapture(int quality = 70, string? deviceName = null)
    {
        _quality = quality;
        _deviceName = deviceName;
    }

    public void Start(int displayId)
    {
        if (_deviceName == null)
        {
            // Find the Parsec VDD virtual display device
            _deviceName = FindVirtualDisplayDevice();
        }

        if (_deviceName != null)
        {
            _hdcSrc = CreateDC(_deviceName, null, null, IntPtr.Zero);
            if (_hdcSrc != IntPtr.Zero)
            {
                _width = GetDeviceCaps(_hdcSrc, HORZRES);
                _height = GetDeviceCaps(_hdcSrc, VERTRES);
                if (_width <= 0) _width = 1920;
                if (_height <= 0) _height = 1080;
            }
        }

        if (_hdcSrc == IntPtr.Zero)
        {
            _width = GetSystemMetrics(SM_CXSCREEN);
            _height = GetSystemMetrics(SM_CYSCREEN);
        }

        _running = true;
    }

    public bool TryGetFrame(out CapturedFrame frame)
    {
        frame = default;
        if (!_running) return false;
        try
        {
            IntPtr hdcSrc;
            bool createdDC = false;
            if (_hdcSrc != IntPtr.Zero)
            {
                hdcSrc = _hdcSrc;
            }
            else
            {
                hdcSrc = GetDC(IntPtr.Zero);
                createdDC = true;
            }

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
            if (createdDC) ReleaseDC(IntPtr.Zero, hdcSrc);

            frame = new CapturedFrame(ms.ToArray(), _width, _height, (ulong)DateTime.UtcNow.Ticks);
            return true;
        }
        catch { return false; }
    }

    public void Stop() => _running = false;
    public void Dispose() => Stop();

    private static string? FindVirtualDisplayDevice()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        uint i = 0;
        while (EnumDisplayDevices(null, i, ref dd, 0))
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                // Parsec VDD devices typically have "Parsec" or "PSCCDD" in the name/string
                if (dd.DeviceString.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                    dd.DeviceName.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                    dd.DeviceID.Contains("Parsec", StringComparison.OrdinalIgnoreCase))
                {
                    return dd.DeviceName;
                }
            }
            i++;
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return null; // fall back to primary
    }
}

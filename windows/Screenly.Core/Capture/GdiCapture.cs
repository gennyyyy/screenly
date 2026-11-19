using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Screenly.Core.Capture;

public sealed record CapturedFrame(byte[] JpegData, int Width, int Height, ulong TimestampUs);

public interface ICapture : IDisposable
{
    void Start(int displayId);
    bool TryGetFrame(out CapturedFrame frame);
    void Stop();
    bool RefreshDimensions();
    int MonitorX { get; }
    int MonitorY { get; }
    int Width { get; }
    int Height { get; }
    // 0 = 0°, 1 = 90°, 2 = 180°, 3 = 270° clockwise (DEVMODE.dmDisplayOrientation).
    int Orientation { get; }
}

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
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int HORZRES = 8;
    private const int VERTRES = 10;
    private const int SRCCOPY = 0x00CC0020;

    private string? _deviceName;
    private IntPtr _hdcSrc = IntPtr.Zero;
    private int _width;
    private int _height;
    private int _monitorX;
    private int _monitorY;
    private IntPtr _monitorHandle = IntPtr.Zero;
    private int _monitorDpi = 96;
    private bool _running;
    private int _orientation;
    public int MonitorX => _monitorX;
    public int MonitorY => _monitorY;
    public int Width => _width;
    public int Height => _height;
    public int Orientation => _orientation;
    private readonly int _quality;
    private static ImageCodecInfo? _jpegCodec;

    public GdiCapture(int quality = 70, string? deviceName = null)
    {
        _quality = quality;
        _deviceName = deviceName;
    }

    public void Start(int displayId)
    {
        if (_deviceName == null) _deviceName = FindVirtualDisplayDevice();
        if (_deviceName != null)
        {
            _hdcSrc = CreateDC(_deviceName, null, null, IntPtr.Zero);
            if (_hdcSrc != IntPtr.Zero)
            {
                // Use EnumDisplaySettings to get the actual resolution
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
                if (EnumDisplaySettings(_deviceName, -1, ref dm))
                {
                    _width = dm.dmPelsWidth;
                    _height = dm.dmPelsHeight;
                    _orientation = DisplayOrientation.Normalize(dm.dmDisplayOrientation);
                }
                else
                {
                    _width = GetDeviceCaps(_hdcSrc, HORZRES);
                    _height = GetDeviceCaps(_hdcSrc, VERTRES);
                }
                if (_width <= 0) _width = 1920;
                if (_height <= 0) _height = 1080;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCallback, IntPtr.Zero);
                _monitorDpi = CursorDrawer.GetMonitorDpi(_monitorHandle);
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
            if (_hdcSrc != IntPtr.Zero) hdcSrc = _hdcSrc;
            else { hdcSrc = GetDC(IntPtr.Zero); createdDC = true; }

            // Memory surface must come from the primary screen DC: DrawIconEx silently
            // draws nothing when the bitmap is created from a secondary display DC.
            var screenDc = GetDC(IntPtr.Zero);
            var hdcMem = CreateCompatibleDC(screenDc);
            var hBitmap = CreateCompatibleBitmap(screenDc, _width, _height);
            SelectObject(hdcMem, hBitmap);
            BitBlt(hdcMem, 0, 0, _width, _height, hdcSrc, 0, 0, SRCCOPY);

            using var bmp = Image.FromHbitmap(hBitmap);
            using var ms = new MemoryStream();
            var jpegCodec = _jpegCodec ??= ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_quality);
            bmp.Save(ms, jpegCodec, encParams);

            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, screenDc);
            if (createdDC) ReleaseDC(IntPtr.Zero, hdcSrc);

            frame = new CapturedFrame(ms.ToArray(), _width, _height, MonotonicClock.NowUs);
            return true;
        }
        catch { return false; }
    }

    private bool MonitorCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfo(hMonitor, ref mi) && mi.szDevice == _deviceName)
        {
            _monitorX = mi.rcMonitor.left;
            _monitorY = mi.rcMonitor.top;
            _monitorHandle = hMonitor;
        }
        return true;
    }

    public bool RefreshDimensions()
    {
        if (_deviceName == null)
        {
            // Fallback: primary screen capture. There is no per-monitor DEVMODE here, so the
            // orientation stays 0 (unrotated) for the lifetime of this capture.
            int sw = GetSystemMetrics(SM_CXSCREEN);
            int sh = GetSystemMetrics(SM_CYSCREEN);
            if (sw > 0 && sh > 0 && (sw != _width || sh != _height))
            {
                _width = sw;
                _height = sh;
                return true;
            }
            return false;
        }

        int oldW = _width, oldH = _height, oldOrientation = _orientation;
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (EnumDisplaySettings(_deviceName, -1, ref dm))
        {
            int w = dm.dmPelsWidth;
            int h = dm.dmPelsHeight;
            if (w <= 0) w = _width;
            if (h <= 0) h = _height;
            _width = w;
            _height = h;
            _orientation = DisplayOrientation.Normalize(dm.dmDisplayOrientation);
        }
        int oldX = _monitorX, oldY = _monitorY;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCallback, IntPtr.Zero);
        if (_monitorHandle != IntPtr.Zero)
            _monitorDpi = CursorDrawer.GetMonitorDpi(_monitorHandle);
        // 0↔2 (and 1↔3) rotate without swapping pixels, so orientation alone must report a change.
        return _width != oldW || _height != oldH || _orientation != oldOrientation
            || _monitorX != oldX || _monitorY != oldY;
    }
    public void Stop() => _running = false;
    public void Dispose() => Stop();

    public static string? FindVirtualDisplayDevicePublic() => FindVirtualDisplayDevice();
    private static string? FindVirtualDisplayDevice()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        // Return the LAST Parsec device (most recently created) to handle multi-display.
        string? last = null;
        uint i = 0;
        while (EnumDisplayDevices(null, i, ref dd, 0))
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                if (dd.DeviceString.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                    dd.DeviceName.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                    dd.DeviceID.Contains("Parsec", StringComparison.OrdinalIgnoreCase))
                {
                    last = dd.DeviceName;
                }
            }
            i++;
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return last;
    }
}
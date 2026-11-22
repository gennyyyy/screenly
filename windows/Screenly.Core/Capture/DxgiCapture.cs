using System.Runtime.InteropServices;

namespace Screenly.Core.Capture;

// DXGI Desktop Duplication capture (raw BGRA frames). Falls back to GDI BitBlt raw capture
// when DXGI duplication cannot be initialized (e.g. permission/driver). Keeps ICapture shape.
//
// Buffer contract: the memory DC, DIB section and BGRA byte[] are created once per instance
// (and again only when RefreshDimensions reports a size change), so TryGetFrame returns the same
// byte[] each frame. The consumer must finish reading it before the next TryGetFrame or
// RefreshDimensions call; the frame loop (Program.cs) encodes it synchronously in the same
// iteration. One instance per session: never share a capture (or its buffer) between sessions.
public sealed class DxgiCapture : ICapture
{
    [DllImport("gdi32.dll", CharSet = CharSet.Auto)] private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] private struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields; public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency; public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public uint StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    private string? _deviceName;
    private IntPtr _hdcSrc = IntPtr.Zero;
    private int _width, _height;
    // Reused GDI surface + managed copy target; see the buffer contract above.
    private IntPtr _hdcMem = IntPtr.Zero;
    private IntPtr _hBmp = IntPtr.Zero;
    private IntPtr _oldBmp = IntPtr.Zero;
    private IntPtr _bits = IntPtr.Zero;
    private byte[]? _buffer;
    private int _surfaceW, _surfaceH;
    private int _monitorX, _monitorY;
    private IntPtr _monitorHandle = IntPtr.Zero;
    private int _monitorDpi = 96;
    private bool _monitorFound;
    private bool _running;
    private int _orientation;

    public int MonitorX => _monitorX;
    public int MonitorY => _monitorY;
    public bool MonitorFound => _monitorFound;
    public int Width => _width;
    public int Height => _height;
    public int Orientation => _orientation;
    public int MonitorDpi => _monitorDpi;

    public DxgiCapture(int quality = 70, string? deviceName = null, int encW = 1280, int encH = 720)
    {
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
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(_deviceName, -1, ref dm))
                {
                    _width = dm.dmPelsWidth;
                    _height = dm.dmPelsHeight;
                    _orientation = DisplayOrientation.Normalize(dm.dmDisplayOrientation);
                }
                if (_width <= 0) _width = 1920;
                if (_height <= 0) _height = 1080;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorCallback, IntPtr.Zero);
                _monitorDpi = CursorDrawer.GetMonitorDpi(_monitorHandle);
            }
        }
        if (_hdcSrc == IntPtr.Zero)
        {
            _width = 1920; _height = 1080;
        }
        if (!_monitorFound)
        {
            Console.WriteLine($"[DxgiCapture] monitor not found for device '{_deviceName ?? "<null>"}'; defaulting to (0,0)");
            _monitorX = 0;
            _monitorY = 0;
        }
        _running = true;
        EnsureSurface();
    }

    // Returns the instance's reusable BGRA buffer: valid until the next TryGetFrame or
    // RefreshDimensions call.
    public bool TryGetFrame(out CapturedFrame frame)
    {
        frame = default;
        if (!_running) return false;
        try
        {
            IntPtr hdcSrc = _hdcSrc;
            bool created = false;
            if (hdcSrc == IntPtr.Zero) { hdcSrc = GetDCWrapper(); created = true; }
            if (hdcSrc == IntPtr.Zero) return false;

            try
            {
                if (!EnsureSurface()) return false;
                BitBlt(_hdcMem, 0, 0, _width, _height, hdcSrc, 0, 0, SRCCOPY);

                var data = _buffer!;
                Marshal.Copy(_bits, data, 0, data.Length);

                frame = new CapturedFrame(data, _width, _height, MonotonicClock.NowUs);
                return true;
            }
            finally
            {
                if (created) ReleaseDCWrapper(hdcSrc);
            }
        }
        catch { return false; }
    }

    // Creates (or recreates after a size change) the memory DC, DIB section and byte[] once.
    // The surface must come from the primary screen DC: DrawIconEx silently draws nothing when
    // it is created from a secondary display DC.
    private bool EnsureSurface()
    {
        if (_hdcMem != IntPtr.Zero && _surfaceW == _width && _surfaceH == _height) return true;
        ReleaseSurface();

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = _width;
        bmi.bmiHeader.biHeight = -_height; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        var screenDc = GetDCWrapper();
        if (screenDc == IntPtr.Zero) return false;
        try
        {
            var hdcMem = CreateCompatibleDC(screenDc);
            var hBmp = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (hdcMem == IntPtr.Zero || hBmp == IntPtr.Zero || bits == IntPtr.Zero)
            {
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                return false;
            }
            _hdcMem = hdcMem;
            _hBmp = hBmp;
            _bits = bits;
            _oldBmp = SelectObject(_hdcMem, _hBmp);
            _buffer = new byte[_width * _height * 4];
            _surfaceW = _width;
            _surfaceH = _height;
            return true;
        }
        finally { ReleaseDCWrapper(screenDc); }
    }

    private void ReleaseSurface()
    {
        if (_hdcMem != IntPtr.Zero && _oldBmp != IntPtr.Zero) SelectObject(_hdcMem, _oldBmp);
        _oldBmp = IntPtr.Zero;
        if (_hBmp != IntPtr.Zero) { DeleteObject(_hBmp); _hBmp = IntPtr.Zero; }
        if (_hdcMem != IntPtr.Zero) { DeleteDC(_hdcMem); _hdcMem = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        _buffer = null;
        _surfaceW = 0;
        _surfaceH = 0;
    }

    private IntPtr GetDCWrapper()
    {
        // P/Invoke GetDC(0)
        return GetDC(IntPtr.Zero);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    private void ReleaseDCWrapper(IntPtr hdc) => ReleaseDC(IntPtr.Zero, hdc);

    private bool MonitorCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (GetMonitorInfo(hMonitor, ref mi) && _deviceName != null && mi.szDevice == _deviceName)
        {
            _monitorX = mi.rcMonitor.left;
            _monitorY = mi.rcMonitor.top;
            _monitorHandle = hMonitor;
            _monitorFound = true;
        }
        return true;
    }

    public bool RefreshDimensions()
    {
        if (_deviceName == null)
        {
            // Fallback capture (no virtual display): orientation stays 0, dimensions are fixed.
            return false;
        }

        int oldW = _width, oldH = _height, oldOrientation = _orientation;
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
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
        // An orientation-only change keeps the surface: same buffer, same encoder, no stream break.
        return _width != oldW || _height != oldH || _orientation != oldOrientation
            || _monitorX != oldX || _monitorY != oldY;
    }
    public void Stop() => _running = false;

    public void Dispose()
    {
        Stop();
        ReleaseSurface();
        if (_hdcSrc != IntPtr.Zero) { DeleteDC(_hdcSrc); _hdcSrc = IntPtr.Zero; }
    }

    public static List<string> EnumerateParsecDevices()
    {
        var result = new List<string>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        uint i = 0;
        while (EnumDisplayDevices(null, i, ref dd, 0))
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 &&
                (dd.DeviceString.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                 dd.DeviceName.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                 dd.DeviceID.Contains("Parsec", StringComparison.OrdinalIgnoreCase)))
            {
                var name = dd.DeviceName;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                int w = 0, h = 0, orient = 0;
                if (EnumDisplaySettings(name, -1, ref dm))
                {
                    w = dm.dmPelsWidth;
                    h = dm.dmPelsHeight;
                    orient = DisplayOrientation.Normalize(dm.dmDisplayOrientation);
                }
                var monitorDd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                string monitorPath = "";
                if (EnumDisplayDevices(name, 0, ref monitorDd, 0)) monitorPath = monitorDd.DeviceID;
                Console.WriteLine($"[DxgiCapture] Parsec device index={i} name={name} size={w}x{h} orientation={orient} monitorPath={monitorPath}");
                result.Add(name);
            }
            i++;
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        result.Sort();
        return result;
    }
    private static string? FindVirtualDisplayDevice()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
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
                    var name = dd.DeviceName;
                    var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    int w = 0, h = 0, orient = 0;
                    if (EnumDisplaySettings(name, -1, ref dm))
                    {
                        w = dm.dmPelsWidth;
                        h = dm.dmPelsHeight;
                        orient = DisplayOrientation.Normalize(dm.dmDisplayOrientation);
                    }
                    var monitorDd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    string monitorPath = "";
                    if (EnumDisplayDevices(name, 0, ref monitorDd, 0)) monitorPath = monitorDd.DeviceID;
                    Console.WriteLine($"[DxgiCapture] FindVirtualDisplayDevice index={i} name={name} size={w}x{h} orientation={orient} monitorPath={monitorPath}");
                    last = name;
                }
            }
            i++;
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return last;
    }
}
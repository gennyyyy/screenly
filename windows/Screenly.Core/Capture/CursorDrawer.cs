using System.Drawing;
using System.Runtime.InteropServices;

namespace Screenly.Core.Capture;

/// <summary>
/// Shared helper for compositing the native Windows cursor into a captured frame.
/// Used by both GdiCapture (JPEG) and DxgiCapture (H.264) paths.
/// </summary>
public static class CursorDrawer
{
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>
    /// Resolves the effective DPI of a monitor handle via shcore. Falls back to 96.
    /// </summary>
    public static int GetMonitorDpi(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero) return 96;
        try
        {
            uint dpiX = 0, dpiY = 0;
            int hr = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            if (hr == 0 && dpiX > 0) return (int)dpiX;
        }
        catch { }
        return 96;
    }

    /// <summary>
    /// Draws the current Windows cursor onto the given HDC at the correct position.
    /// Returns true if cursor was drawn, false if hidden/invalid/out of bounds.
    /// </summary>
    public static bool Draw(IntPtr hdc, int monitorX, int monitorY, int width, int height, int monitorDpi)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return false;
        if ((ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return false;

        int cx = ci.ptScreenPos.x - monitorX;
        int cy = ci.ptScreenPos.y - monitorY;
        if (monitorDpi <= 0) monitorDpi = 96;
        int cursorSize = (int)Math.Round(32.0 * monitorDpi / 96.0);
        if (cx < -cursorSize || cy < -cursorSize || cx >= width + cursorSize || cy >= height + cursorSize) return false;

        // GetIconInfo to obtain native hotspot and free bitmap handles
        var ii = new ICONINFO();
        if (GetIconInfo(ci.hCursor, out ii))
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }

        // DrawIconEx with explicit size so cursor scales with the target monitor's DPI
        DrawIconEx(hdc, cx, cy, ci.hCursor, cursorSize, cursorSize, 0, IntPtr.Zero, DI_NORMAL);
        return true;
    }
}
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

    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    /// <summary>
    /// Draws the current Windows cursor onto the given HDC at the correct position.
    /// Returns true if cursor was drawn, false if hidden/invalid/out of bounds.
    /// </summary>
    public static bool Draw(IntPtr hdc, int monitorX, int monitorY, int width, int height)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return false;
        if ((ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return false;

        int cx = ci.ptScreenPos.x - monitorX;
        int cy = ci.ptScreenPos.y - monitorY;
        if (cx < -32 || cy < -32 || cx >= width + 32 || cy >= height + 32) return false;

        // GetIconInfo to obtain native hotspot and free bitmap handles
        var ii = new ICONINFO();
        if (GetIconInfo(ci.hCursor, out ii))
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }

        // DrawIconEx with cursor handle uses hotspot as origin → pass (cx, cy) directly
        DrawIconEx(hdc, cx, cy, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        return true;
    }
}
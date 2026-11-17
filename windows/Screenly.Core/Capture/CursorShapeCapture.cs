using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenly.Core.Capture;

/// <summary>
/// Captures the current Windows cursor shape as a transparent 32-bit PNG.
/// Returns null if the cursor is hidden or capture fails.
/// </summary>
public static class CursorShapeCapture
{
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);

    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType; public int bmWidth; public int bmHeight; public int bmWidthBytes; public ushort bmPlanes; public ushort bmBitsPixel; public IntPtr bmBits; }
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    public static (int width, int height, int hotspotX, int hotspotY, byte[] png)? Capture()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return null;
        if ((ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return null;

        var ii = new ICONINFO();
        if (!GetIconInfo(ci.hCursor, out ii)) return null;

        int hx = ii.xHotspot;
        int hy = ii.yHotspot;

        // Get dimensions from the color bitmap (or mask if monochrome)
        int width = 32, height = 32;
        var bmpInfo = new BITMAP();
        IntPtr bmpHandle = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
        if (bmpHandle != IntPtr.Zero && GetObject(bmpHandle, Marshal.SizeOf<BITMAP>(), ref bmpInfo) > 0)
        {
            width = bmpInfo.bmWidth;
            height = ii.hbmColor != IntPtr.Zero ? bmpInfo.bmHeight : bmpInfo.bmHeight / 2;
            if (width <= 0) width = 32;
            if (height <= 0) height = 32;
        }

        // Free bitmap handles
        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);

        // Render cursor to a 32-bit bitmap using GDI+
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            // Clear to transparent
            // (Bitmap is already initialized to 0 which is transparent in ARGB)
            DrawIconEx(hdc, 0, 0, ci.hCursor, width, height, 0, IntPtr.Zero, DI_NORMAL);
            g.ReleaseHdc(hdc);
        }

        // Replace the drop shadow with a 1px black outline around the cursor.
        // Shadow pixels are dropped; outline is drawn in the freed transparent
        // cells adjacent to opaque cursor pixels (size preserved as much as possible).
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        // copy non-shadow (cursor) pixels
        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
            {
                var c = bmp.GetPixel(px, py);
                if (c.A == 0) continue;
                // Drop shadow: semi-transparent OR medium-gray (solid gray shadow).
                int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                bool gray = (mx - mn) < 30;
                int brightness = c.R + c.G + c.B;
                bool mediumGrayShadow = gray && brightness > 100 && brightness < 600;
                if (c.A < 200 || mediumGrayShadow) continue;
                result.SetPixel(px, py, c);
            }
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(result, 0, 0);
        }
        result.Dispose();

        // Encode as PNG
        using var ms = new MemoryStream();
        var pngCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/png");
        if (pngCodec == null) return null;
        bmp.Save(ms, pngCodec, null);

        return (width, height, hx, hy, ms.ToArray());
    }
}
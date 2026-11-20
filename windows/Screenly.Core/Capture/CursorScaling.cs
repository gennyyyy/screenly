namespace Screenly.Core.Capture;

/// <summary>
/// Pure DPI math for <see cref="CursorShapeCapture"/>: rendered cursor size and hotspot mapping.
/// Kept free of P/Invoke so the Windows Settings scaling factors are unit-testable.
/// </summary>
public static class CursorScaling
{
    public const int BaseDpi = 96;

    public static int NormalizeDpi(int dpi) => dpi <= 0 ? BaseDpi : dpi;

    /// <summary>
    /// Cursor edge at <paramref name="dpi"/> for a 96-DPI size. Rounds half away from zero, matching
    /// <c>GetSystemMetricsForDpi</c> (e.g. 32 px at 125% = 40, at 150% = 48).
    /// </summary>
    public static int SizeAtDpi(int size96, int dpi)
    {
        if (size96 <= 0) return 0;
        return (int)Math.Round(size96 * (double)NormalizeDpi(dpi) / BaseDpi, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Maps a native hotspot into a rendered bitmap of <paramref name="renderedSize"/>, clamped
    /// inside the image so an oversized hotspot never draws the cursor off its tip.
    /// </summary>
    public static int HotspotAtScale(int hotspot, int nativeSize, int renderedSize)
    {
        if (nativeSize <= 0 || renderedSize <= 0) return 0;
        int scaled = (int)Math.Round(hotspot * (double)renderedSize / nativeSize, MidpointRounding.AwayFromZero);
        return Math.Clamp(scaled, 0, renderedSize - 1);
    }
}

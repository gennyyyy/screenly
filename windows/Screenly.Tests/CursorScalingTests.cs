using Screenly.Core.Capture;
using Xunit;

namespace Screenly.Tests;

// Cursor PNG size and hotspot must follow the Windows display scale (100/125/150/200%);
// Android draws the bitmap 1:1 inside the stream overlay, so the scaling belongs here.
public class CursorScalingTests
{
    [Theory]
    [InlineData(96, 32)]   // 100%
    [InlineData(120, 40)]  // 125%
    [InlineData(144, 48)]  // 150%
    [InlineData(192, 64)]  // 200%
    public void SizeAtDpi_ScalesFromBase96(int dpi, int expected)
    {
        Assert.Equal(expected, CursorScaling.SizeAtDpi(32, dpi));
    }

    [Theory]
    [InlineData(32, 110, 37)] // 36.67 rounds up
    [InlineData(33, 120, 41)] // 41.25 rounds down
    [InlineData(2, 120, 3)]   // 2.5 rounds away from zero, not banker's 2
    [InlineData(32, 0, 32)]   // invalid DPI falls back to 96
    [InlineData(0, 144, 0)]   // no base metric
    public void SizeAtDpi_RoundsHalfAwayFromZero(int size96, int dpi, int expected)
    {
        Assert.Equal(expected, CursorScaling.SizeAtDpi(size96, dpi));
    }

    [Theory]
    [InlineData(96, 16, 16)]
    [InlineData(120, 10, 13)] // 10 * 40/32 = 12.5 rounds up
    [InlineData(144, 16, 24)]
    [InlineData(192, 4, 8)]
    [InlineData(192, 32, 63)] // right edge clamps inside the 64 px image
    public void HotspotAtScale_FollowsRenderedSize(int dpi, int hotspot, int expected)
    {
        int rendered = CursorScaling.SizeAtDpi(32, dpi);
        Assert.Equal(expected, CursorScaling.HotspotAtScale(hotspot, 32, rendered));
    }

    [Theory]
    [InlineData(64, 48, 47)] // beyond the right edge clamps inside
    [InlineData(-5, 48, 0)]  // negative clamps to the left edge
    [InlineData(16, 0, 0)]   // no rendered bitmap
    public void HotspotAtScale_ClampsToImageBounds(int hotspot, int renderedSize, int expected)
    {
        Assert.Equal(expected, CursorScaling.HotspotAtScale(hotspot, 32, renderedSize));
    }

    [Fact]
    public void HotspotAtScale_NoNativeSize_ReturnsZero()
    {
        Assert.Equal(0, CursorScaling.HotspotAtScale(16, 0, 48));
    }
}

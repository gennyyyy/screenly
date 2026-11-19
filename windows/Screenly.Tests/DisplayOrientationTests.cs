using Screenly.Core.Capture;
using Xunit;

namespace Screenly.Tests;

// DEVMODE.dmDisplayOrientation is driver-supplied: validate it before it reaches CONFIG/status.json.
public class DisplayOrientationTests
{
    [Theory]
    [InlineData(0, 0)] // DMDO_DEFAULT
    [InlineData(1, 1)] // DMDO_90
    [InlineData(2, 2)] // DMDO_180
    [InlineData(3, 3)] // DMDO_270
    public void Normalize_KnownValues_PassThrough(int input, int expected)
    {
        Assert.Equal(expected, DisplayOrientation.Normalize(input));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(90)] // degrees instead of DMDO code
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Normalize_UnknownValue_FallsBackToUnrotated(int input)
    {
        Assert.Equal(DisplayOrientation.Default, DisplayOrientation.Normalize(input));
    }

    [Fact]
    public void Normalize_DefaultIsZero()
    {
        Assert.Equal(0, DisplayOrientation.Default);
    }
}

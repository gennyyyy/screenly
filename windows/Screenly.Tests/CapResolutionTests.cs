using Screenly.Core.Config;
using Xunit;

namespace Screenly.Tests;

public class CapResolutionTests
{
    [Fact]
    public void CapResolution_FitsWithinCap_ReturnsClient()
    {
        Assert.Equal((1280, 720), ConfigStore.CapResolution(1280, 720, 1920, 1080));
    }

    [Fact]
    public void CapResolution_ExceedsCap_ScalesDown()
    {
        Assert.Equal((1920, 1080), ConfigStore.CapResolution(3840, 2160, 1920, 1080));
    }

    [Fact]
    public void CapResolution_Portrait_CappedToLandscapeCap()
    {
        // 1080x1920 client, 1920x1080 cap -> scale by height
        Assert.Equal((608, 1080), ConfigStore.CapResolution(1080, 1920, 1920, 1080));
    }

    [Fact]
    public void CapResolution_ZeroOrNegative_ReturnsInput()
    {
        Assert.Equal((0, 0), ConfigStore.CapResolution(0, 0, 1920, 1080));
        Assert.Equal((-1, 5), ConfigStore.CapResolution(-1, 5, 1920, 1080));
    }

    [Fact]
    public void ScaleToCap_MatchesCapResolution()
    {
        Assert.Equal(ConfigStore.CapResolution(1080, 1920, 1920, 1080),
                     ConfigStore.ScaleToCap(1080, 1920, 1920, 1080));
    }

    [Fact]
    public void CapResolution_EqualCap_ReturnsClient()
    {
        Assert.Equal((1920, 1080), ConfigStore.CapResolution(1920, 1080, 1920, 1080));
    }
}

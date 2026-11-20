using Screenly.Core.Display;
using Xunit;

namespace Screenly.Tests;

public class VirtualDisplayModeTests
{
    private static readonly VirtualDisplayMode.Mode Requested = new(3200, 2136, 60);
    private static readonly VirtualDisplayMode.Mode DefaultMode = new(1280, 800, 60);

    [Fact]
    public async Task WaitForDevice_ReturnsNameOnceProbeSeesIt()
    {
        int calls = 0;
        var name = await VirtualDisplayMode.WaitForDeviceAsync(
            () => ++calls >= 3 ? @"\\.\DISPLAY9" : null,
            timeoutMs: 500, pollIntervalMs: 1);

        Assert.Equal(@"\\.\DISPLAY9", name);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WaitForDevice_TimesOutWhenDeviceNeverAppears()
    {
        int calls = 0;
        var name = await VirtualDisplayMode.WaitForDeviceAsync(
            () => { calls++; return null; },
            timeoutMs: 40, pollIntervalMs: 5);

        Assert.Null(name);
        Assert.True(calls >= 2, $"probe should be polled, called {calls} time(s)");
    }

    [Fact]
    public async Task ApplyAndVerify_RetriesOnceThenVerifies()
    {
        var current = DefaultMode;
        int applies = 0;
        var result = await VirtualDisplayMode.ApplyAndVerifyAsync(
            "fake", Requested,
            readMode: _ => current,
            applyMode: (_, mode) =>
            {
                applies++;
                if (applies >= 2) current = mode; // first apply does not take
                return applies >= 2;
            },
            retryDelayMs: 1);

        Assert.True(result.Verified);
        Assert.Equal(2, applies);
        Assert.Equal(Requested, result.Actual);
    }

    [Fact]
    public async Task ApplyAndVerify_FailsAfterOneRetryWithOldModeStillActive()
    {
        int applies = 0;
        var result = await VirtualDisplayMode.ApplyAndVerifyAsync(
            "fake", Requested,
            readMode: _ => DefaultMode,
            applyMode: (_, _) => { applies++; return false; },
            retryDelayMs: 1);

        Assert.False(result.Verified);
        Assert.Equal(VirtualDisplayMode.ApplyAttempts, applies);
        Assert.Equal(DefaultMode, result.Actual);
    }

    [Fact]
    public async Task ApplyAndVerify_RejectsWrongSizeEvenWhenApplyReportsSuccess()
    {
        int applies = 0;
        var result = await VirtualDisplayMode.ApplyAndVerifyAsync(
            "fake", Requested,
            readMode: _ => DefaultMode,
            applyMode: (_, _) => { applies++; return true; },
            retryDelayMs: 1);

        Assert.False(result.Verified);
        Assert.Equal(2, applies);
    }

    [Fact]
    public async Task ApplyAndVerify_AcceptsRightSizeWhenDriverDoesNotReportRefresh()
    {
        var result = await VirtualDisplayMode.ApplyAndVerifyAsync(
            "fake", Requested,
            readMode: _ => new VirtualDisplayMode.Mode(Requested.Width, Requested.Height, 0),
            applyMode: (_, _) => true,
            retryDelayMs: 1);

        Assert.True(result.Verified);
    }

    [Fact]
    public async Task ApplyAndVerify_AcceptsRoundedRefreshReadback()
    {
        var result = await VirtualDisplayMode.ApplyAndVerifyAsync(
            "fake", new VirtualDisplayMode.Mode(1920, 1080, 60),
            readMode: _ => new VirtualDisplayMode.Mode(1920, 1080, 59),
            applyMode: (_, _) => true,
            retryDelayMs: 1);

        Assert.True(result.Verified);
    }
}

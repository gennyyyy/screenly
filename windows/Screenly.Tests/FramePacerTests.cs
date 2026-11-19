using Screenly.Core.Streaming;
using Xunit;

namespace Screenly.Tests;

public class FramePacerTests
{
    [Fact]
    public void NormalCadence_FirstFrameRunsImmediately_ThenWaitsPeriodMinusWork()
    {
        var pacer = new FramePacer(30);
        long period = pacer.PeriodTicks;
        long work = period / 4;

        long now = 0;
        Assert.Equal(0, pacer.TicksUntilNextFrame(now)); // first frame is due now

        now += work; // frame work
        var wait = pacer.TicksUntilNextFrame(now);
        Assert.InRange(wait, period - work - 1, period - work + 1);

        now += wait + work; // slept, then the next frame's work
        wait = pacer.TicksUntilNextFrame(now);
        Assert.InRange(wait, period - work - 1, period - work + 1);
    }

    [Fact]
    public void Overrun_ResetsDeadline_InsteadOfBursting()
    {
        var pacer = new FramePacer(30);
        long period = pacer.PeriodTicks;

        Assert.Equal(0, pacer.TicksUntilNextFrame(0));            // deadline anchored at period
        Assert.Equal(0, pacer.TicksUntilNextFrame(period * 5));   // 5 periods late: due now, re-anchored

        // One tick of work after the reset: a full period is still owed, not a catch-up burst.
        var afterOverrun = pacer.TicksUntilNextFrame(period * 5 + 1);
        Assert.InRange(afterOverrun, period - 1, period);
    }

    [Fact]
    public void Overruns_CountOnlyDeadlineRebases()
    {
        var pacer = new FramePacer(60);
        long period = pacer.PeriodTicks;

        Assert.Equal(0, pacer.TicksUntilNextFrame(0)); // first frame: no overrun
        Assert.Equal(0, pacer.Overruns);

        pacer.TicksUntilNextFrame(period / 2); // work within the period
        Assert.Equal(0, pacer.Overruns);

        Assert.Equal(0, pacer.TicksUntilNextFrame(period * 10)); // late by many periods: rebase
        Assert.Equal(1, pacer.Overruns);

        pacer.TicksUntilNextFrame(period * 10 + period / 2); // work within the period again
        Assert.Equal(1, pacer.Overruns);

        pacer.TicksUntilNextFrame(period * 30); // slept past the deadline: also a rebase
        Assert.Equal(2, pacer.Overruns);
    }

    [Fact]
    public void TargetFps_IsClampedOrDefaults()
    {
        Assert.Equal(FramePacer.DefaultFps, new FramePacer(0).TargetFps);
        Assert.Equal(FramePacer.DefaultFps, new FramePacer(-15).TargetFps);
        Assert.Equal(1, new FramePacer(1).TargetFps);
        Assert.Equal(120, new FramePacer(120).TargetFps);
        Assert.Equal(FramePacer.MaxFps, new FramePacer(1000).TargetFps);
    }

    [Fact]
    public void Delay_IsNeverNegative()
    {
        var pacer = new FramePacer(60);

        Assert.True(pacer.NextDelay() >= TimeSpan.Zero);
        Assert.True(pacer.NextDelay() >= TimeSpan.Zero);
        // A very late caller gets "due now", never a negative delay that Task.Delay would reject.
        Assert.Equal(0, pacer.TicksUntilNextFrame(long.MaxValue / 4));
        Assert.True(pacer.NextDelay() >= TimeSpan.Zero);
    }

    [Fact]
    public void SteadyIntervals_DoNotDriftAcrossFrames()
    {
        var pacer = new FramePacer(60);
        long period = pacer.PeriodTicks;
        long now = 0;
        pacer.TicksUntilNextFrame(now);

        for (int i = 0; i < 600; i++)
        {
            now += period / 8; // frame work
            var wait = pacer.TicksUntilNextFrame(now);
            Assert.True(wait > 0, $"frame {i} overran unexpectedly");
            now += wait; // sleep until the deadline
        }

        // 600 frames at exactly one period each: no accumulated drift.
        Assert.Equal(600L * period, now);
    }
}

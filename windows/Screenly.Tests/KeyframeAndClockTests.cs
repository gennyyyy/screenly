using System.Diagnostics;
using Screenly.Core;
using Screenly.Core.Encoding;
using Xunit;

namespace Screenly.Tests;

public class MonotonicClockTests
{
    [Fact]
    public void ToMicroseconds_ConvertsStopwatchTicks()
    {
        long freq = Stopwatch.Frequency;

        Assert.Equal(0UL, MonotonicClock.ToMicroseconds(0));
        Assert.Equal(1_000_000UL, MonotonicClock.ToMicroseconds(freq));
        Assert.Equal(3_600_000_000UL, MonotonicClock.ToMicroseconds(freq * 3600)); // 1 hour
        Assert.InRange(MonotonicClock.ToMicroseconds(freq / 4), 249_999UL, 250_000UL);
    }

    [Fact]
    public void ToMicroseconds_LongUptime_DoesNotOverflow()
    {
        // 30 days of ticks: ticks * 1_000_000 alone would overflow a signed 64-bit int.
        ulong expected = 30UL * 24 * 3600 * 1_000_000;
        Assert.Equal(expected, MonotonicClock.ToMicroseconds(Stopwatch.Frequency * 30L * 24 * 3600));
    }

    [Fact]
    public void NowUs_IsMonotonicAndAdvancesInMicroseconds()
    {
        var a = MonotonicClock.NowUs;
        Thread.Sleep(2);
        var b = MonotonicClock.NowUs;

        Assert.True(b >= a, $"clock went backwards: {a} -> {b}");
        Assert.InRange(b - a, 200UL, 5_000_000UL);
    }
}

public class KeyframePolicyTests
{
    [Fact]
    public void FirstFrame_RequestsIdr_ThenWaitsForInterval()
    {
        var policy = new KeyframePolicy(2);

        Assert.True(policy.ShouldRequestIdr(0));
        policy.MarkIdr(0);

        Assert.False(policy.ShouldRequestIdr(1_999_999));
        Assert.True(policy.ShouldRequestIdr(2_000_000));
    }

    [Fact]
    public void RequestDrivenIdr_ResetsPeriodicSchedule()
    {
        var policy = new KeyframePolicy(2);
        policy.MarkIdr(0);
        policy.MarkIdr(1_900_000); // client asked at 1.9 s

        // No second IDR 100 ms later just because the 2 s tick came due.
        Assert.False(policy.ShouldRequestIdr(2_000_000));
        Assert.True(policy.ShouldRequestIdr(3_900_000));
    }

    [Fact]
    public void TryAcceptRequest_RateLimitsToOnePer500Ms()
    {
        var policy = new KeyframePolicy(2);

        Assert.True(policy.TryAcceptRequest(0));
        Assert.False(policy.TryAcceptRequest(499_999));
        Assert.True(policy.TryAcceptRequest(500_000));
        Assert.False(policy.TryAcceptRequest(999_999));
    }

    [Fact]
    public void Interval_IsAtLeastOneSecond()
    {
        var policy = new KeyframePolicy(0);
        policy.MarkIdr(0);

        Assert.False(policy.ShouldRequestIdr(999_999));
        Assert.True(policy.ShouldRequestIdr(1_000_000));
    }

    [Fact]
    public void AfterBackpressureGap_PeriodicIdrStillFires()
    {
        var policy = new KeyframePolicy(2);
        policy.MarkIdr(1_000_000);

        // Slot stays pending for 10 s: no frames encoded, but the policy runs on monotonic
        // capture timestamps, so the next captured frame must still ask for an IDR.
        Assert.True(policy.ShouldRequestIdr(11_000_000));
    }
}

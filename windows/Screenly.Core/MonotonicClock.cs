using System.Diagnostics;

namespace Screenly.Core;

// Monotonic microsecond clock for VIDEO_FRAME/CURSOR_POS timestamps (api-contract: timestamp_us).
// Values are intervals from an arbitrary process-local epoch, so they never jump when the wall
// clock is adjusted and are what latency measurement must use.
public static class MonotonicClock
{
    // Split the division so a long-lived process cannot overflow timestampTicks * 1_000_000.
    public static ulong ToMicroseconds(long timestampTicks)
    {
        long freq = Stopwatch.Frequency;
        return (ulong)(timestampTicks / freq * 1_000_000 + timestampTicks % freq * 1_000_000 / freq);
    }

    public static ulong NowUs => ToMicroseconds(Stopwatch.GetTimestamp());
}

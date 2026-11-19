using System.Diagnostics;

namespace Screenly.Core.Streaming;

// Stopwatch-based cadence for the capture loop (plan Phase 5: no fixed sleep).
// Deadlines are absolute, so frame work never accumulates as drift; an overrun re-anchors the
// deadline at now + period instead of bursting frames to catch up.
public sealed class FramePacer
{
    public const int MinFps = 1;
    public const int MaxFps = 120;
    public const int DefaultFps = 30;

    private readonly long _periodTicks;
    private long _nextDeadline;

    public FramePacer(int targetFps)
    {
        // Zero/negative means "unset", not 1 fps; anything above the ceiling is capped.
        TargetFps = targetFps <= 0 ? DefaultFps : Math.Min(targetFps, MaxFps);
        _periodTicks = Math.Max(1, Stopwatch.Frequency / TargetFps);
    }

    public int TargetFps { get; }
    public long PeriodTicks => _periodTicks;

    // Frames whose work overran the period and forced a deadline rebase (cumulative, single frame loop).
    public long Overruns { get; private set; }

    // Ticks to wait before the next frame at nowTicks. 0 means the frame is due now; never
    // negative, so Task.Delay cannot throw and a stalled loop does not fire a burst.
    public long TicksUntilNextFrame(long nowTicks)
    {
        if (_nextDeadline == 0)
        {
            // First frame: run immediately; its work time counts against the next period.
            _nextDeadline = nowTicks + _periodTicks;
            return 0;
        }
        if (nowTicks >= _nextDeadline)
        {
            _nextDeadline = nowTicks + _periodTicks; // overran: re-anchor at now, do not catch up
            Overruns++;
            return 0;
        }
        var wait = _nextDeadline - nowTicks;
        _nextDeadline += _periodTicks;
        return wait;
    }

    public TimeSpan NextDelay()
    {
        var ticks = TicksUntilNextFrame(Stopwatch.GetTimestamp());
        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }
}

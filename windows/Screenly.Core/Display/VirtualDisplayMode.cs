namespace Screenly.Core.Display;

// Parsec VDD ADD returns before Windows attaches/enumerates the new monitor, and
// ChangeDisplaySettingsEx only works on an enumerated device. This is the wait-then-verify
// sequence for that race. Probes are delegates so the logic is testable without a real display:
// production passes GdiCapture.FindVirtualDisplayDevicePublic and the DisplayHelper DEVMODE calls.
public static class VirtualDisplayMode
{
    public const int WaitTimeoutMs = 10_000;
    public const int PollIntervalMs = 100;
    public const int ApplyAttempts = 2;

    public readonly record struct Mode(int Width, int Height, int RefreshHz)
    {
        public override string ToString() => $"{Width}x{Height}@{RefreshHz}Hz";
    }

    public readonly record struct ApplyResult(bool Verified, Mode? Actual);

    // Polls until the probe sees the device, or the timeout elapses (null = never appeared).
    public static async Task<string?> WaitForDeviceAsync(
        Func<string?> probe,
        int timeoutMs = WaitTimeoutMs,
        int pollIntervalMs = PollIntervalMs,
        CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (true)
        {
            var name = probe();
            if (!string.IsNullOrEmpty(name)) return name;
            if (Environment.TickCount64 >= deadline) return null;
            await Task.Delay(pollIntervalMs, ct).ConfigureAwait(false);
        }
    }

    // Applies the requested mode, reads it back and verifies width/height/refresh. One retry when
    // the apply or the readback does not match. Verified=false means the display never reached the
    // requested mode; the caller must not capture from it.
    public static async Task<ApplyResult> ApplyAndVerifyAsync(
        string deviceName,
        Mode requested,
        Func<string, Mode?> readMode,
        Func<string, Mode, bool> applyMode,
        int retryDelayMs = 200,
        CancellationToken ct = default)
    {
        Mode? actual = null;
        for (var attempt = 1; attempt <= ApplyAttempts; attempt++)
        {
            if (attempt > 1) await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            applyMode(deviceName, requested);
            actual = readMode(deviceName);
            if (actual is { } a && Matches(a, requested)) return new ApplyResult(true, actual);
        }
        return new ApplyResult(false, actual);
    }

    // Size must match exactly: a wrong size is the failure this whole sequence exists to catch.
    // Refresh is tolerated within 1 Hz (59/60 readback) and 0 means the driver does not report it.
    private static bool Matches(Mode actual, Mode requested) =>
        actual.Width == requested.Width
        && actual.Height == requested.Height
        && (actual.RefreshHz == 0 || Math.Abs(actual.RefreshHz - requested.RefreshHz) <= 1);
}

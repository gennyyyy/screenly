namespace Screenly.Core.Streaming;

// One-slot latest-frame handoff: the producer never blocks, at most one frame waits for the sender.
// Single sender per slot: TakeAsync must not be called concurrently.
public sealed class LatestFrameSlot
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private byte[]? _pending;

    // True while an unsent frame occupies the slot (producer uses this to drop before capture/encode).
    public bool HasPending
    {
        get { lock (_gate) return _pending is not null; }
    }

    // Latest wins. Returns true when an unsent frame was replaced, so the caller can request a keyframe.
    public bool Publish(byte[] frame)
    {
        bool replaced;
        lock (_gate)
        {
            replaced = _pending is not null;
            _pending = frame;
        }
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* already signalled: one wake-up covers the newest frame */ }
        return replaced;
    }

    // Awaitable for the sender task: returns the newest frame, or null after a spurious wake-up.
    public async Task<byte[]?> TakeAsync(CancellationToken ct = default)
    {
        await _signal.WaitAsync(ct);
        lock (_gate)
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }
}

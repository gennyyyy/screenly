namespace Screenly.Core.Encoding;

// Single keyframe source. OpenH264 is configured with uiIntraPeriod = 0, so it only emits the
// initial IDR; every later IDR comes from RequestKeyframe driven by this policy:
// on connect (first frame after HELLO_ACK), on REQUEST_KEYFRAME (rate-limited), and periodically.
public sealed class KeyframePolicy
{
    public const ulong RequestRateLimitUs = 500_000;

    private readonly ulong _intervalUs;
    private ulong _lastIdrUs;
    private bool _hasIdr;
    private ulong _lastRequestUs;
    private bool _hasRequest;

    public KeyframePolicy(int intervalSec) => _intervalUs = (ulong)Math.Max(1, intervalSec) * 1_000_000UL;

    // True when a periodic IDR is due; always true before the session's first IDR.
    public bool ShouldRequestIdr(ulong nowUs) => !_hasIdr || nowUs - _lastIdrUs >= _intervalUs;

    // Call once a frame actually encoded as a keyframe, whichever source asked for it.
    public void MarkIdr(ulong nowUs)
    {
        _hasIdr = true;
        _lastIdrUs = nowUs;
    }

    // One accepted REQUEST_KEYFRAME per 500 ms per session.
    public bool TryAcceptRequest(ulong nowUs)
    {
        if (_hasRequest && nowUs - _lastRequestUs < RequestRateLimitUs) return false;
        _hasRequest = true;
        _lastRequestUs = nowUs;
        return true;
    }
}

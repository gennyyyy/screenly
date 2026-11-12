namespace Screenly.Core.Encoding;

public sealed record EncodedFrame(byte[] Data, bool IsKeyframe, ulong TimestampUs);

// Media Foundation H.264 hardware encoder (CBR, no B-frames, low-latency).
// Native MFT interop required; implement against deskstream's MF encoder.
public sealed class H264Encoder
{
    public void Initialize(int width, int height, int fps, int bitrateKbps)
    {
        // TODO: MFStartup; create CLSID_CMSH264EncoderMFT; configure low-latency, CBR, MaxBFrames=0, small VBV.
    }

    public EncodedFrame? Encode(IntPtr texture, int width, int height, ulong timestampUs)
    {
        // TODO: texture -> NV12 (multi-threaded), feed MFT, drain output.
        return null;
    }

    public void RequestKeyframe()
    {
        // TODO: CODECAPI_AVEncVideoForceKeyFrame
    }

    public void Dispose() { }
}

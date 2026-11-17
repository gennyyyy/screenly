using System.Runtime.InteropServices;

namespace Screenly.Core.Encoding;

public sealed record EncodedFrame(byte[] Data, bool IsKeyframe, ulong TimestampUs);

// Media Foundation H.264 encoder (CBR, no B-frames, low-latency). Uses COM interop only.
public sealed class H264Encoder : IDisposable
{
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint dwFlags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    // H264 Encoder MFT (from mfh264enc.dll, registered under MediaFoundation\Transforms)
    private static readonly Guid CLSID_MSH264EncoderMFT = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint MF_VERSION = 0x00020070;

    private int _width, _height, _fps, _bitrateKbps;
    private IntPtr _mft = IntPtr.Zero;
    private bool _ready;
    private bool _forceKeyframe;
    public bool IsReady => _ready;
    public string? InitError { get; private set; }

    public void Initialize(int width, int height, int fps, int bitrateKbps)
    {
        _width = width; _height = height; _fps = fps; _bitrateKbps = bitrateKbps;
        try
        {
            MFStartup(MF_VERSION, 0);
            var clsid = CLSID_MSH264EncoderMFT;
            var iid = IID_IUnknown;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out _mft);
            if (hr < 0 || _mft == IntPtr.Zero) throw new Exception($"CoCreateInstance hr={hr}");
            // IMFTransform activation not yet working — MFT doesn't expose IMFTransform via IUnknown.
            // Full MF COM interop pipeline still needed.
            _ready = false;
            InitError = "IMFTransform not accessible (MFT activation not implemented)";
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            _ready = false;
        }
    }

    public EncodedFrame? Encode(byte[] bgra, int width, int height, ulong timestampUs)
    {
        // Not implemented — requires full MF COM interop pipeline.
        if (_forceKeyframe) _forceKeyframe = false;
        return null;
    }

    public void RequestKeyframe() => _forceKeyframe = true;

    public void Dispose()
    {
        if (_mft != IntPtr.Zero) { Marshal.Release(_mft); _mft = IntPtr.Zero; }
        try { MFShutdown(); } catch { }
        _ready = false;
    }
}
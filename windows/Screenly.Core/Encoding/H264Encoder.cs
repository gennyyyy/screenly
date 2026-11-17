using System.Runtime.InteropServices;
using System.Text;

namespace Screenly.Core.Encoding;

public sealed record EncodedFrame(byte[] Data, bool IsKeyframe, ulong TimestampUs);

// Media Foundation H.264 encoder (CBR, no B-frames, low-latency). Uses COM interop.
public sealed class H264Encoder : IDisposable
{
    // ---- P/Invoke helpers ----
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint dwFlags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType ppMFType);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IMFSample ppIMFSample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateAlignedMemoryBuffer(uint cbMaxLength, uint cbAlignment, out IMFMediaBuffer ppBuffer);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);
    [DllImport("ole32.dll")] private static extern int CLSIDFromString([MarshalAs(UnmanagedType.LPWStr)] string lpsz, out Guid pclsid);

    private static readonly Guid CLSID_MSH264EncoderMFT = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba53fccf9f10");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint MF_VERSION = 0x00020070;

    // Media types
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");

    // Attributes
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("165275e3-286d-4087-968e-72391d7c3e84");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("9c05e1e3-5f7a-47be-8983-ea03f3bbfd9b");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");

    // Codec API
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("38cc0c46-73d3-4437-8e36-024775983734");

    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncCommonRateControlMode_CBR = 0;

    private int _width, _height, _fps, _bitrateKbps;
    private IntPtr _mft = IntPtr.Zero;
    private IMFTransform? _transform;
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

            var iidTransform = IID_IMFTransform;
            IntPtr pTransform;
            int qiHr = Marshal.QueryInterface(_mft, ref iidTransform, out pTransform);
            if (qiHr < 0 || pTransform == IntPtr.Zero) throw new Exception($"QueryInterface hr={qiHr}");
            _transform = Marshal.GetObjectForIUnknown(pTransform) as IMFTransform;
            Marshal.Release(pTransform);
            if (_transform == null) throw new Exception("IMFTransform null");

            // Set input type (NV12)
            hr = MFCreateMediaType(out var inputType);
            if (hr < 0) throw new Exception($"MFCreateMediaType input hr={hr}");
            inputType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inputType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            inputType.SetUINT64(MF_MT_FRAME_SIZE, ((ulong)_width << 32) | (ulong)_height);
            inputType.SetUINT64(MF_MT_FRAME_RATE, ((ulong)_fps << 32) | 1);
            inputType.SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

            hr = _transform.SetInputType(0, inputType, 0);
            if (hr < 0) throw new Exception($"SetInputType hr={hr}");

            // Set output type (H.264)
            hr = MFCreateMediaType(out var outputType);
            if (hr < 0) { _ready = false; return; }
            outputType.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outputType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outputType.SetUINT64(MF_MT_FRAME_SIZE, ((ulong)_width << 32) | (ulong)_height);
            outputType.SetUINT64(MF_MT_FRAME_RATE, ((ulong)_fps << 32) | 1);
            outputType.SetUINT32(MF_MT_AVG_BITRATE, (uint)(_bitrateKbps * 1000));

            hr = _transform.SetOutputType(0, outputType, 0);
            if (hr < 0) throw new Exception($"SetOutputType hr={hr}");

            // Set codec API attributes via IMFAttributes on the transform
            var attrs = _transform as IMFAttributes;
            if (attrs != null)
            {
                attrs.SetUINT32(CODECAPI_AVLowLatencyMode, 1);
                attrs.SetUINT32(CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_CBR);
            }

            // Begin streaming
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);

            _ready = true;
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            _ready = false;
        }
    }

    public EncodedFrame? Encode(byte[] bgra, int width, int height, ulong timestampUs)
    {
        if (!_ready || _transform == null || bgra is null || bgra.Length < width * height * 4) return null;

        try
        {
            // Convert BGRA -> NV12
            var nv12 = BgraToNv12(bgra, width, height);

            // Create input buffer + sample
            int hr = MFCreateMemoryBuffer((uint)nv12.Length, out var inBuffer);
            if (hr < 0) return null;

            IntPtr ptr;
            int maxLen, curLen;
            hr = inBuffer.Lock(out ptr, out maxLen, out curLen);
            if (hr < 0) { Marshal.ReleaseComObject(inBuffer); return null; }
            Marshal.Copy(nv12, 0, ptr, nv12.Length);
            inBuffer.Unlock();
            inBuffer.SetCurrentLength((uint)nv12.Length);

            hr = MFCreateSample(out var inSample);
            if (hr < 0) { Marshal.ReleaseComObject(inBuffer); return null; }
            inSample.AddBuffer(inBuffer);
            inSample.SetSampleTime((long)timestampUs * 10); // 100ns units
            inSample.SetSampleDuration((long)(10000000.0 / _fps));

            // Force keyframe if requested
            if (_forceKeyframe)
            {
                var attrs = _transform as IMFAttributes;
                attrs?.SetUINT32(CODECAPI_AVEncVideoForceKeyFrame, 1);
                _forceKeyframe = false;
            }

            // Process input
            hr = _transform.ProcessInput(0, inSample, 0);
            Marshal.ReleaseComObject(inSample);
            Marshal.ReleaseComObject(inBuffer);

            if (hr < 0 && hr != -2147024891) // ignore MF_E_NOTACCEPTING (already has input)
            {
                // MF_E_NOTACCEPTING is OK
            }

            // Drain output
            var outputData = new List<byte>();
            bool isKeyframe = false;
            bool hasOutput = true;
            while (hasOutput)
            {
                // Get output buffer size
                var outputInfo = new MFT_OUTPUT_DATA_BUFFER();
                outputInfo.dwStreamID = 0;
                outputInfo.pSample = IntPtr.Zero;
                outputInfo.dwStatus = 0;
                outputInfo.pEvents = IntPtr.Zero;

                // Create output sample
                hr = MFCreateSample(out var outSample);
                if (hr < 0) break;
                hr = MFCreateMemoryBuffer((uint)(width * height * 2), out var outBuffer);
                if (hr < 0) { Marshal.ReleaseComObject(outSample); break; }
                outSample.AddBuffer(outBuffer);

                outputInfo.pSample = Marshal.GetIUnknownForObject(outSample);

                uint status;
                hr = _transform.ProcessOutput(0, 1, ref outputInfo, out status);

                if (hr == -2147483643) // MF_E_TRANSFORM_NEED_MORE_INPUT
                {
                    hasOutput = false;
                    Marshal.ReleaseComObject(outSample);
                    Marshal.ReleaseComObject(outBuffer);
                    break;
                }
                if (hr < 0)
                {
                    Marshal.ReleaseComObject(outSample);
                    Marshal.ReleaseComObject(outBuffer);
                    break;
                }

                // Extract data from output buffer
                IntPtr outPtr;
                int outMaxLen, outCurLen;
                hr = outBuffer.Lock(out outPtr, out outMaxLen, out outCurLen);
                if (hr >= 0 && outCurLen > 0)
                {
                    var data = new byte[outCurLen];
                    Marshal.Copy(outPtr, data, 0, outCurLen);
                    outputData.AddRange(data);
                    outBuffer.Unlock();
                }

                // Check if keyframe (from sample attributes or flags)
                // H.264 IDR: check if output data starts with 0x00 0x00 0x00 0x01 0x65 or 0x67
                if (outputData.Count > 0 && outputData[0] == 0x00 && outputData.Count > 4)
                {
                    byte nalType = (byte)(outputData[4] & 0x1F);
                    if (nalType == 5 || nalType == 7) isKeyframe = true; // IDR or SPS
                }

                Marshal.ReleaseComObject(outSample);
                Marshal.ReleaseComObject(outBuffer);
            }

            if (outputData.Count == 0) return null;

            return new EncodedFrame(outputData.ToArray(), isKeyframe, timestampUs);
        }
        catch
        {
            return null;
        }
    }

    public void RequestKeyframe() => _forceKeyframe = true;

    public void Dispose()
    {
        if (_transform != null) { Marshal.ReleaseComObject(_transform); _transform = null; }
        if (_mft != IntPtr.Zero) { Marshal.Release(_mft); _mft = IntPtr.Zero; }
        try { MFShutdown(); } catch { }
        _ready = false;
    }

    // ---- BGRA -> NV12 conversion ----
    private static byte[] BgraToNv12(byte[] bgra, int width, int height)
    {
        int ySize = width * height;
        int uvSize = (width / 2) * (height / 2) * 2;
        var nv12 = new byte[ySize + uvSize];

        // Y plane
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 4;
                byte b = bgra[idx];
                byte g = bgra[idx + 1];
                byte r = bgra[idx + 2];
                // Y = 0.299*R + 0.587*G + 0.114*B
                int yVal = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                if (yVal < 0) yVal = 0;
                if (yVal > 255) yVal = 255;
                nv12[y * width + x] = (byte)yVal;
            }
        }

        // UV plane (interleaved, subsampled 2x2)
        int uvOffset = ySize;
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                int idx = (y * width + x) * 4;
                byte b = bgra[idx];
                byte g = bgra[idx + 1];
                byte r = bgra[idx + 2];
                // U = -0.169*R - 0.331*G + 0.5*B + 128
                // V = 0.5*R - 0.419*G - 0.081*B + 128
                int u = (int)(-0.169 * r - 0.331 * g + 0.5 * b + 128);
                int v = (int)(0.5 * r - 0.419 * g - 0.081 * b + 128);
                if (u < 0) u = 0; if (u > 255) u = 255;
                if (v < 0) v = 0; if (v > 255) v = 255;
                int uvIdx = uvOffset + (y / 2) * width + x; // width is full width, UV is half
                // Actually UV plane is (width/2)*2 per row = width bytes per row
                uvIdx = uvOffset + (y / 2) * width + x;
                if (uvIdx + 1 < nv12.Length)
                {
                    nv12[uvIdx] = (byte)u;
                    nv12[uvIdx + 1] = (byte)v;
                }
            }
        }

        return nv12;
    }
}

// ---- COM interfaces ----

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba53fccf9f10"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFTransform
{
    // We only need a subset; define all methods to keep vtable correct.
    // Actually, we need SetInputType, SetOutputType, ProcessInput, ProcessOutput, ProcessMessage.
    // But COM interop requires all methods in vtable order. Let's define them all.
    int GetStreamLimits(out int pdwInputMinimum, out int pdwInputMaximum, out int pdwOutputMinimum, out int pdwOutputMaximum);
    int GetStreamCount(out int pcInputStreams, out int pcOutputStreams);
    int GetStreamIDs(int dwInputIDArraySize, [Out] int[]? pdwInputIDs, int dwOutputIDArraySize, [Out] int[]? pdwOutputIDs);
    int GetInputStreamInfo(int dwInputStreamID, out MFT_INPUT_STREAM_INFO pStreamInfo);
    int GetOutputStreamInfo(int dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo);
    int GetInputAvailableType(int dwInputStreamID, int dwTypeIndex, out IMFMediaType ppType);
    int GetOutputAvailableType(int dwOutputStreamID, int dwTypeIndex, out IMFMediaType ppType);
    int SetInputType(int dwInputStreamID, IMFMediaType pType, int dwFlags);
    int SetOutputType(int dwOutputStreamID, IMFMediaType pType, int dwFlags);
    int GetInputCurrentType(int dwInputStreamID, out IMFMediaType ppType);
    int GetOutputCurrentType(int dwOutputStreamID, out IMFMediaType ppType);
    int GetInputStatus(int dwInputStreamID, out int pdwFlags);
    int GetOutputStatus(out int pdwFlags);
    int SetOutputBounds(long hnsLower, long hnsUpper);
    int GetOutputBounds(out long phnsLower, out long phnsUpper);
    int ProcessEvent(int dwInputStreamID, [MarshalAs(UnmanagedType.IUnknown)] object pEvent);
    int ProcessMessage(MFT_MESSAGE_TYPE eMessage, IntPtr ulParam);
    int ProcessInput(int dwInputStreamID, IMFSample pSample, int dwFlags);
    int ProcessOutput(int dwFlags, int cOutputBufferCount, ref MFT_OUTPUT_DATA_BUFFER pOutputSample, out uint pdwStatus);
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaType : IMFAttributes
{
    // IMFMediaType methods (after IMFAttributes vtable)
    int GetMajorType(out Guid pguidMajorType);
    int GetSubType(out Guid pguidSubtype);
    int GetSampleDuration(out long phnsSampleDuration);
    int GetPresentationDuration(out long phnsPresentationDuration);
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFAttributes
{
    int GetItem(Guid guidKey, IntPtr pValue);
    int GetItemType(Guid guidKey, out int pType);
    int Compare(IMFAttributes pTheirs, int MatchType, out int? pbResult);
    int CompareItem(Guid guidKey, IntPtr Value, out int pbResult);
    int GetUINT32(Guid guidKey, out uint punValue);
    int GetUINT64(Guid guidKey, out ulong punValue);
    int GetDouble(Guid guidKey, out double pfValue);
    int GetGUID(Guid guidKey, out Guid pguidValue);
    int GetStringLength(Guid guidKey, out int pcchLength);
    int GetString(Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder? pwszValue, int cchBufSize, out int pcchLength);
    int GetAllocatedString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string? ppwszValue, out int pcchLength);
    int GetBlobSize(Guid guidKey, out int pcbBlobSize);
    int GetBlob(Guid guidKey, [Out] byte[]? pBuf, int cbBufSize, out int pcbBlobSize);
    int GetAllocatedBlob(Guid guidKey, out IntPtr ppBuf, out int pcbSize);
    int GetUnknown(Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    int SetItem(Guid guidKey, IntPtr Value);
    int DeleteItem(Guid guidKey);
    int DeleteAllItems();
    int SetUINT32(Guid guidKey, uint unValue);
    int SetUINT64(Guid guidKey, ulong unValue);
    int SetDouble(Guid guidKey, double fValue);
    int SetGUID(Guid guidKey, ref Guid guidValue);
    int SetString(Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    int SetBlob(Guid guidKey, byte[] pBuf, int cbBufSize);
    int SetUnknown(Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    int LockStore();
    int UnlockStore();
    int GetCount(out int pcItems);
    int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
    int CopyAllItems(IMFAttributes pDest);
}

[ComImport, Guid("c40b337f-48bf-4a28-a53b-ca19f1e825b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSample : IMFAttributes
{
    int GetSampleFlags(out int pdwSampleFlags);
    int SetSampleFlags(int dwSampleFlags);
    int GetSampleTime(out long phnsSampleTime);
    int SetSampleTime(long hnsSampleTime);
    int GetSampleDuration(out long phnsSampleDuration);
    int SetSampleDuration(long hnsSampleDuration);
    int GetBufferCount(out int pdwBufferCount);
    int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
    int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    int AddBuffer(IMFMediaBuffer pBuffer);
    int RemoveBufferByIndex(int dwIndex);
    int RemoveAllBuffers();
    int GetTotalLength(out int pcbTotalLength);
    int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaBuffer
{
    int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
    int Unlock();
    int GetCurrentLength(out int pcbCurrentLength);
    int SetCurrentLength(uint cbCurrentLength);
    int GetMaxLength(out int pcbMaxLength);
}

// Extension methods for IMFAttributes convenience
public static class MfAttrExtensions
{
    public static void SetGUID(this IMFAttributes attrs, Guid key, Guid value)
    {
        attrs.SetGUID(key, ref value);
    }
    public static void SetUINT32(this IMFAttributes attrs, Guid key, uint value)
    {
        attrs.SetUINT32(key, value);
    }
    public static void SetUINT64(this IMFAttributes attrs, Guid key, ulong value)
    {
        attrs.SetUINT64(key, value);
    }
}

// MFT message types
public enum MFT_MESSAGE_TYPE
{
    MFT_MESSAGE_COMMAND_FLUSH = 0,
    MFT_MESSAGE_COMMAND_DRAIN = 1,
    MFT_MESSAGE_SET_D3D_MANAGER = 2,
    MFT_MESSAGE_DROP_SAMPLES = 3,
    MFT_MESSAGE_COMMAND_TICK = 4,
    MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000,
    MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001,
    MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002,
    MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003,
}

// MFT stream info structs
[StructLayout(LayoutKind.Sequential)]
public struct MFT_INPUT_STREAM_INFO
{
    public long hnsMaxLatency;
    public int dwFlags;
    public int cbSize;
    public int cbMaxLookahead;
    public int cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
public struct MFT_OUTPUT_STREAM_INFO
{
    public int dwFlags;
    public int cbSize;
    public int cbAlignment;
}

[StructLayout(LayoutKind.Sequential)]
public struct MFT_OUTPUT_DATA_BUFFER
{
    public int dwStreamID;
    public IntPtr pSample; // IMFSample*
    public int dwStatus;
    public IntPtr pEvents; // IMFCollection*
}
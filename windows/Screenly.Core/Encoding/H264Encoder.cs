using System.Diagnostics;
using System.Runtime.InteropServices;
using H264Sharp;

namespace Screenly.Core.Encoding;

public sealed record EncodedFrame(byte[] Data, bool IsKeyframe, ulong TimestampUs);

// H.264 encoder using H264Sharp (Cisco OpenH264 wrapper).
public sealed class H264Encoder : IDisposable
{
    private H264Sharp.H264Encoder? _encoder;
    private FileStream? _dump;
    private int _width, _height, _fps, _bitrateKbps;
    private bool _ready;
    private bool _forceKeyframe;
    private double _encodeMsMin = double.MaxValue;
    private double _encodeMsTotal;
    private double _encodeMsMax;
    public bool IsReady => _ready;

    // When set, Initialize truncates/creates this file and every successful encode appends its Annex B bytes.
    public string? DumpPath { get; set; }

    // null when healthy; dump I/O failures only. Never affects encoding and never sets LastError.
    public string? DumpError { get; private set; }

    // null when healthy; full exception text of the last Initialize/Encode failure.
    public string? LastError { get; private set; }
    public long FramesEncoded { get; private set; }
    public long BytesOut { get; private set; }
    public double EncodeMsMin => FramesEncoded == 0 ? 0 : _encodeMsMin;
    public double EncodeMsAvg => FramesEncoded == 0 ? 0 : _encodeMsTotal / FramesEncoded;
    public double EncodeMsMax => _encodeMsMax;

    public void Initialize(int width, int height, int fps, int bitrateKbps)
    {
        _width = width; _height = height; _fps = fps; _bitrateKbps = bitrateKbps;
        _ready = false;
        LastError = null;
        FramesEncoded = 0;
        BytesOut = 0;
        _encodeMsMin = double.MaxValue;
        _encodeMsTotal = 0;
        _encodeMsMax = 0;
        OpenDump();
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
        {
            LastError = $"H.264 requires nonzero even dimensions; got {width}x{height}";
            return;
        }
        try
        {
            _encoder = new H264Sharp.H264Encoder();
            var param = _encoder.GetDefaultParameters();
            param.iUsageType = EUsageType.CAMERA_VIDEO_REAL_TIME;
            param.iPicWidth = width;
            param.iPicHeight = height;
            param.iTargetBitrate = bitrateKbps * 1000;
            // OpenH264 uses fMaxFrameRate as the input rate; if it differs from the layer rate it either
            // rejects init (DetermineTemporalSettings) or silently rewrites the layer rate. Keep them equal.
            param.fMaxFrameRate = fps;
            param.iTemporalLayerNum = 1;
            param.iSpatialLayerNum = 1;
            param.sSpatialLayers[0].iVideoWidth = width;
            param.sSpatialLayers[0].iVideoHeight = height;
            param.sSpatialLayers[0].fFrameRate = fps;
            param.sSpatialLayers[0].iSpatialBitrate = bitrateKbps * 1000;
            param.sSpatialLayers[0].uiProfileIdc = EProfileIdc.PRO_BASELINE;
            param.sSpatialLayers[0].uiLevelIdc = 0;
            param.iComplexityMode = ECOMPLEXITY_MODE.LOW_COMPLEXITY;
            // Single keyframe source: 0 = no periodic IDR of its own (the first frame is still IDR).
            // All later IDRs come from RequestKeyframe, driven by KeyframePolicy.
            param.uiIntraPeriod = 0;
            param.bEnableFrameSkip = false;
            int rc = _encoder.Initialize(param);
            if (rc != 0)
            {
                // OpenH264 rejects parameters without throwing; surface it instead of silently encoding nothing.
                LastError = $"H264Sharp Initialize failed (return code {rc}); OpenH264 rejected the encoder parameters";
                _encoder.Dispose();
                _encoder = null;
                _ready = false;
                return;
            }
            _ready = true;
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            _ready = false;
            _encoder = null;
        }
    }

    public EncodedFrame? Encode(byte[] bgra, int width, int height, ulong timestampUs)
    {
        if (!_ready || _encoder is null || bgra is null || bgra.Length < width * height * 4) return null;

        var sw = Stopwatch.StartNew();
        try
        {
            if (_forceKeyframe)
            {
                _encoder.ForceIntraFrame();
                _forceKeyframe = false;
            }

            var rgbIn = new RgbImage(ImageFormat.Bgra, width, height, bgra);
            if (!_encoder.Encode(rgbIn, out var encodedFrames) || encodedFrames is null || encodedFrames.Length == 0)
            {
                LastError = "H264Sharp Encode produced no output (OpenH264 encode failed)";
                return null;
            }

            var data = encodedFrames.GetAllBytes();
            if (data is null || data.Length == 0)
            {
                LastError = "H264Sharp Encode returned empty NAL data";
                return null;
            }

            if (_dump != null)
            {
                try
                {
                    _dump.Write(data, 0, data.Length);
                    _dump.Flush();
                }
                catch (Exception ex)
                {
                    // Diagnostics only: never let a dump failure break or fail encoding.
                    DumpError = ex.Message;
                    _dump.Dispose();
                    _dump = null;
                }
            }

            bool isKeyframe = false;
            foreach (var f in encodedFrames)
            {
                if (f.FrameType == FrameType.IDR || f.FrameType == FrameType.I)
                {
                    isKeyframe = true;
                    break;
                }
            }

            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            FramesEncoded++;
            BytesOut += data.Length;
            if (ms < _encodeMsMin) _encodeMsMin = ms;
            if (ms > _encodeMsMax) _encodeMsMax = ms;
            _encodeMsTotal += ms;
            LastError = null;
            return new EncodedFrame(data, isKeyframe, timestampUs);
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            return null;
        }
    }

    public void RequestKeyframe() => _forceKeyframe = true;

    private void OpenDump()
    {
        _dump?.Dispose();
        _dump = null;
        DumpError = null;
        if (string.IsNullOrWhiteSpace(DumpPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(DumpPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _dump = new FileStream(DumpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex)
        {
            DumpError = ex.Message;
        }
    }

    public void Dispose()
    {
        _dump?.Dispose();
        _dump = null;
        _encoder?.Dispose();
        _encoder = null;
        _ready = false;
    }
}

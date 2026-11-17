using System.Runtime.InteropServices;
using H264Sharp;

namespace Screenly.Core.Encoding;

public sealed record EncodedFrame(byte[] Data, bool IsKeyframe, ulong TimestampUs);

// H.264 encoder using H264Sharp (Cisco OpenH264 wrapper).
public sealed class H264Encoder : IDisposable
{
    private H264Sharp.H264Encoder? _encoder;
    private int _width, _height, _fps, _bitrateKbps;
    private bool _ready;
    private bool _forceKeyframe;
    public bool IsReady => _ready;

    public void Initialize(int width, int height, int fps, int bitrateKbps)
    {
        _width = width; _height = height; _fps = fps; _bitrateKbps = bitrateKbps;
        try
        {
            _encoder = new H264Sharp.H264Encoder();
            var param = _encoder.GetDefaultParameters();
            param.iUsageType = EUsageType.CAMERA_VIDEO_REAL_TIME;
            param.iPicWidth = width;
            param.iPicHeight = height;
            param.iTargetBitrate = bitrateKbps * 1000;
            param.iTemporalLayerNum = 1;
            param.iSpatialLayerNum = 1;
            param.sSpatialLayers[0].iVideoWidth = width;
            param.sSpatialLayers[0].iVideoHeight = height;
            param.sSpatialLayers[0].fFrameRate = fps;
            param.sSpatialLayers[0].iSpatialBitrate = bitrateKbps * 1000;
            param.sSpatialLayers[0].uiProfileIdc = EProfileIdc.PRO_BASELINE;
            param.sSpatialLayers[0].uiLevelIdc = 0;
            param.iComplexityMode = ECOMPLEXITY_MODE.LOW_COMPLEXITY;
            param.uiIntraPeriod = 300;
            param.bEnableFrameSkip = false;
            _encoder.Initialize(param);
            _ready = true;
        }
        catch
        {
            _ready = false;
            _encoder = null;
        }
    }

    public EncodedFrame? Encode(byte[] bgra, int width, int height, ulong timestampUs)
    {
        if (!_ready || _encoder is null || bgra is null || bgra.Length < width * height * 4) return null;

        try
        {
            if (_forceKeyframe)
            {
                _encoder.ForceIntraFrame();
                _forceKeyframe = false;
            }

            var rgbIn = new RgbImage(ImageFormat.Bgra, width, height, bgra);
            if (!_encoder.Encode(rgbIn, out var encodedFrames) || encodedFrames is null || encodedFrames.Length == 0)
                return null;

            var data = encodedFrames.GetAllBytes();
            if (data is null || data.Length == 0) return null;

            bool isKeyframe = false;
            foreach (var f in encodedFrames)
            {
                if (f.FrameType == FrameType.IDR || f.FrameType == FrameType.I)
                {
                    isKeyframe = true;
                    break;
                }
            }

            return new EncodedFrame(data, isKeyframe, timestampUs);
        }
        catch
        {
            return null;
        }
    }

    public void RequestKeyframe() => _forceKeyframe = true;

    public void Dispose()
    {
        _encoder?.Dispose();
        _encoder = null;
        _ready = false;
    }
}
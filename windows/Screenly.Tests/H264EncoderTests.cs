using Screenly.Core.Encoding;
using Xunit;

namespace Screenly.Tests;

public class H264EncoderTests
{
    [Fact]
    public void Encode_FiveFrames_FirstAccessUnitHasSpsPpsAndIdr()
    {
        using var encoder = new H264Encoder();
        encoder.Initialize(320, 240, 30, 20000);
        Assert.True(encoder.IsReady, $"encoder not ready: {encoder.LastError}");

        var frames = new List<EncodedFrame>();
        for (int i = 0; i < 5; i++)
        {
            var bgra = SyntheticBgra(320, 240, i);
            var frame = encoder.Encode(bgra, 320, 240, (ulong)i * 33_333);
            if (frame != null) frames.Add(frame);
        }

        Assert.Equal(5, frames.Count);
        Assert.True(frames[0].IsKeyframe, "the first encoded frame must be a keyframe");

        var nalTypes = NalUnitTypes(frames[0].Data);
        Assert.Contains(7, nalTypes); // SPS
        Assert.Contains(8, nalTypes); // PPS
        Assert.Contains(5, nalTypes); // IDR slice
    }

    [Fact]
    public void ForcedIdr_ContainsSpsPpsInBand()
    {
        using var encoder = new H264Encoder();
        encoder.Initialize(320, 240, 30, 20000);
        Assert.True(encoder.IsReady, $"encoder not ready: {encoder.LastError}");

        // Prime with two P frames so the forced IDR is not the stream's first access unit.
        Assert.NotNull(encoder.Encode(SyntheticBgra(320, 240, 0), 320, 240, 0));
        Assert.NotNull(encoder.Encode(SyntheticBgra(320, 240, 1), 320, 240, 33_333));

        for (int i = 0; i < 2; i++)
        {
            encoder.RequestKeyframe();
            ulong ts = (ulong)(i + 2) * 33_333;
            var idr = encoder.Encode(SyntheticBgra(320, 240, i + 2), 320, 240, ts);
            Assert.NotNull(idr);
            Assert.True(idr!.IsKeyframe, "RequestKeyframe must produce an IDR");

            var nalTypes = NalUnitTypes(idr.Data);
            Assert.Contains(7, nalTypes); // SPS
            Assert.Contains(8, nalTypes); // PPS
            Assert.Contains(5, nalTypes); // IDR slice
            Assert.True(Array.IndexOf(nalTypes.ToArray(), 7) < Array.IndexOf(nalTypes.ToArray(), 5), "SPS must precede the IDR slice");
        }
    }

    [Fact]
    public void Encoder_EmitsOnlyTheInitialIdr_NoSelfScheduledKeyframes()
    {
        using var encoder = new H264Encoder();
        encoder.Initialize(320, 240, 30, 20000);
        Assert.True(encoder.IsReady, $"encoder not ready: {encoder.LastError}");

        var bgra = SyntheticBgra(320, 240, 0);
        var keyframes = new List<int>();
        // Covers the old uiIntraPeriod = 300: a self-scheduled IDR would show up here.
        for (int i = 0; i < 310; i++)
        {
            bgra[0] = (byte)i; // keep frames distinct
            var frame = encoder.Encode(bgra, 320, 240, (ulong)i * 33_333);
            Assert.NotNull(frame);
            if (frame!.IsKeyframe) keyframes.Add(i);
        }

        // Single keyframe source: only the first frame is IDR, every later IDR comes from RequestKeyframe.
        Assert.Equal(new[] { 0 }, keyframes);
    }

    [Fact]
    public void Initialize_OddWidth_NotReadyWithEvenDimensionError()
    {
        using var encoder = new H264Encoder();
        encoder.Initialize(321, 240, 30, 20000);

        Assert.False(encoder.IsReady);
        Assert.NotNull(encoder.LastError);
        Assert.Contains("even", encoder.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DumpPath_CreatesFileAndAppendsAnnexBOutput()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), $"screenly-dump-{Guid.NewGuid():N}.h264");
        try
        {
            using var encoder = new H264Encoder { DumpPath = dumpPath };
            encoder.Initialize(320, 240, 30, 20000);
            Assert.True(encoder.IsReady, $"encoder not ready: {encoder.LastError}");

            long expected = 0;
            for (int i = 0; i < 2; i++)
            {
                var frame = encoder.Encode(SyntheticBgra(320, 240, i), 320, 240, (ulong)i * 33_333);
                Assert.NotNull(frame);
                expected += frame!.Data.Length;
            }

            Assert.Null(encoder.DumpError);
            Assert.Equal(expected, new FileInfo(dumpPath).Length);
        }
        finally
        {
            if (File.Exists(dumpPath)) File.Delete(dumpPath);
        }
    }

    [Fact]
    public void DumpPath_Unwritable_DoesNotBreakEncodingOrSetLastError()
    {
        using var encoder = new H264Encoder { DumpPath = Path.GetTempPath() }; // a directory, not a file
        encoder.Initialize(320, 240, 30, 20000);

        Assert.True(encoder.IsReady, $"encoder not ready: {encoder.LastError}");
        Assert.NotNull(encoder.DumpError);
        Assert.Null(encoder.LastError);
        Assert.NotNull(encoder.Encode(SyntheticBgra(320, 240, 0), 320, 240, 0));
    }

    // BGRA, top-down, stride = width * 4, with a moving block so every frame differs.
    private static byte[] SyntheticBgra(int width, int height, int index)
    {
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bgra[o + 0] = (byte)(x + index * 8);           // B
                bgra[o + 1] = (byte)(y + index * 4);           // G
                bgra[o + 2] = (byte)((x ^ y) + index * 2);     // R
                bgra[o + 3] = 255;                             // A
            }
        }
        return bgra;
    }

    private static List<int> NalUnitTypes(byte[] annexB)
    {
        var types = new List<int>();
        for (int i = 0; i + 3 < annexB.Length; i++)
        {
            // Accept both 3-byte (00 00 01) and 4-byte (00 00 00 01) start codes.
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                types.Add(annexB[i + 3] & 0x1F);
                i += 2;
            }
        }
        return types;
    }
}

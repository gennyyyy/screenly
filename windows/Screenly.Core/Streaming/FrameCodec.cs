using System.Buffers.Binary;

namespace Screenly.Core.Streaming;

public readonly record struct VideoFrameHeader(uint Seq, ulong TimestampUs, ushort Width, ushort Height, bool IsKeyframe);

public static class FrameCodec
{
    public const int HeaderSize = 4 + 8 + 2 + 2 + 1; // 17

    public static byte[] Encode(VideoFrameHeader h, ReadOnlySpan<byte> nalData)
    {
        var buf = new byte[HeaderSize + nalData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), h.Seq);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(4), h.TimestampUs);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), h.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14), h.Height);
        buf[16] = h.IsKeyframe ? (byte)1 : (byte)0;
        nalData.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    public static (VideoFrameHeader header, byte[] nalData) Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new ArgumentException("frame too short");
        var h = new VideoFrameHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14)),
            data[16] == 1);
        var nal = data.Slice(HeaderSize).ToArray();
        return (h, nal);
    }
}

namespace Screenly.Core.Streaming;

public enum MessageType : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    VideoFrame = 0x03,
    InputTouch = 0x04,
    InputKey = 0x05,
    Config = 0x06,
    Ping = 0x07,
    Pong = 0x08,
    Disconnect = 0x09,
    Error = 0x0A,
    CursorPos = 0x0B,
    CursorShape = 0x0C,
}

public static class Protocol
{
    public const int MaxMessageSize = 4 * 1024 * 1024; // 4 MiB
    public const int HeaderSize = 5; // 4 length + 1 type

    public static async Task WriteMessageAsync(Stream stream, MessageType type, byte[] payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxMessageSize) throw new ArgumentException("payload too large");
        var header = new byte[HeaderSize];
        var len = payload.Length + 1; // type included in length? We frame: [4 len of (type+payload)] [type] [payload]
        // Actually: [4 bytes payload-length (excluding type?)] — define: length = 1 (type) + payload.Length
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), (uint)(1 + payload.Length));
        header[4] = (byte)type;
        await stream.WriteAsync(header, ct);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<(MessageType type, byte[] payload)> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, ct);
        var len = BitConverter.ToUInt32(header, 0);
        if (len < 1 || len > MaxMessageSize) throw new IOException("invalid message length");
        var rawType = header[4];
        if (!Enum.IsDefined(typeof(MessageType), rawType)) throw new IOException("unknown message type");
        var type = (MessageType)rawType;
        var payload = new byte[len - 1];
        if (payload.Length > 0) await ReadExactAsync(stream, payload, ct);
        return (type, payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new IOException("unexpected EOF");
            read += n;
        }
    }
}

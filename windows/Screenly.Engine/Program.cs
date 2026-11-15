using System.Text;
using System.Text.Json;
using Screenly.Core.Capture;
using Screenly.Core.Config;
using Screenly.Core.Discovery;
using Screenly.Core.Display;
using Screenly.Core.Streaming;

namespace Screenly.Engine;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var cfg = ConfigStore.Load();
        using var display = new DisplayManager(new ParsecVddDisplay());
        // Use GDI capture (primary screen) for now; Parsec VDD virtual display when wired.
        var capture = new GdiCapture(cfg.Encoding.BitrateKbps > 0 ? 70 : 70);
        capture.Start(0);

        using var mdns = new MdnsBroadcaster(cfg.Port);
        mdns.Start();

        var server = new StreamServer(cfg.Port);
        server.ClientConnected += s => Console.WriteLine($"Client connected: {s.Id}");
        server.ClientDisconnected += s => Console.WriteLine($"Client disconnected: {s.Id}");
        server.MessageHandler = async (session, type, payload) =>
        {
            switch (type)
            {
                case MessageType.Hello:
                    Console.WriteLine("HELLO received");
                    var ack = new { status = "ok", sessionId = session.Id.ToString(), pairingToken = (string?)null, config = new { width = cfg.VirtualDisplay.Width, height = cfg.VirtualDisplay.Height, fps = cfg.VirtualDisplay.RefreshHz, bitrateKbps = cfg.Encoding.BitrateKbps } };
                    var ackJson = JsonSerializer.Serialize(ack);
                    await session.SendAsync(MessageType.HelloAck, Encoding.UTF8.GetBytes(ackJson));
                    break;
                case MessageType.Ping:
                    await session.SendAsync(MessageType.Pong, Array.Empty<byte>());
                    break;
                case MessageType.Config:
                    Console.WriteLine("CONFIG received");
                    await session.SendAsync(MessageType.Config, payload);
                    break;
                case MessageType.InputTouch:
                case MessageType.InputKey:
                    break;
                case MessageType.Disconnect:
                    Console.WriteLine("Client disconnect requested");
                    break;
            }
        };
        server.Start();
        Console.WriteLine($"listening on port {cfg.Port}");

        // Capture + stream loop (~15 fps)
        uint seq = 0;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (capture.TryGetFrame(out var frame))
                {
                    var header = new VideoFrameHeader(seq++, frame.TimestampUs, (ushort)frame.Width, (ushort)frame.Height, true, true);
                    var msg = FrameCodec.Encode(header, frame.JpegData);
                    foreach (var s in server.Sessions)
                    {
                        try { await s.SendAsync(MessageType.VideoFrame, msg); }
                        catch { }
                    }
                    if (seq % 100 == 0) Console.WriteLine($"frames sent: {seq}");
                }
                await Task.Delay(66, cts.Token); // ~15 fps
            }
        }, cts.Token);

        try { await loop; } catch (OperationCanceledException) { }

        capture.Stop();
        capture.Dispose();
        await display.RemoveAsync();
        await server.DisposeAsync();
    }

}

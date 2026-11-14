using System.Text.Json;
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
        await display.CreateAsync(cfg.VirtualDisplay.Width, cfg.VirtualDisplay.Height, cfg.VirtualDisplay.RefreshHz);

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
                    var ackJson = System.Text.Json.JsonSerializer.Serialize(ack);
                    await session.SendAsync(MessageType.HelloAck, System.Text.Encoding.UTF8.GetBytes(ackJson));
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
                    // TODO: forward to InputInjector (needs scaling + SendInput)
                    break;
                case MessageType.Disconnect:
                    Console.WriteLine("Client disconnect requested");
                    break;
            }
        };
        server.Start();

        using var mdns = new MdnsBroadcaster(cfg.Port);
        mdns.Start();
        Console.WriteLine("mDNS advertising _screenly._tcp");

        // Capture/encode loop is wired once DxgiCapture + H264Encoder are implemented.
        // For now, stream server accepts clients and handles control messages.
        Console.WriteLine($"Screenly engine listening on port {cfg.Port}. Press Ctrl+C to stop.");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;

        await display.RemoveAsync();
        await server.DisposeAsync();
    }
}

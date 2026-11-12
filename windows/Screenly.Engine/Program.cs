using System.Text.Json;
using Screenly.Core.Config;
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
        server.Start();

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

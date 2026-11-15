using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Screenly.Core.Capture;
using Screenly.Core.Config;
// USB-only: no mDNS discovery
using Screenly.Core.Display;
using Screenly.Core.Streaming;

namespace Screenly.Engine;

public static class Program
{
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeArrayElements, IntPtr modeArray, uint flags);
    private const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    private const uint SDC_APPLY = 0x00000080;
    public static async Task Main(string[] args)
    {
        var cfg = ConfigStore.Load();
        using var display = new DisplayManager(new ParsecVddDisplay());
        DisplayHandle vddHandle;
        try
        {
            vddHandle = await display.CreateAsync(cfg.VirtualDisplay.Width, cfg.VirtualDisplay.Height, cfg.VirtualDisplay.RefreshHz);
            Console.WriteLine($"Virtual display created (handle {vddHandle.Id})");
            // Give Windows a moment to register the new monitor
            await Task.Delay(1500);
            // Force extend topology so the virtual display is NOT a mirror of the primary
            try { SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Parsec VDD not available ({ex.Message}); falling back to primary screen (mirror mode).");
            vddHandle = default;
        }
        var capture = new GdiCapture(70);
        capture.Start(0);
        Console.WriteLine($"Capturing display at origin ({capture.MonitorX}, {capture.MonitorY}), size {capture.Width}x{capture.Height}");


        var server = new StreamServer(cfg.Port);
        server.ClientConnected += s => Console.WriteLine($"Client connected: {s.Id}");
        server.ClientDisconnected += s => Console.WriteLine($"Client disconnected: {s.Id}");
        server.MessageHandler = async (session, type, payload) =>
        {
            switch (type)
            {
                case MessageType.Hello:
                    Console.WriteLine("HELLO received");
                    session.HasHandshaked = true;
                    var ack = new { status = "ok", sessionId = session.Id.ToString(), pairingToken = (string?)null, config = new { width = cfg.VirtualDisplay.Width, height = cfg.VirtualDisplay.Height, fps = cfg.VirtualDisplay.RefreshHz, bitrateKbps = cfg.Encoding.BitrateKbps } };
                    var ackJson = JsonSerializer.Serialize(ack);
                    await session.SendAsync(MessageType.HelloAck, Encoding.UTF8.GetBytes(ackJson));
                    Console.WriteLine("HELLO_ACK sent");
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
        // Auto-set up USB reverse tunnel if adb is available (so Android connects over USB with no manual step)
        try
        {
            var adb = FindAdb();
            if (adb != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(adb, "reverse tcp:7543 tcp:7543") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);
                var err = proc?.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine($"adb: {err.Trim()}");
                else Console.WriteLine("adb reverse tcp:7543 tcp:7543 configured");
            }
        }
        catch { }

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
                    foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
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

    static string? FindAdb()
    {
        // Check PATH
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("where", "adb") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null) { p.WaitForExit(3000); var line = p.StandardOutput.ReadLine(); if (!string.IsNullOrWhiteSpace(line) && System.IO.File.Exists(line.Trim())) return line.Trim(); }
        }
        catch { }
        // Common SDK location
        var common = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe");
        if (System.IO.File.Exists(common)) return common;
        return null;
    }

        capture.Stop();
        capture.Dispose();
        await display.RemoveAsync();
        await server.DisposeAsync();
    }

}

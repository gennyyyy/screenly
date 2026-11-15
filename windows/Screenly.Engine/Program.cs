using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Screenly.Core.Capture;
using Screenly.Core.Config;
// USB-only: no mDNS discovery
using Screenly.Core.Display;
using Screenly.Core.Streaming;

namespace Screenly.Engine;

// P/Invoke for display resolution change
public static class DisplayHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmOrientation;
        public int dmPaperSize;
        public int dmPaperLength;
        public int dmPaperWidth;
        public int dmScale;
        public int dmCopies;
        public int dmDefaultSource;
        public int dmPrintQuality;
        public int dmColor;
        public int dmDuplex;
        public int dmYResolution;
        public int dmTTOption;
        public int dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    public const uint DM_PELSWIDTH = 0x00080000;
    public const uint DM_PELSHEIGHT = 0x00100000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_TEST = 0x00000002;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
}

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
            // Set resolution to 1280x720 (VDD default is 1920x1080)
            try
            {
                var devName = GdiCapture.FindVirtualDisplayDevicePublic();
                if (devName != null)
                {
                    var dm = new DisplayHelper.DEVMODE();
                    dm.dmSize = (ushort)Marshal.SizeOf<DisplayHelper.DEVMODE>();
                    dm.dmPelsWidth = (uint)cfg.VirtualDisplay.Width;
                    dm.dmPelsHeight = (uint)cfg.VirtualDisplay.Height;
                    dm.dmDisplayFrequency = 60;
                    dm.dmFields = DisplayHelper.DM_PELSWIDTH | DisplayHelper.DM_PELSHEIGHT | DisplayHelper.DM_DISPLAYFREQUENCY;
                    var res = DisplayHelper.ChangeDisplaySettingsEx(devName, ref dm, IntPtr.Zero, DisplayHelper.CDS_UPDATEREGISTRY, IntPtr.Zero);
                    Console.WriteLine($"SetMode {cfg.VirtualDisplay.Width}x{cfg.VirtualDisplay.Height}: {(res == 0 ? "OK" : $"err={res}")}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"SetMode failed: {ex.Message}"); }
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
                await Task.Delay(33, cts.Token); // ~30 fps
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

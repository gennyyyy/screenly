using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Screenly.Core.Capture;
using Screenly.Core.Config;
using Screenly.Core.Encoding;
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
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, IntPtr pathArray, ref uint numModeInfoArrayElements, IntPtr modeInfoArray, IntPtr currentTopologyId);

    public const uint DM_PELSWIDTH = 0x00080000;
    public const uint DM_PELSHEIGHT = 0x00100000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_TEST = 0x00000002;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
}

public static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }
    private const int CURSOR_SHOWING = 0x00000001;
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeArrayElements, IntPtr modeArray, uint flags);
    private const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    private const uint SDC_APPLY = 0x00000080;
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int awareness);

    public static async Task Main(string[] args)
    {
        // Ensure DPI awareness before any display APIs
        try { SetProcessDpiAwareness(2); } catch { try { SetProcessDPIAware(); } catch { } }
        var cfg = ConfigStore.Load();
        using var display = new DisplayManager(new ParsecVddDisplay());
        DisplayHandle vddHandle;
        try
        {
            vddHandle = await display.CreateAsync(cfg.VirtualDisplay.Width, cfg.VirtualDisplay.Height, cfg.VirtualDisplay.RefreshHz);
            Console.WriteLine($"Virtual display created (handle {vddHandle.Id})");
            // Give Windows a moment to register the new monitor and render to it
            await Task.Delay(5000);
            // Try to set resolution to 1920x1080
            try
            {
                var devName = GdiCapture.FindVirtualDisplayDevicePublic();
                if (devName != null)
                {
                    var mode = new DisplayHelper.DEVMODE();
                    mode.dmSize = (short)Marshal.SizeOf<DisplayHelper.DEVMODE>();
                    if (DisplayHelper.EnumDisplaySettings(devName, -1, ref mode))
                    {
                        mode.dmPelsWidth = cfg.VirtualDisplay.Width;
                        mode.dmPelsHeight = cfg.VirtualDisplay.Height;
                        mode.dmDisplayFrequency = cfg.VirtualDisplay.RefreshHz;
                        mode.dmFields = 0x80000 | 0x100000 | 0x400000;
                        int rc = DisplayHelper.ChangeDisplaySettingsEx(devName, ref mode, IntPtr.Zero, 0x1, IntPtr.Zero);
                        await Task.Delay(3000);
                    }
                    else
                    {
                        Console.WriteLine("EnumDisplaySettings failed");
                    }
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
        var requestedCodec = (cfg.Encoding.Codec ?? "jpeg").ToLowerInvariant();
        ICapture capture;
        H264Encoder? encoder = null;
        bool useH264 = requestedCodec == "h264";
        if (useH264)
        {
            var dc = new DxgiCapture(70);
            H264Encoder? enc = null;
            try
            {
                dc.Start(0);
                enc = new H264Encoder();
                enc.Initialize(cfg.VirtualDisplay.Width > 0 ? cfg.VirtualDisplay.Width : dc.Width,
                    cfg.VirtualDisplay.Height > 0 ? cfg.VirtualDisplay.Height : dc.Height,
                    cfg.Encoding.TargetFps, cfg.Encoding.BitrateKbps);
                if (!enc.IsReady) throw new Exception("H264 encoder init failed");
                encoder = enc;
                capture = dc;
                Console.WriteLine($"H264 mode: DXGI capture {dc.Width}x{dc.Height}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"H264 init failed ({ex.Message}); falling back to JPEG.");
                dc.Dispose();
                enc?.Dispose();
                encoder = null;
                useH264 = false;
                var gc = new GdiCapture(70);
                gc.Start(0);
                capture = gc;
            }
        }
        else
        {
            var gc = new GdiCapture(70);
            gc.Start(0);
            capture = gc;
        }
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
                    var activeCodecNow = useH264 ? "h264" : "jpeg";
                    var ack = new { status = "ok", sessionId = session.Id.ToString(), pairingToken = (string?)null, config = new { width = capture.Width, height = capture.Height, fps = cfg.Encoding.TargetFps, bitrateKbps = cfg.Encoding.BitrateKbps, codec = activeCodecNow } };
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
        // Cursor tracking loop (~30 Hz) - sends cursor position separately
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                    if (GetCursorInfo(ref ci))
                    {
                        int cx = ci.ptScreenPos.x - capture.MonitorX;
                        int cy = ci.ptScreenPos.y - capture.MonitorY;
                        bool visible = (ci.flags & CURSOR_SHOWING) != 0 && cx >= 0 && cy >= 0 && cx < capture.Width && cy < capture.Height;
                        var payload = new byte[17];
                        BitConverter.TryWriteBytes(payload.AsSpan(0), cx);
                        BitConverter.TryWriteBytes(payload.AsSpan(4), cy);
                        payload[8] = visible ? (byte)1 : (byte)0;
                        BitConverter.TryWriteBytes(payload.AsSpan(9), (ulong)DateTime.UtcNow.Ticks);
                        foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
                        {
                            try { await s.SendAsync(MessageType.CursorPos, payload, cts.Token); }
                            catch { }
                        }
                    }
                }
                catch { }
                await Task.Delay(33, cts.Token);
            }
        }, cts.Token);
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (useH264 && encoder != null)
                {
                    if (capture.TryGetFrame(out var rawFrame))
                    {
                        if (seq % (uint)(cfg.Encoding.TargetFps * Math.Max(1, cfg.Encoding.KeyframeIntervalSec)) == 0) encoder.RequestKeyframe();
                        var enc = encoder.Encode(rawFrame.JpegData, rawFrame.Width, rawFrame.Height, rawFrame.TimestampUs);
                        if (enc != null)
                        {
                            var header = new VideoFrameHeader(seq++, enc.TimestampUs, (ushort)rawFrame.Width, (ushort)rawFrame.Height, enc.IsKeyframe, false);
                            var msg = FrameCodec.Encode(header, enc.Data);
                            foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
                            {
                                try { await s.SendAsync(MessageType.VideoFrame, msg); }
                                catch { }
                            }
                            if (seq % 100 == 0) Console.WriteLine($"frames sent: {seq}");
                        }
                    }
                }
                else
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
                }
                // Deadline-based pacing: account for processing time
                // (placeholder - full implementation needs Stopwatch tracking)
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
        encoder?.Dispose();
        await display.RemoveAsync();
        await server.DisposeAsync();
    }

}

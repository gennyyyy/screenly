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

    public static string EngineLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Screenly", "engine.log");

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try
        {
            var dir = Path.GetDirectoryName(EngineLogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(EngineLogPath, line + Environment.NewLine);
        }
        catch { }
        try { Console.WriteLine(line); } catch { }
    }

    // Helpers required by plan (delegating to ConfigStore)
    public static Dictionary<string, (int w, int h)> LoadPersistedResolutions(string? path = null)
        => ConfigStore.LoadPersistedResolutions(path);

    public static void SavePersistedResolutions(Dictionary<string, (int w, int h)> resolutions, string? path = null)
        => ConfigStore.SavePersistedResolutions(resolutions, path);

    public static (int Width, int Height) CapResolution(int clientWidth, int clientHeight, int maxWidth, int maxHeight)
        => ConfigStore.CapResolution(clientWidth, clientHeight, maxWidth, maxHeight);

    public static (int Width, int Height) ApplyResolutionCap(int clientWidth, int clientHeight, int maxWidth, int maxHeight)
        => ConfigStore.ApplyResolutionCap(clientWidth, clientHeight, maxWidth, maxHeight);

    public static (int Width, int Height) ScaleToCap(int clientWidth, int clientHeight, int maxWidth, int maxHeight)
        => ConfigStore.ScaleToCap(clientWidth, clientHeight, maxWidth, maxHeight);

    public static async Task Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(false, "Global\\ScreenlyEngine", out bool createdNew);
        if (!createdNew)
        {
            Log("Another Screenly.Engine instance is already running; exiting.");
            return;
        }

        // Ensure DPI awareness before any display APIs
        try { SetProcessDpiAwareness(2); } catch { try { SetProcessDPIAware(); } catch { } }
        var cfg = ConfigStore.Load();
        using var display = new DisplayManager(new ParsecVddDisplay());

        DisplayHandle vddHandle = default;
        bool displayCreated = false;
        bool recreating = false;
        var displayLock = new SemaphoreSlim(1, 1);
        ICapture? capture = null;
        byte[]? currentShapePayload = null;
        H264Encoder? encoder = null;
        bool useH264 = (cfg.Encoding.Codec ?? "jpeg").Equals("h264", StringComparison.OrdinalIgnoreCase);
        string? lastDeviceId = null;
        string requestedCodec = (cfg.Encoding.Codec ?? "jpeg").ToLowerInvariant();

        async Task CreateCaptureAsync(int width, int height)
        {
            try { capture?.Stop(); } catch { }
            capture?.Dispose();
            capture = null;
            encoder?.Dispose();
            encoder = null;
            await Task.CompletedTask;

            if (requestedCodec == "h264")
            {
                var dc = new DxgiCapture(70);
                H264Encoder? enc = null;
                try
                {
                    dc.Start(0);
                    enc = new H264Encoder();
                    enc.Initialize(dc.Width, dc.Height,
                        cfg.Encoding.TargetFps, cfg.Encoding.BitrateKbps);
                    if (!enc.IsReady) throw new Exception("H264 encoder init failed");
                    encoder = enc;
                    useH264 = true;
                    capture = dc;
                    Log($"H264 mode: DXGI capture {dc.Width}x{dc.Height}");
                }
                catch (Exception ex)
                {
                    Log($"H264 init failed ({ex.Message}); falling back to JPEG.");
                    dc.Dispose();
                    enc?.Dispose();
                    encoder = null;
                    useH264 = false;
                    var gc = new GdiCapture(30);
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
            // capture stays as-is (fallback)
        }

        async Task CreateDisplayAt(int width, int height)
        {
            bool created = false;
            try
            {
                vddHandle = await display.CreateAsync(width, height, cfg.VirtualDisplay.RefreshHz);
                Log($"CreateAsync result: success, handle {vddHandle.Id} ({width}x{height})");
                created = true;
                var devName = GdiCapture.FindVirtualDisplayDevicePublic();
                if (devName != null)
                {
                    try
                    {
                        var cdm = new DisplayHelper.DEVMODE();
                        cdm.dmSize = (short)Marshal.SizeOf<DisplayHelper.DEVMODE>();
                        if (DisplayHelper.EnumDisplaySettings(devName, -1, ref cdm))
                        {
                            cdm.dmPelsWidth = width;
                            cdm.dmPelsHeight = height;
                            cdm.dmDisplayFrequency = cfg.VirtualDisplay.RefreshHz;
                            cdm.dmFields = (int)(DisplayHelper.DM_PELSWIDTH | DisplayHelper.DM_PELSHEIGHT | DisplayHelper.DM_DISPLAYFREQUENCY);
                            int rc = DisplayHelper.ChangeDisplaySettingsEx(devName, ref cdm, IntPtr.Zero, DisplayHelper.CDS_UPDATEREGISTRY, IntPtr.Zero);
                            Log($"ChangeDisplaySettingsEx: rc={rc}");
                        }
                    }
                    catch (Exception ex) { Log($"ChangeDisplaySettingsEx failed: {ex.Message}"); }
                }
                else
                {
                    Log("FindVirtualDisplayDevice returned null");
                }
                try { SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY); } catch { }
            }
            catch (Exception ex)
            {
                Log($"CreateAsync failed ({ex.Message}); falling back to primary capture (no VDD retry).");
                vddHandle = default;
                created = false;
            }
            if (created)
            {
                await CreateCaptureAsync(width, height);
            }
            else
            {
                Log("CreateDisplayAt: no virtual display; NO capture (testing).");
                capture = null;
                displayCreated = false;
            }
        }

        async Task RecreateTo(int width, int height, string? deviceId)
        {
            recreating = true;
            await Task.Delay(100);
            try
            {
                await display.RemoveAsync();
                Log("RecreateTo: RemoveAsync result: success");
            }
            catch (Exception ex)
            {
                Log($"RecreateTo: RemoveAsync result: FAILED ({ex.Message}); proceeding to create (orphan accepted)");
            }
            vddHandle = default;
            await Task.Delay(500);
            await CreateDisplayAt(width, height);
            displayCreated = true;
            Log($"RecreateTo: display count = {display.DisplayCount}");
            if (deviceId != null)
            {
                var map = LoadPersistedResolutions();
                map[deviceId] = (width, height);
                SavePersistedResolutions(map);
            }
            recreating = false;
        }

        async Task EnsureDisplayForClient(string deviceId, int clientW, int clientH)
        {
            await displayLock.WaitAsync();
            try
            {
            Log($"EnsureDisplayForClient: deviceId={deviceId} clientW={clientW} clientH={clientH} displayCreated={displayCreated}");
            var dyn = cfg.VirtualDisplay.DynamicResolution;
            int capW = cfg.VirtualDisplay.MaxWidth;
            int capH = cfg.VirtualDisplay.MaxHeight;

            if (!displayCreated)
            {
                var map = LoadPersistedResolutions();
                int targetW, targetH;
                if (clientW > 0 && clientH > 0)
                {
                    (targetW, targetH) = CapResolution(clientW, clientH, capW, capH);
                    Log($"EnsureDisplayForClient: first-connect creating directly at capped client res {targetW}x{targetH}");
                }
                else if (map.TryGetValue(deviceId, out var persisted))
                {
                    targetW = persisted.w;
                    targetH = persisted.h;
                }
                else
                {
                    targetW = cfg.VirtualDisplay.Width;
                    targetH = cfg.VirtualDisplay.Height;
                }
                await CreateDisplayAt(targetW, targetH);
                if (capture != null) displayCreated = true;
                map[deviceId] = (targetW, targetH);
                SavePersistedResolutions(map);
                return;
            }

            // Keep the current display for all clients (no auto-recreate on device switch).
            // New devices letterbox; resolution changes only via explicit CONFIG.
            if (capture == null) { await EnsureDisplayForClient(deviceId, clientW, clientH); return; }
            }
            finally { displayLock.Release(); }
        }

        var server = new StreamServer(cfg.Port);
        server.ClientConnected += s => Log($"Client connected: {s.Id}");
        server.ClientDisconnected += s => Log($"Client disconnected: {s.Id}");
        server.MessageHandler = async (session, type, payload) =>
        {
            try
            {
            switch (type)
            {
                case MessageType.Hello:
                    Log("HELLO received");
                    string deviceId = "unknown";
                    int clientW = 0, clientH = 0;
                    try
                    {
                        if (payload.Length > 0)
                        {
                            using var doc = JsonDocument.Parse(payload);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("deviceId", out var d) && d.ValueKind == JsonValueKind.String)
                                deviceId = d.GetString() ?? deviceId;
                            if (root.TryGetProperty("screenWidth", out var sw)) clientW = sw.GetInt32();
                            else if (root.TryGetProperty("width", out var w)) clientW = w.GetInt32();
                            if (root.TryGetProperty("screenHeight", out var sh)) clientH = sh.GetInt32();
                            else if (root.TryGetProperty("height", out var h)) clientH = h.GetInt32();
                        }
                    }
                    catch { }
                    session.HasHandshaked = true;
                    lastDeviceId = deviceId;
                    Log($"HELLO parsed: deviceId={deviceId} clientW={clientW} clientH={clientH}");
                    await EnsureDisplayForClient(deviceId, clientW, clientH);

                    var activeCodecNow = useH264 ? "h264" : "jpeg";
                    var cw = capture?.Width ?? cfg.VirtualDisplay.Width;
                    var ch = capture?.Height ?? cfg.VirtualDisplay.Height;
                    var ack = new { status = "ok", sessionId = session.Id.ToString(), pairingToken = (string?)null, config = new { width = cw, height = ch, fps = cfg.Encoding.TargetFps, bitrateKbps = cfg.Encoding.BitrateKbps, codec = activeCodecNow } };
                    var ackJson = JsonSerializer.Serialize(ack);
                    await session.SendAsync(MessageType.HelloAck, Encoding.UTF8.GetBytes(ackJson));
                    Log("HELLO_ACK sent");
                    if (currentShapePayload != null)
                    {
                        try { await session.SendAsync(MessageType.CursorShape, currentShapePayload); } catch { }
                    }
                    try
                    {
                        var ci2 = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                        byte[] pp;
                        if (GetCursorInfo(ref ci2))
                        {
                            var cap2 = capture;
                            int mx2 = cap2?.MonitorX ?? 0;
                            int my2 = cap2?.MonitorY ?? 0;
                            int w2 = cap2?.Width ?? 0;
                            int h2 = cap2?.Height ?? 0;
                            int rx2 = ci2.ptScreenPos.x - mx2;
                            int ry2 = ci2.ptScreenPos.y - my2;
                            int x2 = rx2 < 0 ? 0 : (w2 > 0 && rx2 > w2 ? w2 : rx2);
                            int y2 = ry2 < 0 ? 0 : (h2 > 0 && ry2 > h2 ? h2 : ry2);
                            bool vis2 = (ci2.flags & CURSOR_SHOWING) != 0 && (w2 <= 0 || h2 <= 0 || (rx2 >= 0 && rx2 <= w2 && ry2 >= 0 && ry2 <= h2));
                            pp = new byte[17];
                            BitConverter.TryWriteBytes(pp.AsSpan(0, 4), x2);
                            BitConverter.TryWriteBytes(pp.AsSpan(4, 4), y2);
                            pp[8] = (byte)(vis2 ? 1 : 0);
                            BitConverter.TryWriteBytes(pp.AsSpan(9, 8), (ulong)DateTime.UtcNow.Ticks);
                        }
                        else
                        {
                            pp = new byte[17];
                        }
                        await session.SendAsync(MessageType.CursorPos, pp);
                    }
                    catch { }
                    break;
                case MessageType.Ping:
                    await session.SendAsync(MessageType.Pong, Array.Empty<byte>());
                    break;
                case MessageType.Config:
                    Log("CONFIG received");
                    int rw = 0, rh = 0;
                    try
                    {
                        if (payload.Length > 0)
                        {
                            using var doc = JsonDocument.Parse(payload);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("width", out var w)) rw = w.GetInt32();
                            else if (root.TryGetProperty("screenWidth", out var sw)) rw = sw.GetInt32();
                            if (root.TryGetProperty("height", out var h)) rh = h.GetInt32();
                            else if (root.TryGetProperty("screenHeight", out var sh)) rh = sh.GetInt32();
                        }
                    }
                    catch { }
                    if (rw > 0 && rh > 0 && displayCreated)
                    {
                        int tw, th;
                        if (cfg.VirtualDisplay.DynamicResolution)
                            (tw, th) = CapResolution(rw, rh, cfg.VirtualDisplay.MaxWidth, cfg.VirtualDisplay.MaxHeight);
                        else { tw = cfg.VirtualDisplay.Width; th = cfg.VirtualDisplay.Height; }
                        if (capture == null || capture.Width != tw || capture.Height != th)
                        {
                            await RecreateTo(tw, th, lastDeviceId);
                        }
                    }
                    await session.SendAsync(MessageType.Config, payload);
                    break;
                case MessageType.InputTouch:
                case MessageType.InputKey:
                    break;
                case MessageType.Disconnect:
                    Log("Client disconnect requested");
                    break;
            }
            }
            catch (Exception ex) { Log($"MessageHandler error: {ex}"); }
        };
        server.Start();
        Log($"listening on port {cfg.Port}");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        // Auto-set up USB reverse tunnel for the entire engine lifetime (never give up)
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var adb = FindAdb();
                if (adb == null)
                {
                    Log("ADB not found; retrying in 5s");
                    try { await Task.Delay(5000, cts.Token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(adb, $"reverse tcp:7543 tcp:{cfg.Port}")
                    {
                        CreateNoWindow = true, UseShellExecute = false,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc == null)
                    {
                        Log("ADB reverse failed to start");
                        try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    bool exited = proc.WaitForExit(15000);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        Log("ADB reverse timed out");
                        try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    if (proc.ExitCode != 0)
                    {
                        Log($"ADB reverse failed (exit {proc.ExitCode}): {stderr.Trim()}");
                        try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    Log($"ADB reverse configured: device:7543 -> host:{cfg.Port}");
                    // Success - re-verify periodically
                    await Task.Delay(10000, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"ADB error: {ex.Message}");
                    try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { break; }
                }
            }
        }, cts.Token);

        // Capture + stream loop (~30 fps)
        uint seq = 0;
        uint frameCounter = 0;
        int lastConfigW = 0, lastConfigH = 0;
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (recreating)
                {
                    try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                var cap = capture;
                if (cap == null)
                {
                    try { await Task.Delay(100, cts.Token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                // Throttled dimension refresh (~every 30 frames)
                if (frameCounter % 30 == 0)
                {
                    bool changed = false;
                    try { changed = cap.RefreshDimensions(); } catch { }
                    if (changed)
                    {
                        Log($"dimension change: {cap.Width}x{cap.Height}");
                        // H264 path: reinit encoder; fall back to JPEG on failure
                        if (useH264 && encoder != null)
                        {
                            try
                            {
                                encoder.Dispose();
                                var newEnc = new H264Encoder();
                                newEnc.Initialize(cap.Width, cap.Height, cfg.Encoding.TargetFps, cfg.Encoding.BitrateKbps);
                                if (!newEnc.IsReady) throw new Exception("H264 reinit failed");
                                encoder = newEnc;
                            }
                            catch (Exception ex)
                            {
                                Log($"H264 reinit failed ({ex.Message}); falling back to JPEG.");
                                // ponytail: full capture reinit not wired; switch to GDI JPEG capture
                                encoder?.Dispose();
                                encoder = null;
                                useH264 = false;
                                try { capture?.Stop(); } catch { }
                                capture?.Dispose();
                                var gc = new GdiCapture(30);
                                gc.Start(0);
                                capture = gc;
                                cap = gc;
                            }
                        }
                        var activeCodecNow = useH264 ? "h264" : "jpeg";
                        var cw = cap?.Width ?? cfg.VirtualDisplay.Width;
                        var ch = cap?.Height ?? cfg.VirtualDisplay.Height;
                        if (cw != lastConfigW || ch != lastConfigH)
                        {
                            lastConfigW = cw; lastConfigH = ch;
                            var configJson = JsonSerializer.Serialize(new { width = cw, height = ch, fps = cfg.Encoding.TargetFps, bitrateKbps = cfg.Encoding.BitrateKbps, codec = activeCodecNow });
                            var configBytes = Encoding.UTF8.GetBytes(configJson);
                            foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
                            {
                                try { await s.SendAsync(MessageType.Config, configBytes); } catch { }
                            }
                        }
                    }
                }
                frameCounter++;
                cap = capture;
                if (cap == null)
                {
                    try { await Task.Delay(100, cts.Token); } catch (OperationCanceledException) { break; }
                    continue;
                }
                if (useH264 && encoder != null)
                {
                    if (cap.TryGetFrame(out var rawFrame))
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
                            if (seq % 100 == 0) Log($"frames sent: {seq}");
                        }
                    }
                }
                else
                {
                    if (cap.TryGetFrame(out var frame))
                    {
                        var header = new VideoFrameHeader(seq++, frame.TimestampUs, (ushort)frame.Width, (ushort)frame.Height, true, true);
                        var msg = FrameCodec.Encode(header, frame.JpegData);
                        foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
                        {
                            try { await s.SendAsync(MessageType.VideoFrame, msg); }
                            catch { }
                        }
                        if (seq % 100 == 0) Log($"frames sent: {seq}");
                    }
                }
                try { await Task.Delay(33, cts.Token); } catch (OperationCanceledException) { break; }
            }
        }, cts.Token);

        // Cursor sender loop (~60 Hz): streams CursorPos and broadcasts CursorShape on handle change.
        IntPtr lastCursorHandle = IntPtr.Zero;
        var cursorLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                    if (GetCursorInfo(ref ci))
                    {
                        var cap = capture;
                        int mx = cap?.MonitorX ?? 0;
                        int my = cap?.MonitorY ?? 0;
                        int w = cap?.Width ?? 0;
                        int h = cap?.Height ?? 0;
                        int rx = ci.ptScreenPos.x - mx;
                        int ry = ci.ptScreenPos.y - my;
                        bool inBounds = (w <= 0 || h <= 0) || (rx >= 0 && rx <= w && ry >= 0 && ry <= h);
                        int x = rx; int y = ry;
                        if (x < 0) x = 0;
                        if (y < 0) y = 0;
                        if (w > 0 && x > w) x = w;
                        if (h > 0 && y > h) y = h;
                        bool visible = (ci.flags & CURSOR_SHOWING) != 0 && inBounds;
                        var payload = new byte[17];
                        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), x);
                        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), y);
                        payload[8] = (byte)(visible ? 1 : 0);
                        BitConverter.TryWriteBytes(payload.AsSpan(9, 8), (ulong)DateTime.UtcNow.Ticks);
                        foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
                        {
                            try { await s.SendAsync(MessageType.CursorPos, payload, cts.Token); } catch { }
                        }
                        if (ci.hCursor != lastCursorHandle)
                        {
                            lastCursorHandle = ci.hCursor;
                            var shape = CursorShapeCapture.Capture();
                            if (shape.HasValue)
                            {
                                var (sw, sh, hx, hy, png) = shape.Value;
                                var sp = new byte[16 + png.Length];
                                BitConverter.TryWriteBytes(sp.AsSpan(0, 4), sw);
                                BitConverter.TryWriteBytes(sp.AsSpan(4, 4), sh);
                                BitConverter.TryWriteBytes(sp.AsSpan(8, 4), hx);
                                BitConverter.TryWriteBytes(sp.AsSpan(12, 4), hy);
                                Buffer.BlockCopy(png, 0, sp, 16, png.Length);
                                currentShapePayload = sp;
                                foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
                                {
                                    try { await s.SendAsync(MessageType.CursorShape, sp, cts.Token); } catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
                try { await Task.Delay(16, cts.Token); } catch (OperationCanceledException) { break; }
            }
        }, cts.Token);

        try { await loop; } catch (OperationCanceledException) { }
        try { await cursorLoop; } catch (OperationCanceledException) { }

        capture?.Stop();
        capture?.Dispose();
        encoder?.Dispose();
        await display.RemoveAsync();
        await server.DisposeAsync();
    }

    static string? FindAdb()
    {
        // Prefer PATH via where
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("where", "adb") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null)
            {
                p.WaitForExit(3000);
                var line = p.StandardOutput.ReadLine();
                if (!string.IsNullOrWhiteSpace(line) && System.IO.File.Exists(line.Trim()))
                {
                    var path = line.Trim();
                    Log($"ADB found via PATH: {path}");
                    return path;
                }
            }
        }
        catch { }
        // Common SDK location
        var common = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe");
        if (System.IO.File.Exists(common))
        {
            Log($"ADB found at common SDK path: {common}");
            return common;
        }
        return null;
    }
}

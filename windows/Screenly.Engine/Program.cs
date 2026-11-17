using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
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

public sealed class SessionState
{
    public DisplayManager Display { get; set; } = default!;
    public ICapture? Capture { get; set; }
    public H264Encoder? Encoder { get; set; }
    public bool UseH264 { get; set; }
    public string DeviceId { get; set; } = "unknown";
    public int Width { get; set; }
    public int Height { get; set; }
    public int LastConfigW { get; set; }
    public int LastConfigH { get; set; }
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
        using var vddDriver = new ParsecVddDisplay();

        var displayLock = new SemaphoreSlim(1, 1);
        var sessionStates = new ConcurrentDictionary<Guid, SessionState>();
        string requestedCodec = (cfg.Encoding.Codec ?? "jpeg").ToLowerInvariant();
        byte[]? currentShapePayload = null;

        async Task CreateCaptureForState(SessionState state)
        {
            try { state.Capture?.Stop(); } catch { }
            state.Capture?.Dispose();
            state.Capture = null;
            state.Encoder?.Dispose();
            state.Encoder = null;
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
                    state.Encoder = enc;
                    state.UseH264 = true;
                    state.Capture = dc;
                    Log($"H264 mode: DXGI capture {dc.Width}x{dc.Height}");
                }
                catch (Exception ex)
                {
                    Log($"H264 init failed ({ex.Message}); falling back to JPEG.");
                    dc.Dispose();
                    enc?.Dispose();
                    state.Encoder = null;
                    state.UseH264 = false;
                    var gc = new GdiCapture(30);
                    gc.Start(0);
                    state.Capture = gc;
                }
            }
            else
            {
                var gc = new GdiCapture(70);
                gc.Start(0);
                state.Capture = gc;
                state.UseH264 = false;
            }
        }

        async Task CreateDisplayForState(SessionState state, DisplayManager dm, int width, int height)
        {
            bool created = false;
            try
            {
                if (sessionStates.Count > 1) await Task.Delay(3000);
                var handle = await dm.CreateAsync(width, height, cfg.VirtualDisplay.RefreshHz);
                Log($"CreateAsync result: success, handle {handle.Id} ({width}x{height})");
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
                created = false;
            }
            if (created)
            {
                await CreateCaptureForState(state);
            }
            else
            {
                Log("CreateDisplayAt: no virtual display; falling back to primary screen capture.");
                try
                {
                    var gc = new GdiCapture(70);
                    gc.Start(0);
                    state.Capture = gc;
                    Log($"Fallback capture started: {gc.Width}x{gc.Height}");
                }
                catch (Exception ex)
                {
                    Log($"Fallback capture failed: {ex.Message}");
                    state.Capture = null;
                }
            }
        }

        async Task RecreateSessionAsync(Session session, SessionState state, int tw, int th)
        {
            try { await state.Display.RemoveAsync(); } catch (Exception ex) { Log($"Recreate: RemoveAsync FAILED ({ex.Message}); proceeding"); }
            await Task.Delay(100);
            state.Capture?.Stop();
            try { } catch { }
            state.Capture?.Dispose();
            state.Capture = null;
            state.Encoder?.Dispose();
            state.Encoder = null;
            await Task.Delay(500);
            await CreateDisplayForState(state, state.Display, tw, th);
            state.Width = state.Capture?.Width ?? tw;
            state.Height = state.Capture?.Height ?? th;
            if (state.Capture != null)
            {
                var map = LoadPersistedResolutions();
                map[state.DeviceId] = (tw, th);
                SavePersistedResolutions(map);
            }
        }

        var server = new StreamServer(cfg.Port);
        server.ClientConnected += s => Log($"Client connected: {s.Id}");
        server.ClientDisconnected += s =>
        {
            Log($"Client disconnected: {s.Id}");
            if (sessionStates.TryRemove(s.Id, out var st))
            {
                try { st.Capture?.Stop(); } catch { }
                st.Capture?.Dispose();
                st.Encoder?.Dispose();
                try { st.Display.RemoveAsync().GetAwaiter().GetResult(); } catch { }
            }
        };
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
                        Log($"HELLO parsed: deviceId={deviceId} clientW={clientW} clientH={clientH}");

                        var capW = cfg.VirtualDisplay.MaxWidth;
                        var capH = cfg.VirtualDisplay.MaxHeight;
                        int targetW, targetH;
                        if (clientW > 0 && clientH > 0)
                        {
                            (targetW, targetH) = CapResolution(clientW, clientH, capW, capH);
                            Log($"HELLO: creating at capped client res {targetW}x{targetH}");
                        }
                        else
                        {
                            var map = LoadPersistedResolutions();
                            if (map.TryGetValue(deviceId, out var persisted))
                            {
                                targetW = persisted.w;
                                targetH = persisted.h;
                            }
                            else
                            {
                                targetW = cfg.VirtualDisplay.Width;
                                targetH = cfg.VirtualDisplay.Height;
                            }
                        }

                        var state = new SessionState { DeviceId = deviceId };
                        state.Display = new DisplayManager(vddDriver);
                        await displayLock.WaitAsync();
                        try
                        {
                            await CreateDisplayForState(state, state.Display, targetW, targetH);
                            if (state.Capture != null)
                            {
                                var m = LoadPersistedResolutions();
                                m[deviceId] = (targetW, targetH);
                                SavePersistedResolutions(m);
                            }
                        }
                        finally { displayLock.Release(); }
                        state.Width = state.Capture?.Width ?? targetW;
                        state.Height = state.Capture?.Height ?? targetH;
                        sessionStates[session.Id] = state;

                        var activeCodecNow = state.UseH264 ? "h264" : "jpeg";
                        var cw = state.Capture?.Width ?? cfg.VirtualDisplay.Width;
                        var ch = state.Capture?.Height ?? cfg.VirtualDisplay.Height;
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
                                var cap2 = state.Capture;
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
                        if (sessionStates.TryGetValue(session.Id, out var st) && rw > 0 && rh > 0 && st.Capture != null)
                        {
                            int tw, th;
                            if (cfg.VirtualDisplay.DynamicResolution)
                                (tw, th) = CapResolution(rw, rh, cfg.VirtualDisplay.MaxWidth, cfg.VirtualDisplay.MaxHeight);
                            else { tw = cfg.VirtualDisplay.Width; th = cfg.VirtualDisplay.Height; }
                            if (st.Capture == null || st.Capture.Width != tw || st.Capture.Height != th)
                            {
                                await RecreateSessionAsync(session, st, tw, th);
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
                    var psiList = new System.Diagnostics.ProcessStartInfo(adb, "devices")
                    {
                        CreateNoWindow = true, UseShellExecute = false,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    using var procList = System.Diagnostics.Process.Start(psiList);
                    if (procList != null)
                    {
                        procList.WaitForExit(5000);
                        var output = procList.StandardOutput.ReadToEnd();
                        var serials = new System.Collections.Generic.List<string>();
                        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 && parts[1] == "device")
                                serials.Add(parts[0]);
                        }
                        if (serials.Count == 0)
                        {
                            Log("No ADB devices connected; retrying in 5s");
                            try { await Task.Delay(5000, cts.Token); } catch (OperationCanceledException) { break; }
                            continue;
                        }
                        foreach (var serial in serials)
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo(adb, $"-s {serial} reverse tcp:7543 tcp:{cfg.Port}")
                            {
                                CreateNoWindow = true, UseShellExecute = false,
                                RedirectStandardOutput = true, RedirectStandardError = true,
                            };
                            using var proc = System.Diagnostics.Process.Start(psi);
                            if (proc == null) continue;
                            proc.WaitForExit(5000);
                            var stderr = proc.StandardError.ReadToEnd();
                            if (proc.ExitCode != 0)
                                Log($"ADB reverse failed for {serial} (exit {proc.ExitCode}): {stderr.Trim()}");
                            else
                                Log($"ADB reverse configured: {serial} device:7543 -> host:{cfg.Port}");
                        }
                    }
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
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = sessionStates.ToArray();
                foreach (var kv in snapshot)
                {
                    var sess = server.Sessions.FirstOrDefault(s => s.Id == kv.Key);
                    if (sess == null || !sess.HasHandshaked) continue;
                    var st = kv.Value;
                    var cap = st.Capture;
                    if (cap == null) continue;
                    try
                    {

                    // Throttled dimension refresh (~every 30 frames)
                    if (frameCounter % 30 == 0)
                    {
                        bool changed = false;
                        try { changed = cap.RefreshDimensions(); } catch { }
                        if (changed)
                        {
                            Log($"dimension change: {cap.Width}x{cap.Height}");
                            if (st.UseH264 && st.Encoder != null)
                            {
                                try
                                {
                                    st.Encoder.Dispose();
                                    var newEnc = new H264Encoder();
                                    newEnc.Initialize(cap.Width, cap.Height, cfg.Encoding.TargetFps, cfg.Encoding.BitrateKbps);
                                    if (!newEnc.IsReady) throw new Exception("H264 reinit failed");
                                    st.Encoder = newEnc;
                                }
                                catch (Exception ex)
                                {
                                    Log($"H264 reinit failed ({ex.Message}); falling back to JPEG.");
                                    st.Encoder?.Dispose();
                                    st.Encoder = null;
                                    st.UseH264 = false;
                                    try { st.Capture?.Stop(); } catch { }
                                    st.Capture?.Dispose();
                                    var gc = new GdiCapture(30);
                                    gc.Start(0);
                                    st.Capture = gc;
                                    cap = gc;
                                }
                            }
                            var activeCodecNow = st.UseH264 ? "h264" : "jpeg";
                            var cw = cap?.Width ?? cfg.VirtualDisplay.Width;
                            var ch = cap?.Height ?? cfg.VirtualDisplay.Height;
                            if (cw != st.LastConfigW || ch != st.LastConfigH)
                            {
                                st.LastConfigW = cw; st.LastConfigH = ch;
                                var configJson = JsonSerializer.Serialize(new { width = cw, height = ch, fps = cfg.Encoding.TargetFps, bitrateKbps = cfg.Encoding.BitrateKbps, codec = activeCodecNow });
                                var configBytes = Encoding.UTF8.GetBytes(configJson);
                                try { await sess.SendAsync(MessageType.Config, configBytes); } catch { }
                            }
                        }
                    }

                    if (st.UseH264 && st.Encoder != null)
                    {
                        if (cap != null && cap.TryGetFrame(out var rawFrame))
                        {
                            if (seq % (uint)(cfg.Encoding.TargetFps * Math.Max(1, cfg.Encoding.KeyframeIntervalSec)) == 0) st.Encoder.RequestKeyframe();
                            var enc = st.Encoder.Encode(rawFrame.JpegData, rawFrame.Width, rawFrame.Height, rawFrame.TimestampUs);
                            if (enc != null)
                            {
                                var header = new VideoFrameHeader(seq++, enc.TimestampUs, (ushort)rawFrame.Width, (ushort)rawFrame.Height, true, false); // force keyframe for testing
                                var msg = FrameCodec.Encode(header, enc.Data);
                                try { await sess.SendAsync(MessageType.VideoFrame, msg); } catch { }
                                if (seq % 100 == 0) Log($"frames sent: {seq}");
                            }
                        }
                    }
                    else
                    {
                        if (cap != null && cap.TryGetFrame(out var frame))
                        {
                            var header = new VideoFrameHeader(seq++, frame.TimestampUs, (ushort)frame.Width, (ushort)frame.Height, true, true);
                            var msg = FrameCodec.Encode(header, frame.JpegData);
                            try { await sess.SendAsync(MessageType.VideoFrame, msg); } catch { }
                            if (seq % 100 == 0) Log($"frames sent: {seq}");
                        }
                    }
                }
                catch (Exception ex) { Log($"session {kv.Key} frame error: {ex.Message}"); }
                }
                frameCounter++;
                try { await Task.Delay(10, cts.Token); } catch (OperationCanceledException) { break; }
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
                        foreach (var kv in sessionStates.ToArray())
                        {
                            var sess = server.Sessions.FirstOrDefault(s => s.Id == kv.Key);
                            if (sess == null || !sess.HasHandshaked) continue;
                            var cap = kv.Value.Capture;
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
                            try { await sess.SendAsync(MessageType.CursorPos, payload, cts.Token); } catch { }
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

        foreach (var kv in sessionStates.ToArray())
        {
            try { kv.Value.Capture?.Stop(); } catch { }
            kv.Value.Capture?.Dispose();
            kv.Value.Encoder?.Dispose();
            try { kv.Value.Display.RemoveAsync().GetAwaiter().GetResult(); } catch { }
        }
        sessionStates.Clear();
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
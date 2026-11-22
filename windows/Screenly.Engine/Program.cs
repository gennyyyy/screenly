using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Screenly.Core;
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

    // Current mode of an enumerated display (EnumDisplaySettings index -1), for the wait/verify
    // path in VirtualDisplayMode.
    public static VirtualDisplayMode.Mode? ReadMode(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, -1, ref dm)) return null;
        return new VirtualDisplayMode.Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
    }

    // Applies one mode to one display. Returns false when Windows rejects it; the caller reads the
    // mode back and decides, so a rejection that still lands the right mode is acceptable.
    public static bool ApplyMode(string deviceName, VirtualDisplayMode.Mode mode)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(deviceName, -1, ref dm)) return false;
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.RefreshHz;
        dm.dmFields = (int)(DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY);
        var rc = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        Program.Log($"ChangeDisplaySettingsEx {deviceName} {mode}: rc={rc}");
        return rc == DISP_CHANGE_SUCCESSFUL;
    }
}

// Windows quantizes waits to the system timer (~15.6 ms by default), so Task.Delay rounds a 33 ms
// frame period up to ~46 ms and the pacer rebases every frame (pacerOverruns). 1 ms resolution keeps
// the paced loop honest; Windows 10 2004+ scopes the request to the calling process.
public sealed class TimerResolution : IDisposable
{
    private readonly uint _periodMs;
    private readonly bool _raised;

    public TimerResolution(uint periodMs)
    {
        _periodMs = periodMs;
        _raised = timeBeginPeriod(periodMs) == 0; // 0 = TIMERR_NOERROR
    }

    public bool Raised => _raised;

    // Restores the previous resolution; the compiler's finally (using) covers the shutdown path.
    public void Dispose()
    {
        if (_raised) { try { timeEndPeriod(_periodMs); } catch { } }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}

public sealed class SessionState
{
    public SessionState(int keyframeIntervalSec) => Keyframes = new KeyframePolicy(keyframeIntervalSec);

    public DisplayLease? Lease { get; set; }
    public ICapture? Capture { get; set; }
    public H264Encoder? Encoder { get; set; }
    public bool UseH264 { get; set; }
    // Per-session VIDEO_FRAME sequence (the frame loop is its only writer).
    public uint Seq { get; set; }
    // Frames actually written to the client socket: the sender is the only writer, status fps reads.
    private long _framesSent;
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public void MarkFrameSent() => Interlocked.Increment(ref _framesSent);
    // Frames lost to backpressure, split by cause (frame loop only writer):
    // skipped before capture/encode because the slot was busy, or encoded then replaced in the slot.
    public long FramesDropped { get; set; }
    public long BackpressureSkips { get; set; }
    public long SlotReplacements { get; set; }
    // Sender task's VIDEO_FRAME socket write time: sender is the only writer, status reads.
    private double _sendMsMin = double.MaxValue, _sendMsTotal, _sendMsMax;
    private long _sends;
    public double SendMsMin => _sends == 0 ? 0 : _sendMsMin;
    public double SendMsAvg => _sends == 0 ? 0 : _sendMsTotal / _sends;
    public double SendMsMax => _sendMsMax;
    public void RecordSend(double ms)
    {
        _sends++;
        if (ms < _sendMsMin) _sendMsMin = ms;
        if (ms > _sendMsMax) _sendMsMax = ms;
        _sendMsTotal += ms;
    }
    // Frame loop's ICapture.TryGetFrame time (successful frames only): frame loop is the only
    // writer, status reads. Isolates capture cost from encode/send when a frame misses its budget.
    private double _captureMsMin = double.MaxValue, _captureMsTotal, _captureMsMax;
    private long _captures;
    public double CaptureMsMin => _captures == 0 ? 0 : _captureMsMin;
    public double CaptureMsAvg => _captures == 0 ? 0 : _captureMsTotal / _captures;
    public double CaptureMsMax => _captureMsMax;
    public void RecordCapture(double ms)
    {
        _captures++;
        if (ms < _captureMsMin) _captureMsMin = ms;
        if (ms > _captureMsMax) _captureMsMax = ms;
        _captureMsTotal += ms;
    }
    // One-slot latest frame for this session's sender task; the frame loop never awaits the socket.
    public LatestFrameSlot Frames { get; } = new();
    // Sender lifecycle: started after HELLO_ACK, cancelled on disconnect, codec-change close and shutdown.
    public CancellationTokenSource? SenderCts { get; set; }
    public string DeviceId { get; set; } = "unknown";
    public int Width { get; set; }
    public int Height { get; set; }
    public int LastConfigW { get; set; }
    public int LastConfigH { get; set; }
    public int LastConfigOrientation { get; set; }
    public string? LastLoggedEncoderError { get; set; }
    public DateTime LastEncoderErrorLog { get; set; }
    // Periodic IDR schedule and REQUEST_KEYFRAME rate limit (one per 500 ms).
    public KeyframePolicy Keyframes { get; }
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

    public static string StatusFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Screenly", "status.json");

    public static void WriteStatusFile(string codec, int width, int height, int fps, string connectionType = "USB", int connectedDevices = 0, H264Encoder? encoder = null, string? encoderError = null, SessionState? state = null, long pacerOverruns = 0)
    {
        try
        {
            var dir = Path.GetDirectoryName(StatusFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            encoderError ??= encoder?.LastError;
            var payload = JsonSerializer.Serialize(new
            {
                codec, width, height, fps, connectionType, connectedDevices,
                // 0 = 0°, 1 = 90°, 2 = 180°, 3 = 270°; 0 when no session/capture (primary fallback).
                orientation = state?.Capture?.Orientation ?? DisplayOrientation.Default,
                encoderError,
                framesEncoded = encoder?.FramesEncoded ?? 0,
                framesDropped = state?.FramesDropped ?? 0,
                backpressureSkips = state?.BackpressureSkips ?? 0,
                slotReplacements = state?.SlotReplacements ?? 0,
                pacerOverruns,
                bytesOut = encoder?.BytesOut ?? 0,
                encodeMsMin = Math.Round(encoder?.EncodeMsMin ?? 0, 1),
                encodeMsAvg = Math.Round(encoder?.EncodeMsAvg ?? 0, 1),
                encodeMsMax = Math.Round(encoder?.EncodeMsMax ?? 0, 1),
                captureMsMin = Math.Round(state?.CaptureMsMin ?? 0, 1),
                captureMsAvg = Math.Round(state?.CaptureMsAvg ?? 0, 1),
                captureMsMax = Math.Round(state?.CaptureMsMax ?? 0, 1),
                sendMsAvg = Math.Round(state?.SendMsAvg ?? 0, 1),
                sendMsMax = Math.Round(state?.SendMsMax ?? 0, 1),
            });
            var tmp = StatusFilePath + ".tmp";
            File.WriteAllText(tmp, payload);
            File.Move(tmp, StatusFilePath, overwrite: true);
        }
        catch { }
    }
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

        var displayPool = cfg.VirtualDisplay.PerClientDisplays
            ? new DisplayPool(vddDriver, TimeProvider.System, cfg.VirtualDisplay.MaxDisplays, cfg.VirtualDisplay.RemoveGraceSeconds)
            : null;

        var displayLock = new SemaphoreSlim(1, 1);
        var sessionStates = new ConcurrentDictionary<Guid, SessionState>();
        // One shared virtual display reused by all sessions (no per-client VDD churn).
        DisplayManager? sharedDm = null;
        DisplayHandle? sharedHandle = null;
        string? sharedDeviceName = null;
        int sharedW = 0, sharedH = 0;
        string requestedCodec = (cfg.Encoding.Codec ?? "jpeg").ToLowerInvariant();
        // FramePacer treats <= 0 as "unset"; keep that semantic and clamp the rest to the pacer's range.
        static int ClampFps(int fps) => Math.Clamp(fps <= 0 ? FramePacer.DefaultFps : fps, FramePacer.MinFps, FramePacer.MaxFps);
        int requestedFps = ClampFps(cfg.Encoding.TargetFps);
        // Last H.264 init failure, surfaced in status.json even after the JPEG fallback.
        string? encoderInitError = null;
        var pacer = new FramePacer(requestedFps);

        byte[]? currentShapePayload = null;

        // deviceName pins the capture to the verified virtual display instead of re-discovering
        // "last Parsec device" after the mode change.
        async Task CreateCaptureForState(SessionState state, string? deviceName = null)
        {
            try { state.Capture?.Stop(); } catch { }
            state.Capture?.Dispose();
            state.Capture = null;
            state.Encoder?.Dispose();
            state.Encoder = null;
            await Task.CompletedTask;

            if (requestedCodec == "h264")
            {
                var dc = new DxgiCapture(70, deviceName);
                H264Encoder? enc = null;
                try
                {
                    dc.Start(0);
                    enc = new H264Encoder { DumpPath = cfg.Encoding.DumpPath };
                    enc.Initialize(dc.Width, dc.Height,
                        requestedFps, cfg.Encoding.BitrateKbps);
                    if (!enc.IsReady) throw new InvalidOperationException("H264 encoder init failed");
                    state.Encoder = enc;
                    state.UseH264 = true;
                    state.Capture = dc;
                    encoderInitError = null;
                    Log($"H264 mode: DXGI capture {dc.Width}x{dc.Height}");
                }
                catch (Exception ex)
                {
                    encoderInitError = enc?.LastError ?? ex.ToString();
                    Log($"H264 init failed: {encoderInitError}; falling back to JPEG.");
                    dc.Dispose();
                    enc?.Dispose();
                    state.Encoder = null;
                    state.UseH264 = false;
                    var gc = new GdiCapture(30, deviceName);
                    gc.Start(0);
                    state.Capture = gc;
                }
            }
            else
            {
                encoderInitError = null;
                var gc = new GdiCapture(70, deviceName);
                gc.Start(0);
                state.Capture = gc;
                state.UseH264 = false;
            }
        }

        // H.264 init with one retry at the requested dimensions. Returns null (and sets error) if both fail.
        H264Encoder? TryInitH264(int width, int height, out string? error)
        {
            error = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var enc = new H264Encoder { DumpPath = cfg.Encoding.DumpPath };
                try { enc.Initialize(width, height, requestedFps, cfg.Encoding.BitrateKbps); }
                catch (Exception ex) { error = ex.ToString(); }
                if (enc.IsReady) return enc;
                error = enc.LastError ?? error ?? "unknown H264 init failure";
                enc.Dispose();
                Log($"H264 init attempt {attempt}/2 failed at {width}x{height}: {error}");
            }
            return null;
        }

        // Returns false when the requested virtual display cannot be created and verified at
        // width x height. Callers must fail the session: never capture the primary/old mode instead.
        async Task<bool> CreateDisplayForState(SessionState state, int width, int height)
        {
            if (displayPool != null)
            {
                // Pool path: acquire a lease (reuses/mode-changes/grows as needed).
                var priorLease = state.Lease;
                try
                {
                    var lease = await displayPool.AcquireAsync(width, height, cfg.VirtualDisplay.RefreshHz, state.DeviceId);
                    if (lease == null)
                    {
                        if (priorLease != null) displayPool?.Release(priorLease);
                        state.Lease = null;
                        return false;
                    }
                    state.Lease = lease;
                    Log($"pool acquired lease {lease.Id} device {lease.DeviceName} at {width}x{height}");
                    await CreateCaptureForState(state, lease.DeviceName);
                    return state.Capture != null;
                }
                catch (Exception ex)
                {
                    Log($"pool acquire failed for {width}x{height}: {ex.Message}");
                    if (state.Lease != null) { displayPool?.Release(state.Lease); state.Lease = null; }
                    return false;
                }
            }

            var requested = new VirtualDisplayMode.Mode(width, height, cfg.VirtualDisplay.RefreshHz);
            string? devName = null;
            try
            {
                // Reuse the shared virtual display when the resolution already matches. The mode was
                // verified when it was created, so a reuse needs no display round-trip.
                if (sharedHandle is { } sh && sharedW == width && sharedH == height && sharedDm != null)
                {
                    // legacy shared path: no per-state display handle needed
                    devName = sharedDeviceName;
                    Log($"reusing existing VDD handle {sh.Id} at {width}x{height} ({devName ?? "device unknown"})");
                }
                else
                {
                    // Resolution changed: drop the old shared display and create a new one.
                    if (sharedDm != null)
                    {
                        try { await sharedDm.RemoveAsync(); } catch { }
                        sharedDm = null; sharedHandle = null; sharedDeviceName = null;
                    }
                    var dm = new DisplayManager(vddDriver);
                    var handle = await dm.CreateAsync(width, height, cfg.VirtualDisplay.RefreshHz);
                    Log($"CreateAsync result: success, handle {handle.Id} ({width}x{height})");

                    // ADD returns before Windows enumerates the monitor: poll instead of a single
                    // lookup, then apply and read back the mode on the enumerated device.
                    devName = await VirtualDisplayMode.WaitForDeviceAsync(GdiCapture.FindVirtualDisplayDevicePublic);
                    if (devName == null)
                    {
                        Log($"VDD attach failed: handle {handle.Id} device not enumerated within {VirtualDisplayMode.WaitTimeoutMs} ms ({requested}); removing it");
                        await RemoveQuietlyAsync(dm);
                        return false;
                    }
                    Log($"VDD device enumerated: {devName}");

                    // Extend first so the mode apply is the last display state change; the verify
                    // then describes the final mode the capture will use.
                    try { SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY); } catch { }

                    var applied = await VirtualDisplayMode.ApplyAndVerifyAsync(
                        devName, requested, DisplayHelper.ReadMode, DisplayHelper.ApplyMode);
                    if (!applied.Verified)
                    {
                        Log($"VDD mode not applied: requested {requested}, actual {Describe(applied.Actual)} after {VirtualDisplayMode.ApplyAttempts} attempts; removing it");
                        await RemoveQuietlyAsync(dm);
                        return false;
                    }
                    Log($"VDD mode verified: {applied.Actual}");

                    sharedDm = dm;
                    sharedHandle = handle;
                    sharedDeviceName = devName;
                    sharedW = width; sharedH = height;
                    // legacy: state display handle not tracked per session
                }
            }
            catch (Exception ex)
            {
                Log($"VDD setup failed for {requested}: {ex.Message}");
                return false;
            }

            await CreateCaptureForState(state, devName);
            var codec = state.UseH264 ? "h264" : "jpeg";
            var cw = state.Capture?.Width ?? width;
            var ch = state.Capture?.Height ?? height;
            WriteStatusFile(codec, cw, ch, requestedFps, "USB", 0, state.Encoder, encoderInitError, state, pacer.Overruns);
            return true;
        }

        static string Describe(VirtualDisplayMode.Mode? mode) => mode?.ToString() ?? "unreadable";

        static async Task RemoveQuietlyAsync(DisplayManager dm)
        {
            try { await dm.RemoveAsync(); } catch { }
        }

        async Task<bool> RecreateSessionAsync(Session session, SessionState state, int tw, int th)
        {
            state.Capture?.Stop();
            state.Capture?.Dispose();
            state.Capture = null;
            state.Encoder?.Dispose();
            state.Encoder = null;
            await Task.Delay(500);
            if (!await CreateDisplayForState(state, tw, th)) return false;
            state.Width = state.Capture?.Width ?? tw;
            state.Height = state.Capture?.Height ?? th;
            if (state.Capture != null)
            {
                var map = LoadPersistedResolutions();
                map[state.DeviceId] = (tw, th);
                SavePersistedResolutions(map);
            }
            return true;
        }

        // Terminal setup failure: report it and close, so the client reconnects instead of waiting
        // on a session that will never stream.
        async Task FailSessionAsync(Session session, string code, string message)
        {
            Log($"session {session.Id} failed: {code}: {message}");
            try
            {
                await session.SendAsync(MessageType.Error,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { code, message })));
            }
            catch { }
            try { session.Client.Close(); } catch { } // MonitorSessionAsync removes and disposes the state
        }

        // Per-session capture timing: successful frames only, so captureMs min/avg/max reflect the
        // real cost of pulling one frame and can be compared against the frame period.
        static bool TryCaptureTimed(ICapture capture, SessionState st, out CapturedFrame frame)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = capture.TryGetFrame(out frame);
            sw.Stop();
            if (ok) st.RecordCapture(sw.Elapsed.TotalMilliseconds);
            return ok;
        }

        // Per-session sender: drains the one-slot latest-frame holder and is the only place that
        // awaits VIDEO_FRAME socket writes, so a slow client never blocks the shared capture loop.
        void StartSender(Session session, SessionState state)
        {
            if (state.SenderCts != null) return; // duplicate HELLO: one sender per session
            var sendCts = new CancellationTokenSource();
            state.SenderCts = sendCts;
            var token = sendCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var frame = await state.Frames.TakeAsync(token);
                        if (frame == null) continue;
                        var sendSw = System.Diagnostics.Stopwatch.StartNew();
                        await session.SendAsync(MessageType.VideoFrame, frame, token);
                        sendSw.Stop();
                        state.RecordSend(sendSw.Elapsed.TotalMilliseconds);
                        state.MarkFrameSent();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log($"session {session.Id} sender stopped: {ex.Message}"); }
            }, CancellationToken.None);
        }

        void StopSender(SessionState state)
        {
            // Cancel only: a blocked socket write cannot be interrupted, it faults when the peer closes.
            try { state.SenderCts?.Cancel(); } catch { }
            state.SenderCts = null;
        }

        // Control message off the shared loop: fire-and-forget so a stalled client cannot block capture.
        async Task SendConfigAsync(Session session, byte[] payload)
        {
            try { await session.SendAsync(MessageType.Config, payload); } catch { }
        }

        // One shape for every server CONFIG: dimensions and orientation come from the session
        // capture, so an orientation-only change (0↔2) still reaches the client.
        byte[] BuildConfigPayload(ICapture? cap, bool useH264) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            width = cap?.Width ?? cfg.VirtualDisplay.Width,
            height = cap?.Height ?? cfg.VirtualDisplay.Height,
            fps = requestedFps,
            bitrateKbps = cfg.Encoding.BitrateKbps,
            codec = useH264 ? "h264" : "jpeg",
            orientation = cap?.Orientation ?? DisplayOrientation.Default,
        }));

        var server = new StreamServer(cfg.Port);
        server.ClientConnected += s => Log($"Client connected: {s.Id}");
        server.ClientDisconnected += s =>
        {
            Log($"Client disconnected: {s.Id}");
            if (sessionStates.TryRemove(s.Id, out var st))
            {
                StopSender(st);
                try { st.Capture?.Stop(); } catch { }
                st.Capture?.Dispose();
                st.Encoder?.Dispose();
                // Release the lease back to the pool (scheduled removal after grace).
                if (st.Lease != null) displayPool?.Release(st.Lease);
                st.Lease = null;
            }
            try { WriteStatusFile(requestedCodec, cfg.VirtualDisplay.Width, cfg.VirtualDisplay.Height, requestedFps, "USB", server.Sessions.Count(x => x.HasHandshaked), null, encoderInitError); } catch { }
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

                        var state = new SessionState(cfg.Encoding.KeyframeIntervalSec) { DeviceId = deviceId };
                        bool displayReady;
                        await displayLock.WaitAsync();
                        try
                        {
                            displayReady = await CreateDisplayForState(state, targetW, targetH);
                            if (displayReady)
                            {
                                var m = LoadPersistedResolutions();
                                m[deviceId] = (targetW, targetH);
                                SavePersistedResolutions(m);
                            }
                        }
                        finally { displayLock.Release(); }
                        if (!displayReady)
                        {
                            // No silent downgrade to the primary/old mode: fail the handshake.
                            var errCode = displayPool != null ? (displayPool.LastError ?? PoolErrorCodes.NoDisplay) : "DISPLAY_LOST";
                            await FailSessionAsync(session, errCode,
                                $"virtual display {targetW}x{targetH}@{cfg.VirtualDisplay.RefreshHz}Hz could not be created or verified");
                            break;
                        }
                        state.Width = state.Capture?.Width ?? targetW;
                        state.Height = state.Capture?.Height ?? targetH;
                        sessionStates[session.Id] = state; 
 
                        WriteStatusFile(state.UseH264 ? "h264" : "jpeg", state.Capture?.Width ?? cfg.VirtualDisplay.Width, state.Capture?.Height ?? cfg.VirtualDisplay.Height, requestedFps, "USB", server.Sessions.Count(s => s.HasHandshaked), state.Encoder, encoderInitError, state, pacer.Overruns);
                        var activeCodecNow = state.UseH264 ? "h264" : "jpeg";
                        var cw = state.Capture?.Width ?? cfg.VirtualDisplay.Width;
                        var ch = state.Capture?.Height ?? cfg.VirtualDisplay.Height;
                        var orientation = state.Capture?.Orientation ?? DisplayOrientation.Default;
                        // The client just saw this config: start change tracking from it so the
                        // next CONFIG is only sent for a real dimension/orientation change.
                        state.LastConfigW = cw; state.LastConfigH = ch; state.LastConfigOrientation = orientation;
                        var ack = new { status = "ok", sessionId = session.Id.ToString(), pairingToken = (string?)null, config = new { width = cw, height = ch, fps = requestedFps, bitrateKbps = cfg.Encoding.BitrateKbps, codec = activeCodecNow, orientation } };
                        var ackJson = JsonSerializer.Serialize(ack);
                        await session.SendAsync(MessageType.HelloAck, Encoding.UTF8.GetBytes(ackJson));
                        Log($"HELLO_ACK sent: {ackJson}");
                        // Force an IDR before the first frame, then open the video gate.
                        state.Encoder?.RequestKeyframe();
                        session.ReadyToStream = true;
                        StartSender(session, state);
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
                                BitConverter.TryWriteBytes(pp.AsSpan(9, 8), MonotonicClock.NowUs);
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
                        sessionStates.TryGetValue(session.Id, out var st);
                        if (st != null && rw > 0 && rh > 0 && st.Capture != null)
                        {
                            int tw, th;
                            if (cfg.VirtualDisplay.DynamicResolution)
                                (tw, th) = CapResolution(rw, rh, cfg.VirtualDisplay.MaxWidth, cfg.VirtualDisplay.MaxHeight);
                            else { tw = cfg.VirtualDisplay.Width; th = cfg.VirtualDisplay.Height; }
                            if (st.Capture == null || st.Capture.Width != tw || st.Capture.Height != th)
                            {
                                bool recreated;
                                await displayLock.WaitAsync();
                                try { recreated = await RecreateSessionAsync(session, st, tw, th); }
                                finally { displayLock.Release(); }
                                if (!recreated)
                                {
                                    // The old capture was already torn down; do not keep streaming it.
                                    var cfgErr = displayPool != null ? (displayPool.LastError ?? PoolErrorCodes.NoDisplay) : "DISPLAY_LOST";
                                    await FailSessionAsync(session, cfgErr,
                                        $"virtual display {tw}x{th}@{cfg.VirtualDisplay.RefreshHz}Hz could not be created or verified");
                                    break;
                                }
                                try { WriteStatusFile(st.UseH264 ? "h264" : "jpeg", st.Capture?.Width ?? cfg.VirtualDisplay.Width, st.Capture?.Height ?? cfg.VirtualDisplay.Height, requestedFps, "USB", server.Sessions.Count(s => s.HasHandshaked), st.Encoder, encoderInitError, st, pacer.Overruns); } catch { }
                            }
                        }
                        // Reply with the applied config (capture-sourced), so CONFIG carries the
                        // current orientation even when the client only proposed a resolution.
                        await session.SendAsync(MessageType.Config,
                            st is { Capture: { } cfgCapture } ? BuildConfigPayload(cfgCapture, st.UseH264) : payload);
                        break;
                    case MessageType.InputTouch:
                    case MessageType.InputKey:
                        break;
                    case MessageType.RequestKeyframe:
                        if (sessionStates.TryGetValue(session.Id, out var kf) && kf.Encoder is { } kfEncoder)
                        {
                            if (kf.Keyframes.TryAcceptRequest(MonotonicClock.NowUs))
                            {
                                kfEncoder.RequestKeyframe();
                                Log($"REQUEST_KEYFRAME from {session.Id}: forcing IDR");
                            }
                        }
                        break;
                    case MessageType.Disconnect:
                        Log("Client disconnect requested");
                        break;
                }
            }
            catch (Exception ex) { Log($"MessageHandler error: {ex}"); }
        };
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                server.Start();
                Log($"listening on port {cfg.Port}");
                break;
            }
            catch (Exception ex)
            {
                Log($"TcpListener.Start failed on port {cfg.Port} (attempt {attempt}/5): {ex.Message}");
                if (attempt < 5)
                {
                    try { await Task.Delay(3000); } catch { }
                }
                else
                {
                    Log("Engine exiting: unable to bind port.");
                    return;
                }
            }
        }
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Codec and target-fps changes are detected by polling instead of a FileSystemWatcher: the
        // host rewrites config.json via ConfigStore.Save (tmp + File.Replace), and the replace
        // surfaces as a directory rename that a watcher filtered to LastWrite|Size never reports.
        // A real change is terminal (api-contract): update requestedCodec/requestedFps, DISCONNECT
        // (reason=codec_change, or fps_change; codec wins when both changed in one poll), stop the
        // session sender, then close so Android reconnects.
        _ = Task.Run(async () =>
        {
            var lastCodec = requestedCodec;
            var lastFps = requestedFps;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    var codec = ConfigStore.ReadCodec();
                    var fps = ConfigStore.ReadTargetFps();
                    bool codecChanged = codec != null && codec != lastCodec; // missing/unreadable: ignore
                    bool fpsChanged = fps != null && ClampFps(fps.Value) != lastFps;
                    if (!codecChanged && !fpsChanged) continue;

                    if (codecChanged)
                    {
                        lastCodec = codec!;
                        requestedCodec = codec!;
                        Log($"config changed: encoder switched to {codec}");
                    }
                    if (fpsChanged)
                    {
                        lastFps = ClampFps(fps!.Value);
                        requestedFps = lastFps;
                        pacer = new FramePacer(requestedFps);
                        Log($"config changed: target fps switched to {requestedFps}");
                    }
                    var reason = codecChanged ? "codec_change" : "fps_change";
                    var disconnectPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { reason }));
                    foreach (var s in server.Sessions.Where(s => s.HasHandshaked))
                    {
                        try
                        {
                            await s.SendAsync(MessageType.Disconnect, disconnectPayload);
                            Log($"DISCONNECT ({reason}) sent to {s.Id}");
                        }
                        catch (Exception ex) { Log($"DISCONNECT ({reason}) failed for {s.Id}: {ex.Message}"); }
                        // Config changed: stop this session's frame sender before the socket closes.
                        if (sessionStates.TryGetValue(s.Id, out var oldState)) StopSender(oldState);
                        try { s.Client.Close(); } catch { } // MonitorSessionAsync removes it and disposes state.
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

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

        Log("after ADB task, starting frame loop");

        // Capture + stream loop, paced to requestedFps (stopwatch-based, no fixed sleep).
        // 1 ms timer resolution: restored when Main returns, including the shutdown path (using → finally).
        using var timerResolution = new TimerResolution(1);
        Log($"timer resolution: {(timerResolution.Raised ? "1 ms" : "timeBeginPeriod failed, keeping system default")}");
        uint frameCounter = 0;
        long lastFramesSent = 0;
        var lastStatusTime = DateTime.UtcNow;
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = sessionStates.ToArray();
                foreach (var kv in snapshot)
                {
                    var sess = server.Sessions.FirstOrDefault(s => s.Id == kv.Key);
                    if (sess == null || !sess.ReadyToStream) continue;
                    var st = kv.Value;
                    var cap = st.Capture;
                    if (cap == null) continue;
                    // Previous frame is still waiting for the socket: drop before capture/encode.
                    if (st.Frames.HasPending)
                    {
                        st.BackpressureSkips++;
                        st.FramesDropped++;
                        continue;
                    }
                    try
                    {

                    // Throttled dimension refresh (~every 30 frames)
                    if (frameCounter % 30 == 0)
                    {
                        // H.264 selected but no encoder (reinit failed): retry on the frameCounter cadence.
                        if (st.UseH264 && st.Encoder == null)
                        {
                            var retryEnc = TryInitH264(cap.Width, cap.Height, out var retryError);
                            if (retryEnc != null)
                            {
                                st.Encoder = retryEnc;
                                encoderInitError = null;
                                Log($"H264 init recovered at {cap.Width}x{cap.Height}");
                            }
                            else
                            {
                                encoderInitError = retryError;
                                Log($"H264 init retry failed at {cap.Width}x{cap.Height}: {retryError}");
                            }
                        }
                        bool changed = false;
                        int prevW = cap.Width, prevH = cap.Height;
                        try { changed = cap.RefreshDimensions(); } catch { }
                        if (changed)
                        {
                            bool dimsChanged = cap.Width != prevW || cap.Height != prevH;
                            Log(dimsChanged
                                ? $"dimension change: {cap.Width}x{cap.Height}"
                                : $"orientation change: {cap.Orientation} at {cap.Width}x{cap.Height}");
                            // Only a dimension change invalidates the encoder's SPS/PPS; an
                            // orientation-only change (0↔2, 1↔3) keeps encoding into the same stream.
                            if (dimsChanged && st.UseH264 && st.Encoder != null)
                            {
                                var newEnc = TryInitH264(cap.Width, cap.Height, out var reinitError);
                                if (newEnc != null)
                                {
                                    st.Encoder.Dispose();
                                    st.Encoder = newEnc;
                                    encoderInitError = null;
                                    Log($"H264 reinit ok at {cap.Width}x{cap.Height}");
                                }
                                else
                                {
                                    // Keep H.264 selected: the frame loop retries until init succeeds.
                                    st.Encoder.Dispose();
                                    st.Encoder = null;
                                    encoderInitError = reinitError;
                                    Log($"H264 reinit failed at {cap.Width}x{cap.Height}: {reinitError}; keeping H.264, will retry");
                                }
                            }
                            var cw = cap.Width;
                            var ch = cap.Height;
                            var orientation = cap.Orientation;
                            if (cw != st.LastConfigW || ch != st.LastConfigH || orientation != st.LastConfigOrientation)
                            {
                                st.LastConfigW = cw; st.LastConfigH = ch; st.LastConfigOrientation = orientation;
                                Log($"CONFIG sent: {cw}x{ch} orientation={orientation}");
                                _ = SendConfigAsync(sess, BuildConfigPayload(cap, st.UseH264));
                            }
                        }
                    }

                    if (st.UseH264 && st.Encoder != null)
                    {
                        if (cap != null && TryCaptureTimed(cap, st, out var rawFrame))
                        {
                            // Timestamp doubles as the policy's clock: one monotonic sample per frame.
                            var nowUs = rawFrame.TimestampUs;
                            if (st.Keyframes.ShouldRequestIdr(nowUs)) st.Encoder.RequestKeyframe();
                            var enc = st.Encoder.Encode(rawFrame.JpegData, rawFrame.Width, rawFrame.Height, nowUs);
                            var encError = st.Encoder.LastError
                                ?? (st.Encoder.DumpError is { } dumpErr ? $"dump: {dumpErr}" : null);
                            if (encError != null && (encError != st.LastLoggedEncoderError || (DateTime.UtcNow - st.LastEncoderErrorLog).TotalSeconds >= 5))
                            {
                                Log($"H264 encode error: {encError}");
                                st.LastLoggedEncoderError = encError;
                                st.LastEncoderErrorLog = DateTime.UtcNow;
                            }
                            if (enc != null)
                            {
                                if (enc.IsKeyframe) st.Keyframes.MarkIdr(nowUs);
                                var header = new VideoFrameHeader(st.Seq++, enc.TimestampUs, (ushort)rawFrame.Width, (ushort)rawFrame.Height, enc.IsKeyframe, false);
                                if (st.Seq == 1)
                                {
                                    var prefixLength = Math.Min(24, enc.Data.Length);
                                    Log($"H264 first output: {enc.Data.Length} bytes, keyframe={enc.IsKeyframe}, prefix={Convert.ToHexString(enc.Data.AsSpan(0, prefixLength))}");
                                }
                                var msg = FrameCodec.Encode(header, enc.Data);
                                // A replaced frame never reached the wire: the next one must be an IDR.
                                if (st.Frames.Publish(msg))
                                {
                                    st.SlotReplacements++;
                                    st.FramesDropped++;
                                    st.Encoder.RequestKeyframe();
                                }
                                if (st.Seq % 100 == 0) Log($"frames sent: {st.Seq} to {sess.Id}");
                            }
                        }
                    }
                    else if (!st.UseH264)
                    {
                        if (cap != null && TryCaptureTimed(cap, st, out var frame))
                        {
                            var header = new VideoFrameHeader(st.Seq++, frame.TimestampUs, (ushort)frame.Width, (ushort)frame.Height, true, true);
                            var msg = FrameCodec.Encode(header, frame.JpegData);
                            // Every JPEG frame is a keyframe: replacement needs no recovery.
                            if (st.Frames.Publish(msg)) { st.SlotReplacements++; st.FramesDropped++; }
                            if (st.Seq % 100 == 0) Log($"frames sent: {st.Seq} to {sess.Id}");
                        }
                    }
                }
                catch (Exception ex) { Log($"session {kv.Key} frame error: {ex.Message}"); }
                }
                frameCounter++;
                // Update status file with live FPS every ~1 second.
                // fps is measured from frames the sender actually wrote, not from encoded/queued frames.
                var now = DateTime.UtcNow;
                var statusElapsed = (now - lastStatusTime).TotalSeconds;
                if (statusElapsed >= 1.0)
                {
                    var st0 = sessionStates.Values.FirstOrDefault();
                    var sentNow = st0?.FramesSent ?? 0;
                    var sentDelta = sentNow >= lastFramesSent ? sentNow - lastFramesSent : 0; // session replaced
                    var fps = (int)Math.Round(sentDelta / statusElapsed);
                    lastFramesSent = sentNow;
                    lastStatusTime = now;
                    var codec = st0?.UseH264 == true ? "h264" : "jpeg";
                    var cw = st0?.Capture?.Width ?? cfg.VirtualDisplay.Width;
                    var ch = st0?.Capture?.Height ?? cfg.VirtualDisplay.Height;
                    try { WriteStatusFile(codec, cw, ch, fps, "USB", server.Sessions.Count(s => s.HasHandshaked), st0?.Encoder, encoderInitError, st0, pacer.Overruns); } catch { }
                    // Drop attribution once per second: skipped (slot busy before capture) vs replaced
                    // (encoded, then superseded) vs pacer overrun (frame work exceeded the period).
                    var sendMin = st0?.SendMsMin ?? 0;
                    var sendAvg = st0?.SendMsAvg ?? 0;
                    var sendMax = st0?.SendMsMax ?? 0;
                    var encMin = st0?.Encoder?.EncodeMsMin ?? 0;
                    var encAvg = st0?.Encoder?.EncodeMsAvg ?? 0;
                    var encMax = st0?.Encoder?.EncodeMsMax ?? 0;
                    var capMin = st0?.CaptureMsMin ?? 0;
                    var capAvg = st0?.CaptureMsAvg ?? 0;
                    var capMax = st0?.CaptureMsMax ?? 0;
                    Log($"stats: fps={fps} encoded={st0?.Encoder?.FramesEncoded ?? 0} skipped={st0?.BackpressureSkips ?? 0} replaced={st0?.SlotReplacements ?? 0} pacerOverruns={pacer.Overruns} sendMs min/avg/max={sendMin:F1}/{sendAvg:F1}/{sendMax:F1} encodeMs min/avg/max={encMin:F1}/{encAvg:F1}/{encMax:F1} captureMs min/avg/max={capMin:F1}/{capAvg:F1}/{capMax:F1}");
                }
                try { await Task.Delay(pacer.NextDelay(), cts.Token); } catch (OperationCanceledException) { break; }
            }
        }, cts.Token);

        // Cursor sender loop (~60 Hz): streams CursorPos and broadcasts CursorShape on handle change.
        IntPtr lastCursorHandle = IntPtr.Zero;
        int lastCursorDpi = 0;
        var cursorLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                    if (GetCursorInfo(ref ci))
                    {
                        var cursorSessions = sessionStates.ToArray();
                        // Shape is global (one desktop cursor), so any session capture names the DPI.
                        int cursorDpi = CursorScaling.BaseDpi;
                        foreach (var kv in cursorSessions)
                        {
                            var c = kv.Value.Capture;
                            if (c == null) continue;
                            cursorDpi = c.MonitorDpi;
                            break;
                        }
                        foreach (var kv in cursorSessions)
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
                            BitConverter.TryWriteBytes(payload.AsSpan(9, 8), MonotonicClock.NowUs);
                            try { await sess.SendAsync(MessageType.CursorPos, payload, cts.Token); } catch { }
                        }
                        if (ci.hCursor != lastCursorHandle || cursorDpi != lastCursorDpi)
                        {
                            lastCursorHandle = ci.hCursor;
                            lastCursorDpi = cursorDpi;
                            var shape = CursorShapeCapture.Capture(cursorDpi);
                            if (shape.HasValue)
                            {
                                var (sw, sh, hx, hy, png) = shape.Value;
                                Log($"cursor shape: {sw}x{sh} hotspot={hx},{hy} dpi={cursorDpi}");
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
            if (kv.Value.Lease != null) displayPool?.Release(kv.Value.Lease);
            StopSender(kv.Value);
            try { kv.Value.Capture?.Stop(); } catch { }
            kv.Value.Capture?.Dispose();
            kv.Value.Encoder?.Dispose();
        }
        displayPool?.Dispose();
        sessionStates.Clear();
        await server.DisposeAsync();
    }

    static string? FindAdb()
    {
        // Bundled Platform Tools shipped by the installer
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");
        if (System.IO.File.Exists(bundled))
        {
            Log($"ADB found bundled: {bundled}");
            return bundled;
        }
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

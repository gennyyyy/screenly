# Screenly — Issues and Inefficiencies

## 1. Performance

### 1.1 Low FPS (~15-18 fps observed)
- **Root cause**: GDI+ JPEG encoding at 1920×1080 takes ~25-40ms/frame. Theoretical max ~25 fps, actual ~15 fps after BitBlt + overhead.
- **Hard cap**: `Task.Delay(66)` in `Program.cs` caps the capture loop at ~15 fps regardless of encode speed.
- **No frame dropping on engine**: Every frame is sent to every session via `s.SendAsync`. If a client is slow, the send blocks (write lock) and stalls the loop for all clients.
- **BitBlt cost**: Full 1920×1080 framebuffer read (~8 MB) per frame, ~2-3 ms.
- **`Image.FromHbitmap`**: Allocates a new Bitmap each frame (GC pressure).
- **Android decode blocks receive thread**: `BitmapFactory.decodeByteArray` (~10-15 ms) runs synchronously on the receive thread. Frames arriving during decode are buffered in TCP (not dropped — they pile up, adding latency).
- **Synchronous SurfaceView draw**: `lockCanvas` → `drawBitmap` → `unlockCanvasAndPost` on the receive thread (~5-8 ms). Blocks decode of next frame.
- **No hardware acceleration**: DXGI Desktop Duplication + Media Foundation H.264 encode/decode not implemented. GDI+JPEG is the working path.
- **TCP head-of-line blocking**: On lossy WiFi, TCP HOL blocking adds jitter. Mitigated by single-slot frame channel (engine side only).
- **No compression beyond JPEG**: Frames are ~150-300 KB each at q70. At 15 fps = ~3-4 MB/s over USB. Wasteful but USB 2.0 can handle it.

### 1.2 Android rendering inefficiency
- **SurfaceView size mismatch**: SurfaceView is 1280×709 but frames are 1920×1080. Non-uniform scaling distorts aspect ratio and cursor position.
- **No `inBitmap` reuse**: `BitmapFactory.decodeByteArray` allocates a new Bitmap each frame. `inBitmap` reuse was attempted but caused SIGABRT (native crash). Reverted.
- **Frame-skip logic**: Uses `socket.getInputStream().available()` which may not accurately reflect pending data. Could skip too many or too few frames.
- **No background decode**: Decode + draw run on the receive thread. A background decode thread with `Handler` posting to SurfaceView would be smoother.

## 2. Cursor

### 2.1 Cursor composited on same layer as display
- **No independent cursor tracking**: Cursor is drawn directly onto the captured frame bitmap before JPEG encoding. Position is baked into the image.
- **Encoding/display latency**: Cursor position captured at frame time but displayed later. Causes perceived lag.
- **Scaling distortion**: Non-uniform scaling (1920→1280, 1080→709) distorts cursor position relative to content.
- **Double hotspot offset bug** (fixed): `DrawIconEx` with a cursor handle automatically uses the hotspot as origin. Code was subtracting the hotspot, causing double offset.
- **Monochrome cursor visibility**: Standard Windows cursor is monochrome (white fill is transparent). Only black outline shows. Fixed with yellow backing circle.
- **No separate cursor layer**: Cursor cannot be updated independently of frame rate. A separate cursor overlay on Android would be more responsive.

### 2.2 Recommended improvement
- Send cursor position (x, y) as separate `MessageType.CursorPos` message
- Android renders cursor as overlay on top of video frame
- Eliminates encoding latency and scaling distortion
- Remove `DrawIconEx` from GdiCapture (frames no longer need cursor)

## 3. Network

### 3.1 mDNS self-response amplification loop (fixed)
- **Root cause**: `MdnsBroadcaster` multicasted announcements containing a `_screenly` question. The same socket received them, `ContainsServiceQuery` returned true, and it responded again → infinite multicast storm.
- **Impact**: Saturated Wi-Fi, other devices couldn't connect to router.
- **Fix**: Only respond to real queries (QR bit 0), ignore self-sourced packets, QDCOUNT=0 in announcements, per-IP rate limit.
- **Secondary**: Binding to port 5353 collides with Windows DNS Client service (svchost). `ReuseAddress` allows sharing but traffic competes.

### 3.2 TCP limitations
- Single TCP connection for video + control + input. Head-of-line blocking on lossy networks.
- No UDP/WebRTC for lower latency.
- No frame dropping on engine side for slow clients (write lock serializes sends).

### 3.3 USB connection friction
- Requires manual `adb reverse tcp:7543 tcp:7543` (auto-configured by engine if adb is on PATH).
- No USB accessory mode or USB tethering support.
- adb must be installed on the PC.

## 4. Display

### 4.1 VDD display resolution
- **Default resolution 1280×720** (not 1920×1080 as expected). VDD driver's default or registry-based custom resolutions needed.
- **Custom resolutions via registry**: `HKLM\SOFTWARE\Parsec\vdd\<slot>` with `w`, `h`, `hz` values. Up to 5 slots. Must be created before display creation.
- **`ChangeDisplaySettingsEx`**: Used to set 1920×1080. Required correct `DEVMODE` struct (with `dmPositionX`, `dmDisplayOrientation`, etc.) — earlier attempt with wrong struct returned -2 (bad mode).
- **Ghost displays**: Stale VDD displays from crashed runs accumulate (up to 16). Cleanup via `vdd-index.txt` (fixed REMOVE ioctl to use 16-bit big-endian index per Parsec VDD protocol). Reboot needed for pre-existing ghosts.
- **Mirror mode fallback**: When VDD unavailable, falls back to primary screen (mirror, not extend). No automatic extend topology.

### 4.2 Display topology
- `SetDisplayConfig(SDC_TOPOLOGY_EXTEND)` called to force extend mode. May not always work (needs 5s delay + retry).
- No `SetMode` IOCTL (Parsec VDD doesn't support it). Resolution changed via Win32 Display API.

## 5. Android App

### 5.1 UI/UX
- **No manual IP input** (removed — USB-only now). No fallback if auto-connect fails.
- **No status indicators**: Connection state, FPS, latency not shown.
- **No settings screen**: Can't adjust quality, resolution, or touch sensitivity.
- **SurfaceView size mismatch**: 1280×709 vs 1920×1080 frame. Letterboxing added but still slight mismatch.

### 5.2 Lifecycle
- **Foreground service**: `ConnectionService` started on connect but may not be properly managed.
- **No background reconnection**: Auto-reconnect added but only retries every 2s. No exponential backoff.
- **Scope cancellation fixed**: `close()` no longer cancels scope (was breaking reconnection). But receiveLoop coroutines may accumulate if not properly tracked.

### 5.3 Touch input
- `TouchCapture` handles touch events but multi-touch/gesture support unclear.
- No haptic feedback or visual touch indicator.

## 6. Windows App

### 6.1 Host app (WPF)
- **Tray icon**: Added but uses `SystemIcons.Application` (generic icon). No custom icon.
- **No settings UI**: Config is JSON file only (`%APPDATA%\Screenly\config.json`).
- **Engine launched as child process**: No IPC for control. Host just starts engine and shows info.
- **No auto-start at login**: `AutoStart` config exists but not wired to registry/Task Scheduler.

### 6.2 Engine
- **No proper logging**: `Console.WriteLine` used. No log file, no log levels.
- **No error recovery**: If VDD fails, falls back to mirror mode silently.
- **No configuration validation**: Invalid config values not checked.
- **Engine runs as normal user**: VDD IOCTL requires admin for some operations. May fail silently.
- **No graceful shutdown on crash**: `Dispose()` may not be called if process is killed.

### 6.3 Installer
- **Inno Setup**: Requires admin. Installs driver silently (may need reboot).
- **No .NET runtime bundling**: Assumes .NET 8 runtime is installed.
- **No driver version check**: Doesn't verify Parsec VDD version compatibility.
- **Firewall rule**: Added via netsh but not verified.

## 7. Architecture

### 7.1 No security
- No TLS/encryption on TCP connection.
- No authentication beyond optional PIN (not implemented).
- No certificate pinning.
- LAN assumed trusted.

### 7.2 No database
- JSON config files only. No query capability. Fine at current scale.

### 7.3 No audio streaming
- Only video is streamed. PC audio stays on PC.

### 7.4 No multi-client support
- Engine supports multiple sessions but frame sending is sequential (write lock per session).
- No per-client quality/bitrate adaptation.

### 7.5 No monitoring/observability
- No metrics, no health checks, no telemetry.
- No crash reporting.

## 8. Code Quality

### 8.1 Debug code
- `Console.WriteLine` debug statements left in production code.
- Debug frame saves (removed but pattern remains).
- No conditional compilation (`#if DEBUG`).

### 8.2 Testing
- No unit tests.
- No integration tests.
- No E2E tests (manual testing only).
- Smoke tests exist (`Screenly.Tests/SmokeTests.cs`) but minimal.

### 8.3 Error handling
- Broad `catch { }` blocks swallow exceptions silently.
- No structured error types.
- No retry logic for transient failures.

### 8.4 Hardcoded values
- Port 7543 hardcoded in multiple places.
- Resolution defaults scattered across code.
- Quality 70 hardcoded in GdiCapture constructor.

## 9. Known Bugs

1. **Cursor position inaccuracy** (partially fixed): Windows side correct, Android scaling causes minor offset. Full fix requires separate cursor message.
2. **Ghost displays**: Accumulate from crashed runs. Reboot clears them.
3. **VDD resolution**: Defaults to 1280×720 despite registry keys. ChangeDisplaySettingsEx works but requires correct DEVMODE struct.
4. **Auto-reconnect scope**: `close()` was cancelling scope (fixed), but receiveLoop coroutines may still accumulate.
5. **Frame-skip logic**: `available()` may not accurately reflect pending data.
6. **SurfaceView size mismatch**: 1280×709 vs 1920×1080 frame. Letterboxing added but slight mismatch remains.

## 10. Recommended Priority

### Immediate (P0)
1. Separate cursor message (fixes cursor accuracy + improves responsiveness)
2. Rebuild installer with all fixes (done but needs verification)
3. Clean up debug code

### Short-term (P1)
4. DXGI Desktop Duplication + H.264 hardware encode (60 fps target)
5. Background decode on Android (smoother rendering)
6. Proper logging framework
7. Error recovery and retry logic

### Medium-term (P2)
8. TLS/encryption for TCP connection
9. Multi-client support with per-client quality
10. Settings UI on both platforms
11. Audio streaming
12. Auto-start at login

### Long-term (P3)
13. WebRTC for NAT traversal
14. Cross-platform (macOS, Linux)
15. Enterprise features (SSO, MDM)
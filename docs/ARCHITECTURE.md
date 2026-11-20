# Screenly — Tech Stack & How It Works

> Verified against the source on 2026-11-19. This is the current, code-accurate picture.
> Some older docs under `docs/plan/` (notably `documentation/architecture.md`) describe an earlier
> design with mDNS, Media Foundation, named-pipe IPC, and input injection that were never shipped
> or were replaced. When this document and older docs disagree, trust this document.

## What the app is

Screenly turns an Android device into an extended display for a Windows PC. The Windows engine
creates a Parsec virtual display, captures it, encodes JPEG or H.264, and streams it over a single
TCP connection. The Android app decodes and renders fullscreen, and locks its orientation to the
Windows display's orientation. Connection is **USB only**: the engine runs `adb reverse` so the
phone reaches the PC at `127.0.0.1:7543`.

Status: both sides are implemented and streaming works end to end. Touch is captured and sent by
Android but the engine ignores it (`InputInjector` is a stub). There is no audio, no LAN mode, and
no pairing/TLS.

## Tech stack

| Layer | Technology |
|---|---|
| Windows runtime | C# / .NET 8 (`net8.0`, `net8.0-windows`), WPF + WinForms tray (`Screenly.Host`), console process (`Screenly.Engine`) |
| Windows interop | P/Invoke to `setupapi`, `kernel32`, `user32`, `gdi32`, `shcore`, `Dwmapi`-style APIs as needed; no COM |
| Virtual display | Parsec VDD driver (bundled `driver/parsec-vdd/ParsecVDisplay-v0.45-setup.exe`), IOCTL-controlled |
| Capture | GDI `BitBlt` into a reused 32-bit DIB section (`DxgiCapture` keeps the name but is GDI, not DXGI Desktop Duplication) |
| Video codecs | JPEG via `System.Drawing.Common`; H.264 via H264Sharp 1.6.0 → Cisco OpenH264 2.4.1 (software, Baseline) |
| Transport | One TCP socket, custom length-prefixed binary protocol; `adb reverse tcp:7543 tcp:7543` for USB |
| Android | Kotlin 2.0.21, Jetpack Compose (BOM 2024.12.01), MediaCodec AVC → `SurfaceView`, kotlinx.coroutines 1.9.0, kotlinx.serialization 1.7.3 |
| Android SDK | minSdk 26, target/compileSdk 35, AGP 8.7.3, Java 17 |
| Config / state | JSON files under `%APPDATA%\Screenly`; no database |
| Packaging | Inno Setup (`installer/screenly.iss`) → `app/Screenly-Setup.exe`; Android APK → `app/Screenly.apk` |
| Tests | xUnit (`windows/Screenly.Tests`); no Android tests |

Dependencies worth knowing: `H264Sharp` (includes native `H264SharpNative-win64.dll` and
`openh264-2.4.1-win64.dll`), `System.Drawing.Common`. Nothing else — no networking, serialization,
logging, or DI framework on the Windows side.

## Repository map

```
screenly/
├── windows/
│   ├── Screenly.sln
│   ├── Screenly.Core/            # All reusable logic (class library)
│   │   ├── Config/               # AppConfig + ConfigStore (JSON, atomic replace)
│   │   ├── Display/              # ParsecVddDisplay (IOCTL), DisplayManager wrapper
│   │   ├── Capture/              # GdiCapture (JPEG), DxgiCapture (BGRA), cursor helpers
│   │   ├── Encoding/             # H264Encoder (OpenH264), KeyframePolicy
│   │   ├── Streaming/            # Protocol framing, Session/StreamServer, FrameCodec,
│   │   │                         #   LatestFrameSlot, FramePacer
│   │   └── Input/                # InputInjector (stub)
│   ├── Screenly.Engine/          # Console process; composition root + frame/cursor/ADB loops
│   ├── Screenly.Host/            # WPF tray app; config selectors + status display
│   └── Screenly.Tests/           # xUnit tests
├── android/
│   └── app/src/main/kotlin/com/screenly/
│       ├── MainActivity.kt       # Compose UI, connection loop, render, cursor overlay
│       ├── net/                  # Protocol.kt (DTOs/codes), ScreenlyClient.kt (socket)
│       ├── video/                # VideoDecoder (MediaCodec), DisplayGeometry (letterbox)
│       ├── input/                # TouchCapture (send only)
│       └── service/              # ConnectionService (foreground + wake lock)
├── installer/                    # Inno Setup script + bundled adb
├── driver/parsec-vdd/            # Bundled VDD installer
├── dist/win-x64/                 # self-contained publish output
└── app/                          # FINAL artifacts: Screenly-Setup.exe, Screenly.apk
```

## Runtime topology

```
Android app                      USB / adb reverse                 Windows PC
┌──────────────────┐           127.0.0.1:7543             ┌─────────────────────────────┐
│ ScreenlyClient   │◄──────── TCP (single socket) ────────►│ Screenly.Engine             │
│  control channel │                                      │  StreamServer (loopback)    │
│  frame channel   │                                      │  Session → sender task      │
│ VideoDecoder     │◄──── VIDEO_FRAME / CONFIG / CURSOR ──│  capture → encode loop      │
│ SurfaceView      │                                      │  cursor loop (~60 Hz)       │
│ TouchCapture     │──── INPUT_TOUCH (ignored today) ────►│  InputInjector (stub)       │
└──────────────────┘                                      │  Parsec VDD ─► virtual mon │
                                                          │  Host (tray) ─► status.json │
                                                          └─────────────────────────────┘
```

There is no mDNS, no LAN listening, and no IPC named pipe. The Host and Engine communicate only
through `config.json` (Host writes, Engine polls) and `status.json` (Engine writes, Host polls).

## Windows: processes and startup

### Screenly.Host (WPF tray)

- On startup: loads `config.json`, kills a stale engine only if the port is not listening, starts
  `Screenly.Engine.exe` from its own directory, then starts a 5 s watchdog.
- Watchdog: if the engine process is alive **and** `127.0.0.1:<port>` accepts a connection, do
  nothing; otherwise kill all `Screenly.Engine` processes and start one.
- Window shows encoder, resolution/FPS, connection type and device count by reading `status.json`
  every 1 s. It also reads H.264 `encoderError`, `framesEncoded`, `bytesOut`, `encodeMsAvg`.
- Codec (JPEG/H.264) and FPS (30/60) selectors write `config.json` through `ConfigStore.Save`.
  They never write during initial selection (guarded by `_suppressSelectionChange`).
- Closing the window hides to the tray; the engine keeps running. Tray "Exit" kills the engine
  (via app shutdown).

### Screenly.Engine (console)

Composition root and all runtime loops live in `windows/Screenly.Engine/Program.cs` (~1,100 lines):

1. Single-instance mutex `Global\ScreenlyEngine`.
2. DPI awareness (`SetProcessDpiAwareness(2)` / `SetProcessDPIAware`).
3. Load `config.json`; construct one `ParsecVddDisplay` (the VDD handle owner for the process).
4. Start `StreamServer` on `IPAddress.Loopback:<port>` with up to 5 bind retries (3 s apart).
5. Start a 500 ms config poll (codec/FPS changes → `DISCONNECT` + close session).
6. Start an infinite ADB loop: find `adb`, list devices, run `adb -s <serial> reverse tcp:7543
   tcp:<port>` for each; retry every 5 s when no device, every 10 s otherwise.
7. Start the shared frame loop (paced) and the cursor loop (~60 Hz).
8. On Ctrl+C: cancel, stop senders, dispose captures/encoders, dispose server.

## Windows: virtual display

`Screenly.Core/Display/DisplayManager.cs` contains `ParsecVddDisplay`, which talks to the driver
directly with `DeviceIoControl` on the device interface
`\\?\ROOT#DISPLAY#0000#{00b41627-...}` (SetupAPI enumeration is declared but the open path is
hard-coded):

| IOCTL | Purpose |
|---|---|
| `0x0022e004` ADD | Create a display; all-zero 32-byte input; returns int index |
| `0x0022a008` REMOVE | Remove by index (tries 32-byte and 2-byte, big- and little-endian) |
| `0x0022a00c` UPDATE | Keep-alive ping; also sent after ADD |
| `0x0022e010` VERSION | Declared, not used |

Supporting behavior:

- `EnsureRegistryPreset(w,h,hz)` writes a `HKLM\SOFTWARE\Parsec\vdd\<slot>` preset (`width`/`w`,
  `height`/`h`, `hz`) so the driver creates the display at the requested mode. The installer grants
  standard users write access to that key so no per-launch UAC is needed.
- ADD creates at the driver default; the actual mode is applied by the engine with
  `ChangeDisplaySettingsEx(devName, DM_PELSWIDTH|DM_PELSHEIGHT|DM_DISPLAYFREQUENCY)` plus
  `SetDisplayConfig(SDC_TOPOLOGY_EXTEND|SDC_APPLY)`.
- A background thread pings `UPDATE` every **100 ms** while at least one display is tracked.
- Created indices are appended to `%APPDATA%\Screenly\vdd-indices.txt`. On the next run,
  `CleanupStaleIndices()` removes them so crashed runs do not leave ghost displays. Failed removals
  are also persisted for later cleanup.
- `DisplayManager` wraps the driver with a 3-attempt remove retry and keeps the handle on failure.

**Current topology policy:** one shared display for all sessions, reused when the requested
resolution matches. A new client at a different resolution causes the shared display to be removed
and recreated, which invalidates any other active session. Per-client display pooling is only a
proposal (`docs/plan/multi-display-pool.md`, ADR-012).

Fallback: if VDD creation fails, the engine captures the primary screen with `GdiCapture` and
streams that instead (orientation reported as 0).

## Windows: capture

Both captures implement `ICapture` (`Capture/GdiCapture.cs`):

```csharp
void Start(int displayId);
bool TryGetFrame(out CapturedFrame frame);   // non-blocking, null/false when nothing ready
bool RefreshDimensions();                    // true if size/orientation/monitor origin changed
int MonitorX, MonitorY, Width, Height, Orientation;
```

- **`GdiCapture` (JPEG path)** — creates a DC for the Parsec device (`CreateDC`), reads the mode
  with `EnumDisplaySettings`, locates the monitor rect with `EnumDisplayMonitors`, and on every
  frame `BitBlt`s into a compatible bitmap, encodes it to JPEG at quality **70**, and returns the
  bytes. Allocates per frame.
- **`DxgiCapture` (H.264 path, despite the name)** — same GDI `BitBlt` capture, but into a
  **reused** top-down 32-bit DIB section (`CreateDIBSection`) and a reused managed `byte[]`. No
  DXGI Desktop Duplication exists in the codebase. The buffer is valid only until the next
  `TryGetFrame`/`RefreshDimensions`; the frame loop encodes synchronously before that.
- Both find the display by scanning `EnumDisplayDevices` for the **last** attached device whose
  name/string/ID contains `Parsec`.
- `RefreshDimensions()` (called about every 30 frames) picks up resolution changes and
  `DEVMODE.dmDisplayOrientation`. Orientation values are normalized to `0..3` (0°, 90°, 180°, 270°
  clockwise) and are the same values put on the wire.

## Windows: encoding

### JPEG (default, fallback)

`GdiCapture` returns JPEG bytes directly. Every frame is treated as a keyframe
(`VideoFrameHeader.IsKeyframe = true, IsJpeg = true`).

### H.264 (selectable)

`Screenly.Core/Encoding/H264Encoder.cs` wraps `H264Sharp.H264Encoder` (OpenH264):

- Parameters: `CAMERA_VIDEO_REAL_TIME`, one temporal layer, one spatial layer,
  **Baseline**, `LOW_COMPLEXITY`, `iTargetBitrate = bitrateKbps * 1000`,
  `fMaxFrameRate = fps`, `bEnableFrameSkip = false`, `uiIntraPeriod = 0`.
- `uiIntraPeriod = 0` means OpenH264 emits only the first IDR on its own; every later IDR comes
  from `RequestKeyframe()`.
- Requires non-zero **even** width/height; otherwise init fails with a clear `LastError`.
- `Encode(byte[] bgra, ...)` wraps the BGRA buffer in `H264Sharp.RgbImage`, returns Annex B bytes
  and whether the access unit contains an IDR/I frame.
- Every keyframe access unit has SPS+PPS in-band (locked by a test).
- Optional `Encoding.DumpPath` appends raw Annex B to a file for diagnostics; dump failures never
  affect encoding.
- Exposes `FramesEncoded`, `BytesOut`, `EncodeMsMin/Avg/Max`, `LastError`.
- Initialization is retried once at the requested size; on failure the session falls back to
  JPEG/GDI and `encoderError` appears in `status.json` and the Host.

`KeyframePolicy` is the single IDR scheduler:

- IDR on connect / before first frame.
- Periodic IDR every `Encoding.KeyframeIntervalSec` (default 2 s).
- `REQUEST_KEYFRAME` from the client, rate-limited to one per 500 ms per session.

JPEG↔H.264 switching is not negotiated on the wire. The Host edits `config.json`; the engine's
500 ms poll sees the change, sends `DISCONNECT {reason:"codec_change"}`, closes the socket, and
Android reconnects; the next session builds the new encoder. FPS changes behave the same with
`reason:"fps_change"`. If both change in one poll, codec wins.

## Windows: streaming loop and backpressure

Per session (`SessionState` in `Program.cs`):

- `LatestFrameSlot` holds at most **one** encoded frame. The producer never blocks:
  - Before capture: if a frame is still pending, skip capture/encode entirely
    (`BackpressureSkips` / `FramesDropped`).
  - After encode: publishing replaces the pending frame (`SlotReplacements`) and immediately
    requests a keyframe so the next H.264 frame repairs the reference chain. JPEG replacement needs
    no recovery.
- A dedicated sender task (`StartSender`) is the only code that awaits a socket write. It drains
  the slot and writes `VIDEO_FRAME`; this keeps a slow client from stalling capture or other
  sessions.
- Socket writes are serialized by a `SemaphoreSlim` in `Session.SendAsync`.
- `ReadyToStream` is set only after `HELLO_ACK` is written; no video can precede it.

Pacing:

- `FramePacer` uses `Stopwatch` absolute deadlines. If frame work overruns the period it re-anchors
  the next deadline at `now + period` instead of bursting (`Overruns`).
- The loop calls `timeBeginPeriod(1)` for the process, because Windows otherwise rounds `Task.Delay`
  to ~15.6 ms and turns 33 ms into ~46 ms.
- Target FPS is clamped to `1..120`, default 30. 60 works but the GDI capture path cannot sustain
  it at 800×1280 (~23–28 FPS measured); 30 is the stable profile.

Cursor is streamed **outside** the video frame:

- A ~60 Hz loop sends `CURSOR_POS` (17-byte binary: x, y, visible, timestamp) per handshaked
  session. Coordinates are relative to the captured monitor and clamped to its bounds.
- When the Windows cursor handle changes, `CursorShapeCapture` renders the cursor to a transparent
  32-bit PNG (drop shadow removed, 1 px outline preserved) and broadcasts `CURSOR_SHAPE`
  (16-byte header + PNG). The latest shape is cached and replayed to new sessions.
- Android draws the shape/position as a Compose overlay, not inside the video.

## Windows: sessions and protocol

`StreamServer` (`Streaming/Session.cs`) is a `TcpListener` on **`IPAddress.Loopback` only**. For each
accepted `TcpClient` it creates a `Session`, stores it in a list, and starts `MonitorSessionAsync`:

- Sends `PING` every 5 s.
- Runs the receive loop, dispatching non-PONG messages to the engine handler.
- On any receive error or cancellation: removes the session, raises `ClientDisconnected`, disposes
  the socket and session state (capture, encoder, sender).
- `LastPong` is updated, but **no missed-PONG timeout is enforced**; the TCP connection is the
  liveness boundary.

Framing (`Streaming/Protocol.cs`), little-endian:

```
[4 bytes: uint32 length = 1 + payload.Length]
[1 byte:  message type]
[N bytes: payload]
```

Max message size is 4 MiB. All enums and payload layouts are duplicated in
`android/.../net/Protocol.kt`.

Message types:

| Code | Name | Direction | Payload |
|---|---|---|---|
| 0x01 | HELLO | C→S | JSON: deviceName, screenWidth, screenHeight, densityDpi, deviceId, optional pin/token |
| 0x02 | HELLO_ACK | S→C | JSON: status, sessionId, config |
| 0x03 | VIDEO_FRAME | S→C | 17-byte binary header + Annex B NALs or JPEG |
| 0x04 | INPUT_TOUCH | C→S | JSON batch (engine currently ignores) |
| 0x05 | INPUT_KEY | C→S | JSON (engine currently ignores) |
| 0x06 | CONFIG | both | JSON width/height/fps/bitrateKbps/keyframeIntervalSec/codec/orientation |
| 0x07 | PING | S→C | empty |
| 0x08 | PONG | C→S | empty |
| 0x09 | DISCONNECT | S→C | JSON reason; terminal |
| 0x0A | ERROR | both | JSON code/message (declared; not actively sent) |
| 0x0B | CURSOR_POS | S→C | binary x,y,visible,timestamp |
| 0x0C | CURSOR_SHAPE | S→C | binary w,h,hotspotX,hotspotY + PNG |
| 0x0D | REQUEST_KEYFRAME | C→S | empty |

`VIDEO_FRAME` header (`Streaming/FrameCodec.cs`, 17 bytes):

```
uint32 seq
uint64 timestamp_us   // monotonic, from Stopwatch (MonotonicClock)
uint16 width
uint16 height
byte   flags          // bit0 = keyframe, bit1 = JPEG
```

HELLO handling:

1. Parse `deviceId`, `screenWidth`, `screenHeight`.
2. Cap the client resolution to `VirtualDisplay.maxWidth/maxHeight` (default 3840×2160), forcing
   even dimensions.
3. With no client size, fall back to the per-device persisted resolution
   (`vdd-resolutions.json`), then to config defaults.
4. Under a global display lock, create or reuse the shared VDD and build capture/encoder.
5. Reply `HELLO_ACK {status:"ok", sessionId, config{...}}`.
6. Persist the resolution for that `deviceId`.
7. Request an IDR, set `ReadyToStream`, start the sender, replay cursor shape/position.

`CONFIG` handling: if dynamic resolution is enabled, cap the proposed size, recreate the display
only when dimensions actually changed, then reply with the applied config (sourced from the real
capture, so orientation is correct even when the client only proposed a size).

`Disconnect`: logged; the receive loop ends when the peer closes.

## Android: how it works

Single activity, no navigation. `MainActivity.kt` owns the whole client lifecycle.

Connection:

- `ScreenlyClient.connect("127.0.0.1", 7543)` with a 15 s timeout, `tcpNoDelay = true`.
- Retry loop: every 2 s after a failed connect, forever.
- On connect it sends `HELLO` with model name, real screen size (`defaultDisplay.getRealSize`),
  density, and `ANDROID_ID`.
- `HELLO_ACK` sets `connected`, stream dimensions, and the requested activity orientation; starts
  `ConnectionService`.
- 10 s without a first frame shows a Retry button. Retry resets the connection and loop.
- `DISCONNECT` or a socket error closes the client and waits for the retry loop.

Two channels in `ScreenlyClient`:

- `controlEvents`: an **unlimited** `Channel` for HELLO_ACK, CONFIG, ERROR, PONG, CURSOR_POS,
  CURSOR_SHAPE, DISCONNECT. Never dropped.
- `frames`: capacity **1**; a newer frame replaces an undelivered one (`VideoStats.onBacklogSkip`).
  The receive loop is the only producer.

Decode/render:

- Video is consumed on one dedicated thread (`screenly-video`).
- H.264: `MediaCodec` AVC decoder to the `SurfaceView` surface, configured with
  `KEY_PRIORITY = 0` and, on API 30+, `KEY_LOW_LATENCY = 1`. Falls back to a minimal format if the
  enhanced one throws. Input dequeue timeout is 10 ms; outputs are drained and released immediately
  (`releaseOutputBuffer(index, true)`).
- Sequence gaps (`ev.seq != prev + 1`) mark the stream unrecoverable and send `REQUEST_KEYFRAME`;
  non-IDR frames are gated until an IDR that carries SPS+PPS (NAL types 7 and 8) arrives.
  Local request throttle: 500 ms, matching the engine.
- Dimension changes reconfigure the codec and the Compose layout.
- JPEG: decoded on the video thread, letterboxed, and drawn with `lockCanvas()/unlockCanvasAndPost`.
- `DisplayGeometry` is shared by video, cursor, and touch so all three use the same letterboxed
  content rect.

Orientation:

- `Config.orientation` (0..3) plus `width > height` selects `SCREEN_ORIENTATION_LANDSCAPE/PORTRAIT`
  (or the reversed variants for 180°/270°). This hard-locks the tablet and overrides its rotation
  lock while connected; it is released (`UNSPECIFIED`) on disconnect.
- The manifest declares `configChanges="orientation|screenSize|smallestScreenSize"` so the activity
  is not recreated on the swap.

Cursor overlay: Compose `Canvas` draws the cursor bitmap offset by its hotspot at
`cursorX * scale`, or a yellow circle if no shape has arrived.

Touch: `TouchCapture` batches all pointers per `MotionEvent`, maps view coordinates through
`DisplayGeometry.contentRect` (touches in letterbox bars are dropped), and sends `INPUT_TOUCH`.
The engine currently drops these messages (`InputInjector` is a stub), so touch does not move the
cursor.

Service: `ConnectionService` is a `dataSync` foreground service with an ongoing notification and a
10-minute `SCREEN_BRIGHT_WAKE_LOCK`. The activity also holds `FLAG_KEEP_SCREEN_ON`.

## Persisted state and diagnostics

All under `%APPDATA%\Screenly`:

| File | Writer | Reader | Contents |
|---|---|---|---|
| `config.json` | Host (also manual edits) | Engine, Host | Port, VDD defaults/caps, encoding (codec, fps, bitrate, keyframe interval, dump path), pairing placeholders, autoStart |
| `status.json` | Engine (~1 Hz) | Host | codec, width/height, fps, connectionType, connectedDevices, orientation, encoderError, framesEncoded/bytesOut/encodeMs stats, session/capture/send stats, pacer overruns |
| `engine.log` | Engine | Humans | Timestamped lifecycle, VDD, ADB, encoder, and stats lines |
| `vdd-indices.txt` | ParsecVddDisplay | Engine | Live/orphaned VDD indices for stale cleanup |
| `vdd-resolutions.json` | Engine | Engine | Last requested `(w,h)` per Android `deviceId` |

Config shape (`Config/AppConfig.cs`, camelCase JSON):

```json
{
  "version": 1,
  "port": 7543,
  "virtualDisplay": { "width": 1920, "height": 1080, "refreshHz": 60,
                      "dynamicResolution": true, "maxWidth": 3840, "maxHeight": 2160 },
  "encoding": { "bitrateKbps": 8000, "keyframeIntervalSec": 2, "targetFps": 30,
                "codec": "jpeg", "dumpPath": null },
  "pairing": { "pinEnabled": false, "pinHash": null, "salt": null },
  "autoStart": false,
  "firewallRuleAdded": false
}
```

`ConfigStore.Save` is atomic: write `config.json.tmp`, then `File.Replace` (first save uses
`File.Move`). The engine intentionally **polls** rather than using `FileSystemWatcher`, because a
watcher filtered to `LastWrite|Size` misses the rename performed by `File.Replace`.

## Build, run, package

```powershell
# Windows build + tests
cd windows
dotnet build Screenly.sln
dotnet test Screenly.Tests\Screenly.Tests.csproj
dotnet run --project Screenly.Engine
dotnet run --project Screenly.Host

# Publish (self-contained, used by the installer)
dotnet publish windows\Screenly.Engine -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\win-x64
dotnet publish windows\Screenly.Host   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o dist\win-x64
```

Android: open `android/` in Android Studio (or `gradlew`, but no wrapper is checked in), build the
APK, and place the result at `app/Screenly.apk`.

Installer (`installer/screenly.iss`, Inno Setup, admin required):

- Copies `dist\win-x64\*` recursively (runtime + H264Sharp/OpenH264 natives).
- Copies bundled `adb\*` to `{app}\adb`.
- Runs the Parsec VDD 0.45 installer silently.
- Grants standard users write access to `HKLM\SOFTWARE\Parsec\vdd`.
- Seeds a 1920×1080@60 preset.
- Kills Host then Engine (twice, with a pause) before replacing files.
- Deliberately has **no firewall rule** — the engine listens on loopback only.

## Tests (what exists)

`windows/Screenly.Tests` (xUnit):

| File | Covers |
|---|---|
| `SmokeTests.cs` | Default config, protocol round-trip, `REQUEST_KEYFRAME` code |
| `ConfigCodecPollTests.cs` | Poller sees codec after atomic save; unreadable file returns null |
| `FramePacerTests.cs` | Deadline pacing, overrun rebase, clamping |
| `LatestFrameSlotTests.cs` | Single-slot latest-wins, replacement signal |
| `KeyframeAndClockTests.cs` | Monotonic clock conversion/overflow/monotonicity; keyframe policy |
| `H264EncoderTests.cs` | Init, SPS/PPS/IDR, in-band parameter sets on forced IDR |
| `CapResolutionTests.cs` | Client resolution capping/even dimensions |
| `DisplayOrientationTests.cs` | Orientation normalization |
| (no tests) | VDD IOCTL, capture, server/session, Android |

There are no Android unit or instrumentation tests. End-to-end verification is manual (USB device
+ Windows PC).

## Measured performance and known limits

- 800×1280, H.264, 30 FPS target: 10-minute run averaged **29.9 FPS**, 29 dropped frames, 39 pacer
  overruns, 0 slot replacements, encode avg ~3.0 ms, send avg ~0.1 ms.
- 60 FPS at 800×1280 is selectable but not sustained (~23–28 FPS); the GDI `BitBlt` capture is the
  bottleneck (capture ~13.5 ms/frame). 640×1024 reaches ~52 FPS.
- JPEG is the default and cheapest path; H.264 is software-only (no Media Foundation hardware
  encoding — ADR-011).
- The message "DXGI" in file/class names is historical; there is no Desktop Duplication code.

## Current limitations and sharp edges (important for planning)

1. **USB only.** `StreamServer` binds to loopback. There is no Wi-Fi/LAN path, no mDNS, no manual
   IP entry. `adb reverse` is maintained by an infinite engine loop.
2. **Single shared virtual display.** Multiple sessions mirror the same display. A new resolution
   removes/recreates the shared VDD and disrupts existing sessions. Per-client pooling is proposed,
   not implemented.
3. **Touch/keys are not injected.** Android sends `INPUT_TOUCH`/`INPUT_KEY`; the engine ignores
   both, and `InputInjector` only computes scaled coordinates.
4. **No pairing, auth, or TLS.** `PairingConfig` and `pairingToken` exist on the wire, but the HELLO
   handler never validates a PIN or issues a token; `HELLO_ACK.pairingToken` is always null.
5. **Keepalive is advisory.** `PING` is sent every 5 s, but missed `PONG` is not enforced.
6. **Host/Engine "IPC" is file + port polling.** No named pipe, no commands channel. The Host can
   only change `config.json`; the Engine reacts by disconnecting sessions.
7. **Codec/FPS changes always disconnect the client.** Reconnection takes ~2 s (Android retry).
8. **No audio.**
9. **Only 5 Parsec registry preset slots** (`SOFTWARE\Parsec\vdd\0..4`); arbitrary sizes beyond
   that rely on the driver's fallback behavior.
10. **`DxgiCapture` returns a reused buffer.** Consumers must finish reading before the next
    `TryGetFrame`/`RefreshDimensions`; don't share a capture instance across sessions.
11. **Stale docs.** `docs/plan/documentation/architecture.md` and parts of `README.md` /
    `USER_MANUAL.md` still mention mDNS, Media Foundation, named pipes, firewall rules, and DataStore
    that the code does not use. `docs/plan/documentation/api-contract.md` and `adr.md` are much
    closer to reality.

## Where to make changes (extension seams)

| Goal | Start here |
|---|---|
| New wire message / field | `Streaming/Protocol.cs`, `Streaming/FrameCodec.cs`, `Engine/Program.cs` handler, `android/net/Protocol.kt`, `ScreenlyClient.kt` |
| Change capture behavior | `Capture/GdiCapture.cs`, `Capture/DxgiCapture.cs`, `ICapture` |
| Real DXGI Desktop Duplication | Replace `DxgiCapture` internals; keep `ICapture` and the buffer contract |
| Hardware encode | Replace `Encoding/H264Encoder.cs`; keep `IsReady`, `Encode`, `RequestKeyframe`, stats |
| Add input injection | `Input/InputInjector.cs` + handle `InputTouch/InputKey` in `Engine/Program.cs` |
| Per-client displays | `Engine/Program.cs` display/session state, `Display/DisplayManager.cs`; see `docs/plan/multi-display-pool.md` |
| Android UI/state | `MainActivity.kt` (single composable); `ConnectionService` for lifecycle |
| Config/settings | `Config/AppConfig.cs`, `Config/ConfigStore.cs`, Host `MainWindow.xaml(.cs)` |
| Packaging | `installer/screenly.iss`, `dist/win-x64`, `app/` |
| Diagnostics | `WriteStatusFile` in `Engine/Program.cs`, `engine.log`, Android `VideoStats` |

## Planning pointers

- The **contract** between the two sides is: one TCP socket, 4-byte length + 1-byte type framing,
  4 MiB max, no video before `HELLO_ACK`, every H.264 keyframe carries SPS+PPS, `DISCONNECT` is
  terminal. Any change to these needs both sides updated together and a protocol-version decision.
- The **state ownership** is: Engine owns display/capture/encoder/session state in memory; Android
  owns only UI/decoder state and reconnects from scratch. Persisted state is JSON files on Windows,
  nothing on Android (no DataStore despite the stale docs).
- The **safe extension path** is to add optional JSON fields and new message types; the Android
  decoder already tolerates unknown JSON keys (`ignoreUnknownKeys = true`).
- For anything performance-related, measure first: `status.json` and the per-second `engine.log`
  stats line already break down capture, encode, send, drops, and pacer overruns; Android
  `ScreenlyStats` logs received/queued/dropped/codec exceptions once per second.

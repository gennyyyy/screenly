# Screenly Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Build a Windows host + Android client that turns an Android device into a real second monitor, per `docs/plan/architecture.md` and `docs/plan/api-contract.md`.

**Architecture:** Windows engine creates a virtual display (Parsec VDD), captures via DXGI Desktop Duplication, encodes H.264 via Media Foundation, streams length-prefixed messages over TCP; Android decodes via MediaCodec to Surface and sends touch back. Engine runs as separate background process controlled by WPF tray app.

**Tech Stack:** C# .NET 8 (Windows), Kotlin + Jetpack Compose (Android), Parsec VDD (driver), Media Foundation, DXGI, mDNS (Makaretu.Dns.Multicast / NSD on Android).

**Spec:** docs/plan/architecture.md, docs/plan/api-contract.md, docs/plan/adr.md

## Global Constraints
- .NET 8 SDK; min Windows 10 1903 (IddCx 1.4 via Parsec VDD 0.41).
- Android minSdk 26, targetSdk 35, Kotlin 2.x, Compose BOM 2025.x.
- No new dependencies beyond what's listed; prefer BCL/Android SDK.
- All network I/O async; no blocking on UI thread.
- Single TCP connection per session; port default 7543.
- Final build output (Windows installer + Android APK) goes to `E:\code_ni_hans\screenly\app\`.

## Review Focus
1. Driver watchdog ping (VDD unplugs after ~1s without ping) — missed ping kills the display mid-session.
2. Frame backpressure: slow client must drop frames, never accumulate latency.
3. Keyframe on (re)connect: decoder must receive IDR or shows garbage.
4. Touch coordinate scaling when stream size ≠ display size.
5. Firewall/NAT: installer must add firewall rule or first connection silently fails.

---

### Task 1: Scaffold Windows solution
**Files:** Create `windows/Screenly.sln`, `windows/Screenly.Core/Screenly.Core.csproj`, `windows/Screenly.Engine/Screenly.Engine.csproj`, `windows/Screenly.Host/Screenly.Host.csproj`, `windows/Screenly.Tests/Screenly.Tests.csproj`
**Interfaces:** Produces project skeleton; `Screenly.Core` is classlib, others reference it.
- [ ] Step 1: `dotnet new sln -n Screenly` in windows/, create 4 projects, add to sln.
- [ ] Step 2: Set TargetFramework net8.0 (Host/Engine net8.0-windows), ImplicitUsings enable, Nullable enable.
- [ ] Step 3: Add project references Engine→Core, Host→Core, Tests→Core.
- [ ] Step 4: `dotnet build` → 0 errors. Commit.

### Task 2: Scaffold Android project
**Files:** Create `android/settings.gradle.kts`, `android/build.gradle.kts`, `android/app/build.gradle.kts`, `android/app/src/main/AndroidManifest.xml`, `android/app/src/main/kotlin/com/screenly/MainActivity.kt`
**Interfaces:** Produces runnable empty Compose app.
- [ ] Step 1: Create Gradle project with Compose, minSdk 26, targetSdk 35, Kotlin 2.x.
- [ ] Step 2: Add dependencies: compose-bom, material3, lifecycle-viewmodel-compose, datastore-preferences, nsd (built-in), coroutines.
- [ ] Step 3: MainActivity shows "Screenly" text. `./gradlew assembleDebug` → success. Commit.

### Task 3: Config store (Windows)
**Files:** Create `windows/Screenly.Core/Config/ConfigStore.cs`, `windows/Screenly.Core/Config/AppConfig.cs`; Test `windows/Screenly.Tests/Config/ConfigStoreTests.cs`
**Interfaces:** Consumes none. Produces `ConfigStore.Load()/Save(AppConfig)`, `AppConfig` record.
- [ ] Step 1: Write failing test: load missing file returns defaults; save then load round-trips.
- [ ] Step 2: Implement `AppConfig` record + `ConfigStore` using System.Text.Json, atomic write (temp + rename).
- [ ] Step 3: Run tests → pass. Commit.

### Task 4: Parsec VDD integration (Display module)
**Files:** Create `windows/Screenly.Core/Display/IVirtualDisplay.cs`, `windows/Screenly.Core/Display/ParsecVddDisplay.cs`, `windows/Screenly.Core/Display/DisplayManager.cs`
**Interfaces:** Produces `IVirtualDisplay { Create(width,height,refreshHz): DisplayHandle; Remove(handle); SetMode(handle,w,h,refresh); EnumerateDisplays(): List<DisplayInfo>; Ping(); }`.
- [ ] Step 1: Define interfaces and DTOs.
- [ ] Step 2: Verify Parsec VDD control mechanism from ParsecDisplay source (named pipe / IOCTL / CLI) before coding; implement `ParsecVddDisplay` against it; document the mechanism in a code comment. Wrap calls in try/catch, return `Result<T>`. If VDD exposes no programmatic API, fall back to driving it via its documented CLI.
- [ ] Step 3: DisplayManager orchestrates create+ping watchdog (Timer 500ms). Implement `StartWatchdog()`/`StopWatchdog()`.
- [ ] Step 4: Manual test on a Windows machine with VDD installed: create 1920x1080@60 display, verify appears in Settings. Commit.

### Task 5: DXGI capture
**Files:** Create `windows/Screenly.Core/Capture/DxgiCapture.cs`, `windows/Screenly.Core/Capture/CapturedFrame.cs`
**Interfaces:** Produces `DxgiCapture.Start(displayId)`, `TryGetFrame(out CapturedFrame)`, `Stop()`. `CapturedFrame { Texture, Width, Height, TimestampUs }` (cursor composited into texture).
- [ ] Step 1: Implement DXGI Desktop Duplication: create D3D11 device, DuplicateOutput for target display, AcquireNextFrame loop.
- [ ] Step 2: Composite cursor: read `PointerPosition` + `PointerShapeBuffer` from `DXGI_OUTDUPL_FRAME_INFO`; blit cursor shape into staging texture at pointer position before encode (standard DXGI pattern). Skip if no pointer info.
- [ ] Step 3: Expose `TryGetFrame` returning latest frame or null (non-blocking). Test: capture 60 frames in 1s on a test display. Commit.

### Task 6: Media Foundation H.264 encoder
**Files:** Create `windows/Screenly.Core/Encoding/H264Encoder.cs`, `windows/Screenly.Core/Encoding/EncodedFrame.cs`
**Interfaces:** Produces `H264Encoder.Initialize(width,height,fps,bitrateKbps)`, `Encode(texture) -> EncodedFrame?`, `RequestKeyframe()`, `Dispose()`. `EncodedFrame { Data: byte[], IsKeyframe, TimestampUs }`.
- [ ] Step 1: Set up Media Foundation: MFStartup, create H.264 encoder MFT (CLSID_CMSH264EncoderMFT), configure low-latency (CODECAPI_AVLowLatencyMode=TRUE), CBR, no B-frames (MaxBFrames=0), small buffer.
- [ ] Step 2: Implement texture→NV12 conversion (multi-threaded) then feed to MFT, drain output.
- [ ] Step 3: Keyframe on demand via CODECAPI_AVEncVideoForceKeyFrame.
- [ ] Step 4: Test: encode 10 frames, verify H.264 NAL output, at least one IDR. Commit.

### Task 7: Protocol codec + framing
**Files:** Create `windows/Screenly.Core/Streaming/Protocol.cs`, `windows/Screenly.Core/Streaming/FrameWriter.cs`, `windows/Screenly.Core/Streaming/FrameReader.cs`; Tests.
**Interfaces:** Produces `WriteMessage(stream, type, payload)`, `ReadMessageAsync(stream) -> (type, payload)`, `EncodeVideoFrame(...)`, `DecodeVideoFrame(...)`.
- [ ] Step 1: Write failing tests for round-trip framing (length prefix, type byte, payload).
- [ ] Step 2: Implement binary reader/writer with 4-byte length prefix, max 4MiB guard.
- [ ] Step 3: Implement VIDEO_FRAME encode/decode per contract.
- [ ] Step 4: Tests pass. Commit.

### Task 8: TCP stream server
**Files:** Create `windows/Screenly.Core/Streaming/StreamServer.cs`, `windows/Screenly.Core/Streaming/Session.cs`
**Interfaces:** Produces `StreamServer.Start(port)`, `Stop()`, events `ClientConnected(Session)`, `ClientDisconnected(Session)`. `Session` has `SendAsync(type,payload)`, `ReceiveLoopAsync(handler)`.
- [ ] Step 1: TcpListener on port; accept clients; per-client receive loop dispatching to handler.
- [ ] Step 2: Single-slot frame channel (Channel<byte[]> with capacity 1, DropOldest) for video; slow client drops.
- [ ] Step 3: PING/PONG keepalive (5s interval, 10s timeout → disconnect).
- [ ] Step 4: Test with two local clients: frames delivered, disconnect detected. Commit.

### Task 9: Input injection
**Files:** Create `windows/Screenly.Core/Input/InputInjector.cs`
**Interfaces:** Produces `InjectTouch(events)` (scaled coords → SendInput with POINTER_FLAG), `InjectKey(events)`.
- [ ] Step 1: Implement SendInput with INPUT_MOUSE/INPUT_KEYBOARD; multi-touch via multiple INPUT structs in one call.
- [ ] Step 2: Coordinate scaling: stream (x,y) → virtual display pixel coords (multiply by displayW/streamW).
- [ ] Step 3: Pen pressure → not natively supported via SendInput mouse; document limitation (pressure ignored in v1, or use RealTimeStylus — out of scope). Commit.

### Task 10: mDNS discovery (Windows broadcast)
**Files:** Create `windows/Screenly.Core/Discovery/MdnsBroadcaster.cs`
**Interfaces:** Produces `Start(serviceName, port)`, `Stop()`.
- [ ] Step 1: Broadcast `_screenly._tcp.local.` PTR/SRV/TXT records with port + hostname via UDP multicast 224.0.0.251:5353.
- [ ] Step 2: Test: Android NSD / dns-sd sees the service. Commit.

### Task 11: Engine process (composition root)
**Files:** Create `windows/Screenly.Engine/Program.cs`, `windows/Screenly.Engine/Pipeline.cs`
**Interfaces:** Consumes all Core modules. Produces running engine that: creates virtual display, starts capture→encode→stream loop, handles client HELLO/CONFIG/INPUT, watchdog pings.
- [ ] Step 1: Composition: load config, create virtual display, start watchdog, start capture+encode loop feeding StreamServer's frame channel.
- [ ] Step 2: Handle HELLO: validate PIN (if enabled), issue pairing token, send HELLO_ACK with config. Request keyframe on accept.
- [ ] Step 3: Handle CONFIG: apply new display mode + encoder settings; send CONFIG ack.
- [ ] Step 4: Handle INPUT_TOUCH/INPUT_KEY → InputInjector.
- [ ] Step 5: Named-pipe IPC to tray app: commands (start/stop/status/updateConfig), status events.
- [ ] Step 6: Auto-start at login (registry Run key) controlled by config.autoStart. Commit.

### Task 12: WPF tray control UI
**Files:** Create `windows/Screenly.Host/App.xaml`, `windows/Screenly.Host/Views/MainWindow.xaml`, `windows/Screenly.Host/TrayIcon.cs`, viewmodels.
**Interfaces:** Talks to engine via named pipe.
- [ ] Step 1: Tray icon with context menu: Open, Start/Stop engine, Quit.
- [ ] Step 2: MainWindow: display selector, resolution/refresh dropdowns, bitrate slider, PIN enable+entry, auto-start checkbox, status indicator, "Connect info" (IP + port).
- [ ] Step 3: Start/stop engine process; show status. Commit.

### Task 13: Android project networking layer
**Files:** Create `android/app/src/main/kotlin/com/screenly/net/Protocol.kt`, `ProtocolCodec.kt`, `ScreenlyClient.kt`
**Interfaces:** Produces `ScreenlyClient.connect(host,port): Flow<ServerMessage>` and `send(msg)`.
- [ ] Step 1: Implement length-prefixed framing read/write with coroutines (Dispatchers.IO).
- [ ] Step 2: Data classes for all message types with kotlinx.serialization.
- [ ] Step 3: Reconnect with exponential backoff (1s→8s cap). Test with local server. Commit.

### Task 14: Android video decode + render
**Files:** Create `android/app/src/main/kotlin/com/screenly/video/VideoDecoder.kt`, `VideoRenderer.kt` (SurfaceView)
**Interfaces:** Consumes VIDEO_FRAME messages; renders to Surface.
- [ ] Step 1: MediaCodec decoder configured for AVC, low-latency (KEY_LOW_LATENCY=1), output to Surface.
- [ ] Step 2: Feed NAL units; handle IDR; reconfigure on dimension change (new MediaFormat).
- [ ] Step 3: SurfaceView in fullscreen immersive activity; latest-frame rendering (drop stale queued frames).
- [ ] Step 4: Measure decode time; target <2ms. Commit.

### Task 15: Android touch input
**Files:** Create `android/app/src/main/kotlin/com/screenly/input/TouchCapture.kt`, `InputSender.kt`
**Interfaces:** Captures touch on SurfaceView → sends INPUT_TOUCH via dedicated sender thread.
- [ ] Step 1: MotionEvent handler: map pointer coords to stream resolution (view size → stream size).
- [ ] Step 2: Dedicated sender thread with a queue; batch events; never block UI thread.
- [ ] Step 3: Multi-pointer support (pointerId). Commit.

### Task 16: Android discovery + connection UI
**Files:** Create `android/app/src/main/kotlin/com/screenly/discovery/Discovery.kt`, `ui/DiscoveryScreen.kt`, `ui/ViewerScreen.kt`
**Interfaces:** mDNS browse `_screenly._tcp`; list hosts; manual IP entry; connect → ViewerScreen.
- [ ] Step 1: NsdManager discoverServices; resolve to host+port.
- [ ] Step 2: DiscoveryScreen: list of found hosts + manual IP field + PIN entry.
- [ ] Step 3: On connect: HELLO with device info + PIN/token; on HELLO_ACK store token; navigate to ViewerScreen.
- [ ] Step 4: "Waiting" screen shown when disconnected. Commit.

### Task 17: Android foreground service + keep-screen-on
**Files:** Create `android/app/src/main/kotlin/com/screenly/service/ConnectionService.kt`, manifest service declaration.
**Interfaces:** Foreground service keeps connection alive; wake lock; keep screen on (FLAG_KEEP_SCREEN_ON).
- [ ] Step 1: Foreground service with notification; starts on connect, stops on disconnect.
- [ ] Step 2: Acquire wake lock + keep screen on while viewing.
- [ ] Step 3: Handle process death: reconnect on restart. Commit.

### Task 18: Config negotiation + resolution change
**Files:** Modify engine Pipeline + Android VideoDecoder/ConnectionRepository.
**Interfaces:** CONFIG message round-trip applies new resolution/bitrate/fps.
- [ ] Step 1: Server applies CONFIG: change virtual display mode, rebuild encoder, send CONFIG ack with actual.
- [ ] Step 2: Client reconfigures decoder + view on dimension change; request keyframe.
- [ ] Step 3: Handle orientation: client requests a matching display mode (swap W/H) on rotation; server recreates virtual display. Test: rotate phone → display + stream update within 2s. Commit.

### Task 19: Pairing (PIN)
**Files:** Modify engine HELLO handler + Android DiscoveryScreen/ConnectionRepository.
**Interfaces:** Optional PIN flow per ADR-010.
- [ ] Step 1: Engine: if pinEnabled and hello.pin missing/invalid → HELLO_ACK{pin_required/pin_invalid}.
- [ ] Step 2: Android: PIN entry dialog; store issued token in DataStore; send token on reconnect.
- [ ] Step 3: Engine validates token (constant-time compare). Commit.

### Task 20: Installer + firewall rule
**Files:** Create `scripts/build-installer.ps1`, Inno Setup script `installer/screenly.iss`.
**Interfaces:** Produces Windows installer (MSI/Inno) that installs .NET runtime (if missing), Parsec VDD, engine, host, adds firewall rule for 7543, registers auto-start (optional).
- [ ] Step 1: Inno Setup script: install files, run VDD installer, add firewall rule via netsh, create shortcuts. Output installer to `..\app\Screenly-Setup.exe`.
- [ ] Step 2: Build Android APK via Gradle; document sideload.
- [ ] Step 3: End-to-end smoke test on two machines. Commit.

### Task 21: End-to-end verification
- [ ] Step 1: Install on Windows PC + Android phone; create virtual display; connect over WiFi; verify 60fps 1080p, touch moves cursor.
- [ ] Step 2: Test USB path via adb reverse.
- [ ] Step 3: Test reconnect (kill WiFi briefly) → recovers within 10s.
- [ ] Step 4: Measure latency (frame timestamp → render timestamp); document. Target <50ms glass-to-glass.
- [ ] Step 5: Fix any issues found. Commit.

---

## Verification checklist (proof)
- [ ] `dotnet build` clean; `./gradlew assembleDebug` clean.
- [ ] Unit tests: ConfigStore, Protocol framing.
- [ ] Manual: virtual display appears in Windows Settings.
- [ ] Manual: 60fps@1080p sustained over 5 min WiFi.
- [ ] Manual: touch → cursor movement <50ms.
- [ ] Manual: reconnect after network drop works.
- [ ] Manual: close tray window → stream keeps running.

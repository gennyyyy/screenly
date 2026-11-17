# Architecture Decision Records — Screenly (Windows host + Android second screen)

## ADR-001: Reuse Parsec VDD instead of writing a custom IddCx driver
- **Status:** Accepted
- **Context:** A true "second screen" requires Windows to expose a real virtual monitor (so apps render to it, drag windows onto it). Options: (a) write our own IddCx UMDF driver, (b) bundle an existing one.
- **Decision:** Bundle Parsec VDD (IddCx-based, free, supports up to 4K@240Hz, up to 16 displays per adapter).
- **Rationale:** Writing/maintaining a UMDF driver is months of work and a kernel-adjacent risk; Parsec VDD is battle-tested by Parsec/Sunshine/Moonlight ecosystems. Lazy: reuse.
- **Consequences:** Must keep the display alive with a periodic ping (VDD unplugs after ~1s without pings). Driver install requires admin + reboot on first install. HDR not supported by Parsec VDD (acceptable for v1; SDR 8/10-bit only).
- **Upgrade path:** If Parsec VDD licensing or behavior becomes a blocker, swap in VirtualDrivers/Virtual-Display-Driver or our own fork of Microsoft's IndirectDisplay sample. The `DisplayManager` interface hides the driver behind `IVirtualDisplay`.

## ADR-002: C# / .NET 8 for the Windows host
- **Status:** Accepted
- **Context:** Need DXGI Desktop Duplication, Media Foundation H.264 encode, SendInput, TCP sockets, mDNS, tray UI.
- **Decision:** C# .NET 8 with P/Invoke/COM interop to DXGI/MF.
- **Rationale:** Proven by deskstream (same pipeline); fastest iteration; strong Windows API support; WPF/WinForms tray UI. Rust/C++ would be lower-level but slower to ship and harder to staff.
- **Consequences:** Requires .NET 8 runtime (bundled in installer). COM interop code needs care around apartment threading.

## ADR-003: Kotlin for the Android client
- **Status:** Accepted
- **Context:** Need hardware H.264 decode to Surface, multi-touch, foreground service, low latency.
- **Decision:** Native Kotlin (Jetpack Compose UI, MediaCodec, SurfaceView).
- **Rationale:** Direct access to MediaCodec hardware decode and Surface rendering; no framework overhead; matches "latest-frame" rendering needs. Flutter/RN add a layer that fights low-latency decode.
- **Consequences:** Android-only (acceptable: Windows+Android scope). minSdk 26 (Android 8.0) for stable MediaCodec low-latency mode.

## ADR-004: Single TCP connection, multiplexed messages (no UDP/WebRTC in v1)
- **Status:** Accepted
- **Context:** Need video + control + input transport. Options: TCP, UDP+FEC, WebRTC.
- **Decision:** One TCP connection carries length-prefixed multiplexed messages (video frames + control + input). ADB reverse for USB.
- **Rationale:** TCP is reliable and simple; SuperScreen achieved 60fps@1080p over direct TCP on LAN. Avoids FEC/packet-reassembly bugs. WebRTC is overkill for LAN and adds NAT/ICE complexity.
- **Consequences:** TCP head-of-line blocking can add jitter on lossy WiFi. Mitigation: single-slot frame channel (drop stale frames server-side), latest-frame rendering client-side.
- **Upgrade path:** If profiling shows TCP HOL blocking hurting latency, split video to UDP with XOR FEC (deskstream pattern). Mark with `ponytail:` comment at the transport seam.

## ADR-005: No database — JSON config files
- **Status:** Accepted
- **Context:** App state = settings, pairing keys, recent hosts. No multi-user, no server.
- **Decision:** JSON files (`%APPDATA%/Screenly/config.json` on Windows; DataStore on Android). No DB.
- **Rationale:** YAGNI. A DB adds migrations and a dependency for ~10 keys.
- **Consequences:** No query capability; fine at this scale. If settings grow complex, migrate to SQLite.

## ADR-006: Windows listens, Android connects; discovery via mDNS
- **Status:** Accepted
- **Context:** Who initiates the connection and how does the client find the host.
- **Decision:** Windows host listens on a fixed TCP port (default 7543). Android client connects. Windows broadcasts `_screenly._tcp` via mDNS; Android browses it. Manual IP entry as fallback.
- **Rationale:** One firewall prompt on Windows (one-time); simple mental model; mDNS removes IP typing for the common case.
- **Consequences:** Firewall must allow port 7543 (installer adds rule). mDNS may be blocked on some corporate WiFi — manual IP fallback covers it. USB uses `adb reverse tcp:7543 tcp:7543` then client connects to localhost.

## ADR-007: H.264 hardware encode/decode (not HEVC/AV1 in v1)
- **Status:** Accepted
- **Context:** Codec choice for video.
- **Decision:** H.264 (AVC) via Media Foundation hardware encoder (Windows) and MediaCodec hardware decoder (Android).
- **Rationale:** Universal hardware support on both sides; lowest decode latency (~1-2ms). HEVC/AV1 save bandwidth but uneven hardware support and higher decode latency on mid-range phones.
- **Upgrade path:** Add HEVC as an optional negotiated codec when both sides advertise support.

## ADR-008: DXGI Desktop Duplication for capture (not GDI)
- **Status:** Accepted
- **Context:** How to grab frames of the virtual display.
- **Decision:** DXGI Desktop Duplication API (GPU-direct), capturing the virtual display.
- **Rationale:** Zero CPU copy before encode; lower latency; captures cursor correctly (blended). GDI BitBlt is CPU-bound and slower.
- **Consequences:** Requires a real display (the virtual one) — satisfied by ADR-001.

## ADR-009: Separate worker process for streaming engine (Windows)
- **Status:** Accepted
- **Context:** If the control UI crashes, should the second screen die?
- **Decision:** Streaming engine runs in a separate background process (`Screenly.Engine.exe`) controlled by the tray app via local IPC. Closing the tray window keeps the screen alive; engine can auto-start at login.
- **Rationale:** Matches SuperScreen's "runs in background" UX; a UI crash doesn't kill the session.
- **Consequences:** IPC between tray and engine (named pipes). More processes to manage in installer.

## ADR-010: Optional PIN pairing (LAN security)
- **Status:** Accepted
- **Context:** Anyone on the LAN could connect to an open host.
- **Decision:** Optional 4-6 digit PIN set in Windows app; client must enter it on first connect. PIN hashed (SHA-256 + salt) and stored in config. Pairing token issued after success and stored on client (DataStore) for auto-reconnect. PIN can be disabled for trusted networks.
- **Rationale:** Minimal friction for home use; basic protection against drive-by connections on shared WiFi.
- **Consequences:** Not enterprise-grade security; no TLS in v1 (LAN assumed trusted). Document limitation.
- **Upgrade path:** Add TLS + certificate pinning if used on untrusted networks.

# Architecture & Structure — Screenly

## Goal
Turn an Android device into a real second monitor for a Windows PC: Windows exposes a virtual display, captures/encodes it, streams H.264 over TCP; Android decodes and renders fullscreen; touch/pen input flows back and is injected as native pointer input.

## Components

### Windows host (C# .NET 8)

| Module | Responsibility |
|---|---|
| `Screenly.Host` (WPF) | Tray icon, control window (monitor pick, resolution, bitrate, PIN, start/stop, auto-start toggle). Talks to engine via named pipe. |
| `Screenly.Engine` | Background worker: owns virtual display, capture, encode, stream server, input injection, watchdog ping. |
| `Display/` | `IVirtualDisplay`: create/remove virtual monitor, set mode (resolution/refresh), enumerate displays. Backed by Parsec VDD control API. |
| `Capture/` | DXGI Desktop Duplication on a chosen display → `ID3D11Texture2D` frames (with cursor blended). |
| `Encoding/` | Media Foundation H.264 hardware encoder (CBR, no B-frames, short VBV, keyframe on demand). |
| `Streaming/` | TCP listener, session management, length-prefixed message framing, single-slot frame channel (drop stale). |
| `Input/` | Receive INPUT_TOUCH/INPUT_KEY → `SendInput` injection (multi-touch, pen pressure). |
| `Discovery/` | mDNS broadcast `_screenly._tcp`. |
| `Config/` | JSON config load/save (`%APPDATA%/Screenly/config.json`). |

### Android client (Kotlin)

| Module | Responsibility |
|---|---|
| `ui/` | Compose screens: discovery list, connection status, settings, fullscreen viewer. |
| `discovery/` | mDNS browse + manual IP entry. |
| `net/` | TCP client, protocol codec, reconnect with backoff. |
| `video/` | MediaCodec H.264 decoder → Surface; latest-frame rendering. |
| `input/` | Touch capture (multi-pointer), coordinate mapping, dedicated sender thread. |
| `service/` | Foreground service to keep connection alive in background. |
| `data/` | DataStore preferences (paired host, PIN token, resolution). |

## Data flow

```
[Windows] Virtual display (Parsec VDD)
   → DXGI Desktop Duplication captures frames (GPU)
   → Media Foundation H.264 encode (hardware, low-latency)
   → TCP stream (length-prefixed VIDEO_FRAME messages)
   → [Android] MediaCodec decode → SurfaceView (fullscreen)
[Android] touch events
   → dedicated sender thread → TCP (INPUT_TOUCH messages)
   → [Windows] InputInjector → SendInput → virtual display
```

## State management

- **Windows:** `SessionManager` singleton in engine holds runtime state (connected clients, current display, encoder settings). Persisted config in JSON file. No reactive framework needed; tray UI polls engine over named pipe.
- **Android:** `ViewModel` + `StateFlow` per screen; `ConnectionRepository` exposes connection state. Persisted prefs in DataStore (typed, async). No Room/DB.

## Directory structure

```
screenly/
├── docs/plan/                      # this folder
├── windows/
│   ├── Screenly.sln
│   ├── Screenly.Host/              # WPF tray app
│   │   ├── App.xaml
│   │   ├── Views/
│   │   └── Program.cs
│   ├── Screenly.Engine/            # background worker (console/service)
│   │   ├── Program.cs
│   │   └── Ipc/
│   ├── Screenly.Core/              # shared library
│   │   ├── Display/
│   │   ├── Capture/
│   │   ├── Encoding/
│   │   ├── Streaming/
│   │   ├── Input/
│   │   ├── Discovery/
│   │   └── Config/
│   └── Screenly.Tests/
├── android/
│   ├── app/
│   │   ├── src/main/kotlin/com/screenly/
│   │   │   ├── ui/
│   │   │   ├── net/
│   │   │   ├── video/
│   │   │   ├── input/
│   │   │   ├── discovery/
│   │   │   ├── service/
│   │   │   └── data/
│   │   └── src/main/res/
│   ├── build.gradle.kts
│   └── settings.gradle.kts
├── driver/
│   └── parsec-vdd/                 # bundled installer + license
├── scripts/
│   └── build-installer.ps1
└── app/                            # FINAL BUILD OUTPUT (installer + APK)
```

## Config schema (Windows, %APPDATA%/Screenly/config.json)

```json
{
  "version": 1,
  "port": 7543,
  "virtualDisplay": { "width": 1920, "height": 1080, "refreshHz": 60 },
  "encoding": { "bitrateKbps": 8000, "keyframeIntervalSec": 2 },
  "pairing": { "pinEnabled": false, "pinHash": null, "salt": null },
  "autoStart": false,
  "firewallRuleAdded": false
}
```

## Android preferences (DataStore)
- `pairedHostIp: String?`
- `pairedHostPort: Int` (default 7543)
- `pairingToken: String?`
- `preferredWidth/Height: Int?`
- `keepScreenOn: Boolean` (default true)
- `lastResolution: String?`

## Security model (v1)
- LAN-trusted. Optional PIN pairing (ADR-010). No TLS. Pairing token stored client-side. Document: do not use on untrusted networks without VPN.

## Failure modes & mitigations
- Driver ping watchdog: engine pings Parsec VDD every 500ms; if VDD unplugs, re-create display and request keyframe.
- Client disconnect: server keeps running; new client gets IDR keyframe on connect.
- Slow client: single-slot channel drops stale frames; never queues.
- Reconnect: exponential backoff (1s→2s→4s→8s cap) on client; server accepts new session.
- Resolution change: re-create virtual display at new mode; rebuild encoder; send CONFIG message; client reconfigures MediaCodec.
- Crash isolation: engine separate process (ADR-009); tray restarts engine if it dies.

# H.264 Baseline — 2026-11-19

## Update — 1 ms timer resolution (2026-11-19 16:39)

The 60-second sample was repeated after raising the Engine process timer resolution to 1 ms around the frame loop:

| Metric | Result |
|---|---|
| FPS samples | avg 29.8, min 25, max 30 |
| `pacerOverruns` delta | 6 |
| `backpressureSkips` delta | 4 |
| `slotReplacements` delta | 0 |
| `framesEncoded` delta | 1,820 over 60 s (~30.3 FPS) |
| `framesDropped` delta | 4 |
| Send time | avg 0.1 ms, max 0.6 ms |
| Encode time | avg 3.0 ms, max 13 ms |

The earlier 26 FPS result was Windows timer granularity rounding a 33 ms delay up to ~46 ms, not encoder or socket throughput. The 30 FPS target is now met in this sample; the 10-minute stability run is the remaining check.

## Capture-surface reuse and 60 FPS decision (2026-11-19)

`DxgiCapture` now reuses one memory DC/DIB/BGRA buffer per session. It removed allocation spikes (capture max 267 → 23 ms) but not the GDI floor:

- 800×1280, 60 FPS target: capture 13.5 ms, encode 2.5 ms, send 0.1 ms → ~38 FPS.
- 640×1024, 60 FPS target: ~52 FPS average, 55–59 FPS steady.
- Native 60 FPS requires real DXGI Desktop Duplication; it is not reachable with GDI `BitBlt`.

**Decision:** keep 30 FPS as the stable native profile; leave the 60 FPS selector as experimental. A 60 FPS reduced-resolution profile or a DXGI capture spike can be scoped separately.

## 60 FPS trial (2026-11-19 18:03)

Live `targetFps` switching works: the Engine logs `target fps switched to 60`, sends `DISCONNECT(fps_change)`, and the next session starts at 60. Sustained output does not:

| Metric | Result |
|---|---|
| 20 s FPS samples | avg 23.2, min 0, max 33 |
| Typical steady samples | 25–28 FPS |
| Pacer overruns | 394 |
| Backpressure skips | 350 |
| Encode | avg 2.5 ms |
| Send | avg 0.1 ms |

The capture/loop path cannot meet the 16.6 ms period at 800×1280 with the current GDI capture and software encode. The selector is usable, but 30 FPS remains the stable profile. 60 FPS requires capture optimization (DXGI Desktop Duplication) or hardware encoding.

## 10-minute stability result (2026-11-19)

| Metric | Result |
|---|---|
| FPS samples | avg 29.9, min 22, max 31 |
| Per-minute FPS | 30, 30, 30, 30, 30, 30, 29, 29, 30, 30 |
| `framesEncoded` delta | 18,158 over 600 s (~30.3 FPS) |
| `framesDropped` delta | 29 |
| `pacerOverruns` delta | 39 |
| `backpressureSkips` delta | 29 |
| `slotReplacements` delta | 0 |
| Send time | avg 0.1 ms, max 1.0 ms |
| Encode time | avg 3.0 ms, max 21.7 ms |

The 30 FPS target is sustained for 10 minutes at 800×1280. The occasional 22 FPS sample is a single-second dip; every per-minute sample is 29–30 FPS. No encoder errors, no slot replacements, and the send path remains negligible.

## Initial measurements

## Configuration

- Windows: `dist/win-x64/Screenly.Engine.exe`, H.264 via H264Sharp 1.6.0 / OpenH264 2.4.1
- Virtual display: Parsec VDD, 800×1280 (client native portrait), 60 Hz
- Encoding: `targetFps=30`, `bitrateKbps=20000`, `keyframeIntervalSec=2`, Baseline, `uiIntraPeriod=0`
- Android: `Tab_S28_Ultra_Pro`, Android 8.1 / API 27, `OMX.MTK.VIDEO.DECODER.AVC`, USB via `adb reverse`
- APK: `app/Screenly.apk` (9,692,355 bytes), diagnostics removed

## Measured 60-second window

| Metric | Result |
|---|---|
| `status.json` FPS samples | avg 26.6, min 22, max 30 |
| Frames encoded delta | 1,553 over 60 s (~25.9 FPS) |
| `framesDropped` delta | 224 (~3.7/s) |
| Encode time | avg 2.3 ms, max 14.0–16.2 ms |
| Encoder errors | none (`encoderError=null`) |
| Android decoder | `recv=28/s`, `queued=28/s`, `dropped=0`, `codecEx=0` |
| First IDR | 93,214 bytes, Annex B `nalTypes=[7, 8, 5]` |
| First output | 12 bytes |
| Visual | `dist/screenshots/screenly-h264.png` shows the live Windows desktop |

## Interpretation

- H.264 is software-encoded at 800×1280 and decoded by the MTK hardware decoder; end-to-end rendering is confirmed.
- Encoder compute is not the bottleneck: average 2.3 ms per 33 ms frame budget.
- The 30 FPS target is not consistently met: average 26.6 FPS with ~3.7 dropped frames/s. Drops are accounted by `framesDropped` (skip-before-capture or latest-slot replacement), not encoder failure.
- Glass-to-glass latency and 10-minute thermal stability were not measured.
- The earlier `MediaCodec.configure` failure was from an intermediate APK; the current APK configures and decodes without exception.

## Next measurements

- Identify the drop source: sender/socket backpressure vs pacer overrun vs decode timing.
- Run 10 minutes at 800×1280 and record FPS/drop drift and temperatures.
- Measure glass-to-glass latency for JPEG and H.264 at the same resolution.
- If 30 FPS must be guaranteed, evaluate a lower resolution/bitrate or hardware encoding after the drop source is known.

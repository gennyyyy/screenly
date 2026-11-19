# Plan — Get H.264 Working in Screenly

**Status:** Phase 0–6 complete. H.264 is verified end to end on the test tablet. 10-minute stability at 800×1280: 29.9 FPS average, 29 dropped frames, 39 pacer overruns, no encoder errors. Native 60 FPS is not reachable with GDI capture (38 FPS at 800×1280); 30 FPS is the stable profile and the 60 selector is experimental. The only unverified item is the clean-VM installer check; see `docs/plan/performance-baseline.md`.

## Goal

Selecting H.264 in the Host gives a picture on Android within 200 ms of `HELLO_ACK` (not from TCP connect; VDD creation is excluded), with no smearing or green blocks, at least as fast as JPEG, on both `dotnet run` and an installed build.

## Contract decisions

- H.264 v1 remains software OpenH264 via H264Sharp 1.6.0. Media Foundation stays blocked (ADR-011).
- Default codec remains JPEG. H.264 is selected in the Host.
- `VIDEO_FRAME` timestamps become monotonic microseconds from `Stopwatch`, not .NET ticks.
- Every IDR access unit must contain SPS and PPS in-band.
- No `VIDEO_FRAME` may be sent before `HELLO_ACK`.
- `REQUEST_KEYFRAME` is added as message type `0x0D` (empty payload, C→S). The engine rate-limits it to one request per 500 ms.
- `DISCONNECT` is terminal: the sender closes after writing it; the receiver closes and reconnects. Android must handle it explicitly.
- `status.json` adds `encoderError` (string or null), `framesEncoded`, `bytesOut`, `encodeMsMin`, `encodeMsAvg`, `encodeMsMax`.
- `Encoding.DumpPath` (nullable string) writes raw Annex B encoder output for diagnostics.
- H.264 bitrate default becomes 20,000 kbps for new configs, configurable; the encoder must reject unsupported dimensions with a clear error rather than silently producing garbage.

## Phase 0 — Make failures visible

Scope: `H264Encoder.cs`, `Screenly.Engine/Program.cs`, `Screenly.Host/MainWindow.xaml(.cs)`.

- Log every `H264Encoder` catch with the full exception, including `DllNotFoundException` and `BadImageFormatException`.
- Expose `LastError` (string) from `H264Encoder`; keep `IsReady`.
- Add `encoderError` and `framesEncoded` to `status.json`; show `encoderError` in the Host.
- Log once per second: frames encoded, bytes out, encode time min/avg/max.
- Android logs: frames received, queued to MediaCodec, dropped, and any `MediaCodec.CodecException`.
- Acceptance: selecting H.264 shows either a specific error or live stats; never silence.

## Phase 1 — Prove the encoder in isolation

**Phase 1A finding (2026-11-18):** `TargetFps = 30` currently makes H264Sharp `Initialize` return failure (`DetermineTemporalSettings`, input/output fps ratio vs `iTemporalLayerNum`). 10/25/32/60 initialize. Phase 1A must determine the correct parameter combination and make 30 initialize; do not silently change the product's default target FPS.

- Add `Encoding.DumpPath`; append raw encoder output to `dump.h264`.
- Run H.264 for 10 seconds and play the dump with `ffplay`.
- Assert the first access unit contains Annex B start codes and NAL types 7 (SPS), 8 (PPS), 5 (IDR). Accept 3- or 4-byte start codes; do not require an exact `00 00 00 01` prefix.
- Verify even dimensions; verify BGRA input through `H264Sharp.RgbImage`; verify the top-down DIB is `width * height * 4` with stride `width * 4`.
- Verify bitrate and frame-skip settings are applied; do not rewrite H264Sharp’s internal I420 conversion unless the dump shows a color fault.
- Add `Screenly.Tests/H264EncoderTests.cs`: encode 5 synthetic BGRA frames; assert SPS, PPS, IDR, and that the stream starts with a keyframe.
- Acceptance: dump plays correctly and the test passes. If not, Phases 2–3 are blocked.

## Phase 2 — Packaging

- Publish engine to a dedicated `dist/win-x64` folder, self-contained, `PublishSingleFile=false`.
- Installer copies the whole publish folder; never copy `app/` directly (it contains `engine_new` and `runtimes`).
- Ensure `H264Sharp.dll`, `H264SharpNative-win64.dll`, `openh264-2.4.1-win64.dll`, and `Microsoft.Win32.SystemEvents.dll` are present in the installed output.
- Clean-machine check: install, select H.264, confirm either a specific error or a live stream; no manual DLL copying.
- Acceptance: installed build encodes H.264 without manual file placement.

## Phase 3 — Stream start and recovery

**Phase 3A1 finding (2026-11-18):** OpenH264 2.4.1 via H264Sharp 1.6.0 emits SPS (7) and PPS (8) before every IDR, including `ForceIntraFrame` IDRs after the first frame. No parameter-set cache/prepend was added; the invariant is locked by `H264EncoderTests.ForcedIdr_ContainsSpsPpsInBand` instead.

- Gate `SESSION_READY` on `HELLO_ACK`; no video before the ACK is written.
- Force an IDR on the session’s encoder before the first frame after `HELLO_ACK`.
- Ensure SPS/PPS precede every IDR; cache and prepend if H264Sharp/OpenH264 does not emit them.
- Implement `REQUEST_KEYFRAME (0x0D)` and a 500 ms rate limit.
- On host codec change, send `DISCONNECT`, close the session, and let Android reconnect with the new codec.
- Runtime verification 2026-11-19: the engine-side 500 ms codec poll detects `ConfigStore.Save` atomic replacement; Host JPEG↔H.264 toggles produced `config changed` + `DISCONNECT` and the next session used the new codec. The Host startup selection overwrite was fixed by suppressing `SelectionChanged` during combo initialization.
- On encoder reinit failure, retry once, surface `encoderError`, and keep the session recoverable; do not permanently lock to JPEG without reporting it.
- Acceptance: first picture within 200 ms of `HELLO_ACK`; codec switch updates the phone within a few seconds without manual action.

## Phase 4 — Android decoder fixes

- Set `KEY_LOW_LATENCY`, `KEY_PRIORITY = 0`, and optionally `KEY_OPERATING_RATE`.
- Replace `dequeueInputBuffer(0)` with a 5–10 ms timeout.
- On a dropped frame, send `REQUEST_KEYFRAME` and discard non-key frames until the next IDR.
- Ensure the first buffer after `configure()` is SPS/PPS or an IDR with parameter sets prepended.
- Move decode off the event collector: receive loop → one-slot latest-frame handoff → decode worker → output drain; render with `releaseOutputBuffer(index, true)`.
- Do not use `InputStream.available() > 0` to skip H.264 frames.
- Make control messages (`PING`, `CONFIG`, `DISCONNECT`, `REQUEST_KEYFRAME`) non-droppable, separate from video frames.
- Acceptance: 10-minute run with zero decoder exceptions; any dropped frame recovers within one keyframe round trip.

## Phase 5 — Backpressure and pacing

- Per-session one-slot latest-frame holder plus a dedicated sender task; the shared loop never awaits a socket.
- Prefer skipping capture/encode while a send is pending over dropping encoded P-frames; if an encoded frame is dropped, request a keyframe.
- Enforce `targetFps` with a stopwatch-based pacer; remove the fixed 10 ms sleep.
- Use monotonic microsecond timestamps and measure latency with them.
- Keyframe policy: IDR on connect, on request, and every 2–5 seconds; remove the `uiIntraPeriod = 300` / forced-IDR overlap.
- Host exposes a 30/60 FPS selector. Both switch live via `DISCONNECT(fps_change)`. 30 FPS is the measured stable profile; 60 FPS runs at ~23–28 FPS on the test tablet with the current capture path.
- Acceptance: a slow client does not slow other sessions or capture; logs show steady frame intervals.

## Phase 6 — Benchmark and decision

- Record FPS, encode ms, bytes/frame, and glass-to-glass latency for JPEG and H.264 at 1080p in `docs/plan/performance-baseline.md`.
- If H.264 holds 30+ FPS at text-capable bitrate, stop codec work.
- If CPU encode or GDI capture is the limit, next projects are DXGI Desktop Duplication and a hardware encoder (NVENC/AMF/QuickSync or Media Foundation).

## Risks

- Software OpenH264 at 1080p60 may not keep up; 60/120 Hz points to the Phase 6 hardware path.
- Baseline profile needs more bitrate for text than High.
- Some Android decoders buffer regardless of `KEY_LOW_LATENCY`; measure before changing SPS VUI.

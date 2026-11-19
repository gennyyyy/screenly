# H.264 Encoding — Current Implementation & Issues

## Status

The shipping H.264 path does **not** use Windows Media Foundation. The Media Foundation
H.264 MFT (`mfh264enc.dll`, CLSID `6ca50344-051a-4ded-9779-a43305165e35`) is registered
but cannot be activated as a usable `IMFTransform` in this environment (see
`docs/plan/h264-encoder-implementation.md` — blocked). The current implementation substitutes
**Cisco OpenH264 via the `H264Sharp` 1.6.0 NuGet wrapper**, a software encoder.

- Windows side: `windows/Screenly.Core/Encoding/H264Encoder.cs` wraps `H264Sharp.H264Encoder`.
- Capture: `DxgiCapture` (GDI BitBlt into a top-down BGRA DIB) — despite the name it is not
  DXGI Desktop Duplication.
- Android side: `android/.../video/VideoDecoder.kt` decodes Annex B AVC via `MediaCodec` to a `Surface`.
- Codec is selectable at runtime through the host app / `config.json` (`Encoding.Codec = "h264"|"jpeg"`).

ADR-007 originally selected "Media Foundation hardware encoder". The current build deviates:
it is a software OpenH264 encoder. Treat ADR-007 as not-yet-satisfied.

## Data flow

```
DxgiCapture.TryGetFrame  -> CapturedFrame { JpegData: byte[] BGRA, Width, Height, TimestampUs }
     |
H264Encoder.Encode(bgra, w, h, tsUs)
     |  ForceIntraFrame (if requested)
     |  RgbImage(Bgra) -> OpenH264.Encode -> EncodedFrame { Data: Annex B bytes, IsKeyframe, TimestampUs }
     |
VideoFrameHeader(seq, tsUs, w, h, isKeyframe, isJpeg=false)
     |
FrameCodec.Encode -> length-prefixed MessageType.VideoFrame over TCP
     |
Android ScreenlyClient -> VideoDecoder.decode(nal, ptsUs, isKeyframe) -> MediaCodec -> Surface
```

## Implementation details

### `H264Encoder` (`windows/Screenly.Core/Encoding/H264Encoder.cs`)

- `Initialize(width, height, fps, bitrateKbps)`:
  - `new H264Sharp.H264Encoder()`
  - `GetDefaultParameters()` then override:
    - `iUsageType = CAMERA_VIDEO_REAL_TIME`
    - `iPicWidth/Height`, `iTargetBitrate = bitrateKbps * 1000`
    - `iTemporalLayerNum = 1`, `iSpatialLayerNum = 1`
    - spatial layer 0: width/height, `fFrameRate = fps`, `iSpatialBitrate = bitrateKbps * 1000`
    - `uiProfileIdc = PRO_BASELINE`, `uiLevelIdc = 0` (auto)
    - `iComplexityMode = LOW_COMPLEXITY`
    - `uiIntraPeriod = 300` (IDR every ~10 s at 30 fps)
    - `bEnableFrameSkip = false`
  - `_encoder.Initialize(param)`; `_ready = true` on success.
- `Encode(bgra, width, height, timestampUs)`:
  - guards: ready, non-null bgra, `bgra.Length >= width*height*4`
  - consumes `_forceKeyframe` via `ForceIntraFrame()`
  - `new RgbImage(ImageFormat.Bgra, width, height, bgra)` then `_encoder.Encode(..., out encodedFrames)`
  - `encodedFrames.GetAllBytes()` -> `Data`
  - `IsKeyframe` = any frame with `FrameType.IDR` or `FrameType.I`
  - swallows all exceptions and returns `null`.
- `RequestKeyframe()` sets `_forceKeyframe = true`.
- `Dispose()` disposes the inner encoder.

### Engine integration (`windows/Screenly.Engine/Program.cs`)

- `requestedCodec` loaded from config; `CreateCaptureForState`:
  - h264: `DxgiCapture(70)` + `H264Encoder`, init with `dc.Width/Height`, `TargetFps`, `BitrateKbps`.
  - on any init failure: log, dispose, fall back to `GdiCapture(30)`, `UseH264 = false`.
- Frame loop:
  - every 30 frames: `cap.RefreshDimensions()`; on change, reinit encoder (or fall back to JPEG + GdiCapture).
  - if `UseH264`: `TryGetFrame` -> `Encode(rawFrame.JpegData, ...)`; on non-null, send `VideoFrameHeader(..., enc.IsKeyframe, isJpeg=false)`.
  - keyframe request: `if (seq % (TargetFps * KeyframeIntervalSec) == 0) Encoder.RequestKeyframe();` (every 2 s at defaults).
  - frame loop delay 10 ms after processing.
- Advertises active codec in `HelloAck` config and periodic status file.

### Android decoder (`android/.../video/VideoDecoder.kt`)

- `MediaCodec` AVC decoder configured with `KEY_LOW_LATENCY = 1`, output to `Surface`.
- Surface lifecycle handled via `SurfaceHolder.Callback`; deferred config until surface exists.
- `decode(nal, ptsUs, isKeyframe)` queues input, sets `BUFFER_FLAG_KEY_FRAME` when `isKeyframe`,
  drains output buffers to the surface.
- Validates Annex B start codes and logs NAL types for diagnostics.

### Config (`EncodingConfig`)

`BitrateKbps = 8000`, `KeyframeIntervalSec = 2`, `TargetFps = 30`, `Codec = "jpeg"` (default).

## Issues

1. **Not hardware encoding / deviates from ADR-007.** OpenH264 is software; CPU-bound. Measured ~23 fps
   vs. 30–60 target. No GPU path until Media Foundation blocker is resolved.
2. **Media Foundation MFT still blocked.** `h264-encoder-implementation.md` documents 17+ failed activation
   attempts. No upgrade path available without a known-good reference implementation.
3. **Capture is not actually DXGI Desktop Duplication.** `DxgiCapture` uses `BitBlt` + `CreateDIBSection`
   (CPU copy, secondary-display DC quirks). Cursor compositing baked per frame. This is the GDI path
   under a misleading name; limits throughput independent of encoder.
4. **Field naming mismatch.** `CapturedFrame.JpegData` holds raw BGRA bytes in the H.264 path; encoder
   reads `rawFrame.JpegData`. Works but misleads; any refactor must touch both sides.
5. **Silent failures.** `H264Encoder.Initialize` and `Encode` swallow all exceptions (`catch { }`) and
   drop `InitError`. Engine only logs "H264 encoder init failed" with no reason; null encode results
   cause silent frame drops with no counter or log.
6. **No input validation of dimensions.** `Initialize` accepts any width/height; OpenH264 requires even
   dimensions for YUV; odd capture dims may fail or distort. Not checked.
7. **No SPS/PPS / parameter-set delivery handling.** Relies on OpenH264 emitting Annex B with start
   codes including SPS/PPS on IDR. If a decoder joins mid-stream it waits for the next IDR (up to
   `KeyframeIntervalSec`). No out-of-band config record.
8. **Keyframe cadence.** `uiIntraPeriod = 300` (~10 s) is long; forced keyframes every 2 s via
   `RequestKeyframe`. Forced frame is large; may spike bandwidth. No adaptive keyframe on scene change.
9. **Frame loop adds processing time.** `Task.Delay(10)` after work, so effective interval = encode time + 10 ms.
   No async handoff / latest-frame dropping; slow frames backlog.
10. **Dimension change reinit can permanently downgrade.** On reinit failure, session switches to JPEG +
    GdiCapture and never retries H.264 even if conditions recover.
11. **Android decoder fragility.** `dequeueInputBuffer(0)` with 0 timeout; if no buffer available the
    frame is dropped. `BUFFER_FLAG_KEY_FRAME` is set from the wire flag; if a non-keyframe is mislabeled
    or an IDR is missed, decoder corrupts until next IDR. No input-buffer exhaustion handling.
12. **No end-to-end validation.** No automated test encodes N frames and checks for an IDR + SPS/PPS;
    no measured throughput record. `CapResolutionTests` only covers resolution capping.
13. **Deployment gap.** Installer (`installer/screenly.iss`) copies `Screenly.Core.dll` etc. but does **not**
    copy `H264Sharp.dll`, `H264SharpNative-win64.dll`, or `openh264-2.4.1-win64.dll`. H.264 mode will
    fail on an installed machine (DLL load failure) unless the app folder is used directly.
14. **Host app encoder switcher.** `Screenly.Host` writes `config.json` codec field; engine polls config
    every ~? and switches, but switching codec mid-session recreates capture and may drop the client
    connection briefly. Not exercised in tests.

## Ponytail notes

- The Media Foundation path was deliberately abandoned after 17+ failed attempts; do not re-run
  activation experiments. The OpenH264 substitution is the "laziest working solution" and is acceptable
  until a known-good MF reference is available.
- `catch { }` in `Encode` is a deliberate corner-cut; upgrade path is to log + return null with a reason.
  Marked here rather than in code to avoid churn.

## Verification status

- `dotnet build` of `Screenly.Core` / `Screenly.Engine`: not re-run in this doc pass.
- End-to-end H.264 decode on Android: code present; no recorded measured throughput.
- Installer H.264 DLL gap: confirmed by inspection of `screenly.iss`.

## Related files

- `windows/Screenly.Core/Encoding/H264Encoder.cs`
- `windows/Screenly.Engine/Program.cs` (H264 init, frame loop, reinit)
- `windows/Screenly.Core/Capture/DxgiCapture.cs`, `GdiCapture.cs`, `CapturedFrame`
- `android/app/src/main/kotlin/com/screenly/video/VideoDecoder.kt`
- `windows/Screenly.Core/Config/AppConfig.cs` (`EncodingConfig`)
- `docs/plan/h264-encoder-implementation.md` (blocked Media Foundation plan)
- `docs/plan/documentation/adr.md` (ADR-007)
- `installer/screenly.iss`

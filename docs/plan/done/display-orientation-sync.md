# Plan — Display Orientation Sync (Windows ↔ Android)

**Goal:** When the Windows virtual display is rotated (portrait/landscape), the Android client rotates to match and the stream fills the screen with correct touch mapping.

**Scope:** Detection + propagation of oriented dimensions; Android activity orientation follow; geometry/touch already aspect-aware. No protocol change — `VIDEO_FRAME` width/height and `CONFIG` already carry oriented dimensions.

**Non-goals:** Auto-rotating the Windows virtual display from the phone; pen pressure; multi-monitor.

---

## Current state (verified)

- Windows capture (`GdiCapture`/`DxgiCapture`) reads `dmPelsWidth/Height` once in `Start()`; never refreshes. `DisplayManager.SetModeAsync` is a no-op (Parsec VDD has no set-mode IOCTL); rotation happens via Windows Display Settings on the virtual monitor.
- Engine stream loop sends `VideoFrameHeader(seq, ts, width, height, …)` from `capture.Width/Height`; Android `Frame` event updates `streamW/H` when they differ.
- Android `SurfaceView` is sized by `BoxWithConstraints` using `streamW/H` aspect, so letterboxing already adapts.
- Android never calls `Activity.setRequestedOrientation`, so the device stays in its physical orientation and portrait streams are letterboxed inside a landscape activity.
- `configChanges="orientation|screenSize|smallestScreenSize"` already prevents activity recreation.

## Design

1. **Windows — dynamic dimensions.** Capture must re-read the current mode periodically (or on a trigger) instead of caching forever. Engine detects a dimension change, logs it, and lets the next frame carry the new size; optionally sends `CONFIG` to nudge decoder reconfiguration.
2. **Android — follow the stream.** When incoming dimensions cross the portrait/landscape boundary (width vs height), request the matching orientation via `requestedOrientation` (guarded to avoid redundant calls and loops). Reapply fullscreen on `onConfigurationChanged`.
3. **Geometry** is already aspect-correct (`DisplayGeometry.contentRect`); verify with portrait streams.

## Tasks

### W1 — Dynamic capture dimensions (Windows)
- Files: `windows/Screenly.Core/Capture/ICapture.cs` (in `GdiCapture.cs`), `GdiCapture.cs`, `DxgiCapture.cs`.
- Add `bool RefreshDimensions()` to `ICapture` that re-runs `EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS)` and updates `_width/_height`; returns true if changed.
- Implement in both captures, keeping the existing fallback (primary screen) when device is null.
- Acceptance: rotating the virtual display in Windows Settings updates `capture.Width/Height` within one refresh cycle without restarting the engine.

### W2 — Detect & propagate in engine loop
- File: `windows/Screenly.Engine/Program.cs`.
- Before `TryGetFrame`, call `capture.RefreshDimensions()` (throttled, e.g. every 30 frames) when non-null; on change log `orientation/dimension change: WxH`.
- On change, send `CONFIG{width,height,fps,bitrateKbps,codec}` to all handshaked sessions (so Android reconfigures decoder immediately rather than waiting for a keyframe).
- For H.264 path, reinitialize encoder if dimensions changed (currently `CreateCaptureAsync` only runs at startup); otherwise fall back to JPEG until reinit. Document as `ponytail:` limitation.
- Acceptance: no engine restart needed; clients receive new dimensions within 1s.

### W3 — Android orientation follow
- File: `android/app/src/main/kotlin/com/screenly/MainActivity.kt`.
- In `ServerEvent.Frame` handling, after updating `streamW/H`, compute desired orientation from `streamW > streamH`. If it differs from the last applied, call `activity.requestedOrientation = …` (`SCREEN_ORIENTATION_SENSOR_LANDSCAPE` / `SCREEN_ORIENTATION_SENSOR_PORTRAIT`) and record it.
- Guard: only act when `firstFrameReceived` or on dimension change; don't fight user rotation lock? Keep simple: follow stream, since that's the product intent.
- Reapply fullscreen flags in `onConfigurationChanged` (override in Activity) in addition to `onWindowFocusChanged`.
- Acceptance: rotating Windows display to portrait makes the phone rotate to portrait and the video fills the screen; reverting restores landscape.

### W4 — Surface/decoder consistency
- Files: `VideoDecoder.kt`, `MainActivity.kt`.
- Ensure `decoder.configure(ev.w, ev.h)` is called on every dimension change (already in `Frame` and `Configured` branches) and that a keyframe is requested after reconfig. Add `requestKeyframe` handling if server supports; otherwise rely on periodic keyframes (every `TargetFps * KeyframeIntervalSec`).
- Acceptance: no garbage frames after rotation; decoder reconfigures within 2s.

### W5 — Touch mapping verification
- Files: `TouchCapture.kt`, `DisplayGeometry.kt`.
- No code change expected; add manual verification that `contentRect` + `viewToStream` produce correct coords for portrait streams (view and stream both portrait, and cross ratios).
- Acceptance: touch moves cursor to the correct point in portrait mode.

### W6 — Tests
- Windows: unit test `RefreshDimensions` logic is not P/Invoke-testable; cover the dimension-change detection helper if extracted. Add a small pure function `OrientationOf(w,h) -> bool isPortrait` test if introduced.
- Android: no test infra; manual verification matrix (portrait/landscape stream × portrait/landscape phone hold).
- Add a manual checklist to this plan's verification section.

## API contract
No new message types. `VIDEO_FRAME` width/height and `CONFIG` width/height are interpreted as **oriented** (as captured). Document this in `api-contract.md` under `VIDEO_FRAME` and `CONFIG`: "Dimensions reflect the current display orientation; clients must reconfigure on change." Optional: add `orientation: "portrait"|"landscape"` to `CONFIG` for clarity — decide during implementation; prefer dimensions-only to keep the diff minimal (ponytail).

## Risks
- `requestedOrientation` can conflict with system rotation settings / accessibility; document that Screenly takes over orientation while connected.
- Parsec VDD rotation via Windows Settings changes `dmPelsWidth/Height` but `dmDisplayOrientation` may be 0/1/2/3; capture must use the oriented dims (already does via `dmPelsWidth/Height` with `ENUM_CURRENT_SETTINGS`). Verify on real hardware.
- H.264 encoder reinit on dimension change not yet implemented (see W2 note); JPEG path is the safe fallback until then.
- Rapid rotation could thrash; throttle refresh (W1/W2) and guard orientation requests (W3).

## Verification checklist
- [ ] Rotate Windows virtual display to portrait via Settings → Android rotates to portrait within 2s.
- [ ] Video fills screen (no letterbox) in portrait; touch maps correctly.
- [ ] Rotate back to landscape → Android follows; no connection drop.
- [ ] JPEG path and (if implemented) H.264 path both handle the change.
- [ ] No leaked workers / no duplicate orientation requests in log.
- [ ] `dotnet build` + tests still pass.

## Files likely touched
- `windows/Screenly.Core/Capture/GdiCapture.cs`
- `windows/Screenly.Core/Capture/DxgiCapture.cs`
- `windows/Screenly.Engine/Program.cs`
- `android/app/src/main/kotlin/com/screenly/MainActivity.kt`
- `android/app/src/main/kotlin/com/screenly/video/VideoDecoder.kt` (verify only)
- `docs/plan/api-contract.md` (one-line note)
# Plan

Cursor still invisible on Android because the receiver never applies position updates and only the JPEG path can draw an overlay. Fix the receiver first, then make the overlay codec-independent, then harden Windows geometry. Keep the yellow marker as permanent fallback; cursor visibility must not depend on video capture or codec.

## Root cause findings (updated)

- **Receiver bug (primary):** `MainActivity.kt` has two `is ServerEvent.CursorPos` branches; the first is a no-op ("composited into frames") and wins, so `cursorX/cursorY/cursorVisible` never update. `cursorVisible` stays `false` and no overlay is ever drawn.
- **Codec-limited overlay:** cursor drawing exists only inside the JPEG `lockCanvas` branch. The H.264 path decodes to the `SurfaceView` and never composites the cursor, so H.264 shows no cursor even if position were applied.
- **Stale comment/contract:** code claims cursor is composited into frames, but capture backends no longer composite it; position/shape are sent separately. The duplicate branch encodes that false assumption.
- **Windows geometry defaults:** both capture backends default monitor origin to `(0,0)` when the monitor callback does not match, and cursor bounds use a fixed 32px margin. This can silently suppress `CursorPos` visibility for the virtual display.
- **H.264 not implemented:** `H264Encoder.Encode` returns `null` for every frame, so an H.264 session produces no video at all; cursor overlay work cannot be validated on H.264 until the encoder returns frames or JPEG is used.
- **Shape path mostly intact:** `CursorShapeCapture` reads hotspot/dimensions and rasterizes to PNG; Android decodes and caches it. This path is fine once position updates flow.

## Scope

- In: make `CursorPos` actually update state; draw one overlay above the video surface for both JPEG and H.264; harden monitor geometry/bounds; keep yellow marker fallback.
- Out: compositing cursor into captured frames, touch injection, latency tuning, animated cursors, H.264 encoder implementation (tracked separately).

## Action items

- [ ] Remove the no-op first `ServerEvent.CursorPos` branch in `MainActivity.kt` so the real handler runs; verify `cursorVisible` becomes true and marker/cursor moves.
- [ ] Move cursor drawing out of the JPEG-only canvas branch into a shared overlay above `SurfaceView` (Compose `Canvas`/`AndroidView` overlay) so JPEG and H.264 use the same path and scale.
- [ ] Keep yellow marker until a valid `CursorShape` bitmap arrives; on shape/decode failure stay on marker, never invisible.
- [ ] Make monitor geometry explicit in `GdiCapture`/`DxgiCapture`: fail or report when the selected monitor is not found instead of keeping origin `(0,0)`; use actual monitor bounds; intersect the cursor rectangle with the frame instead of a fixed 32px margin.
- [ ] Validate on JPEG first (known-good path), then H.264 only after `H264Encoder.Encode` returns real frames; record codec/version in test notes.
- [ ] Test arrow/I-beam/hand/resize/wait/custom/monochrome/high-contrast/large cursors, hotspots at center and all four edges, hidden/outside-display, reconnect, and no double cursor.

## Open questions

- None blocking. JPEG remains the verified path until H.264 produces frames. Native shape sync must never be able to make the cursor disappear.
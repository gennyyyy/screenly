# Plan — Smooth Cursor via Independent Overlay

**Goal:** Make the mouse cursor on the Android screen track smoothly and with low latency, instead of being baked into the ~15 fps video frames (choppy).

**Approach:** Stop compositing the cursor into captured frames. Stream cursor position/shape as separate high-rate messages (already defined in `api-contract.md` as `CURSOR_POS` 0x0B and `CURSOR_SHAPE` 0x0C) and render them as an Android overlay at the display refresh rate.

**No protocol change** — both message types and payloads already exist on Windows and Android.

---

## Current state (verified)

- `CursorDrawer.Draw` is called inside `GdiCapture.TryGetFrame` (and `DxgiCapture`), baking the cursor into every JPEG/H.264 frame → cursor updates at frame rate only.
- Engine never sends `CursorPos`/`CursorShape`.
- `ScreenlyClient` already parses both messages into `ServerEvent.CursorPos` / `ServerEvent.CursorShape`.
- `MainActivity` collects events but drops cursor events in the `else -> {}` branch; nothing is stored or rendered.
- `CursorShapeCapture.Capture()` exists and returns (width, height, hotspotX, hotspotY, png).

## Design

### Windows (engine)
1. **Cursor position loop** — a separate `Task.Run` at ~60 Hz that, for each handshaked session, sends `CursorPos{x, y, visible, tsUs}`. Coordinates are in stream/display space (`cursorScreen - monitorOrigin`), clamped to `[0,width]×[0,height]`. `visible=0` when hidden or outside.
2. **Cursor shape** — capture via `CursorShapeCapture.Capture()`; send `CursorShape` to all sessions when the cursor handle changes, and to a newly connected client right after `HELLO_ACK`.
3. **Handle-change detection** — store the last `hCursor` (from `GetCursorInfo`); resend shape only on change to avoid flooding.
4. **Remove compositing** — delete the `CursorDrawer.Draw(...)` call from `GdiCapture` (and `DxgiCapture`) so frames no longer contain a cursor. Keep `CursorDrawer` helper for DPI if still used elsewhere; otherwise leave it (harmless).

### Android
1. **State** — in `MainActivity`, hold `cursorPos: CursorPos?` and `cursorBitmap: Bitmap?` + hotspot, updated from `ServerEvent.CursorPos` / `ServerEvent.CursorShape`.
2. **Overlay** — a Compose `Canvas` sibling of the `SurfaceView`, sized identically to the video (`videoWidth × videoHeight`, centered). Draw the cached PNG at `streamToView(pos)` offset by `hotspot * scale`. Hide when `visible==false` or no position. Yellow circle fallback when no shape received.
3. **Decode** — `BitmapFactory.decodeByteArray` for the PNG; cache and only re-decode on shape change.
4. **Mapping** — reuse `DisplayGeometry.streamToView` with a full content rect (the overlay box is already stream-aspect, so scale is uniform).

## Tasks

### E1 — Engine cursor sender
- File: `windows/Screenly.Engine/Program.cs`.
- Add a `Task.Run` loop (~60 Hz) sending `CursorPos` to handshaked sessions using `GetCursorInfo` + monitor origin. Handle cancellation.
- Add handle-change detection + `CursorShape` send (broadcast on change; send current shape to a new client after HELLO_ACK).
- Acceptance: engine log shows cursor messages not required; Android receives them (verify via overlay appearing).

### E2 — Remove cursor compositing from capture
- Files: `windows/Screenly.Core/Capture/GdiCapture.cs`, `DxgiCapture.cs`.
- Remove the `CursorDrawer.Draw(...)` call from `TryGetFrame`. Leave the helper.
- Acceptance: frames no longer contain a cursor; cursor still visible via overlay.

### E3 — Android state + overlay
- File: `android/app/src/main/kotlin/com/screenly/MainActivity.kt`.
- Add `cursorPos` / `cursorBitmap` / hotspot state; handle `ServerEvent.CursorPos` and `ServerEvent.CursorShape` branches.
- Add a Compose `Canvas` overlay sized to the video, drawing the cursor with hotspot offset; yellow fallback if no bitmap.
- Acceptance: cursor visible, tracks movement, disappears when hidden.

### E4 — Verify no double cursor
- Manual: confirm only the overlay cursor appears (none baked into frames). If a baked cursor remains, re-check E2.
- Acceptance: exactly one cursor on screen.

### E5 — Performance sanity
- Manual: cursor should feel smooth at display refresh rate even when video is ~15 fps. Measure no extra GC pressure on Android (decode only on shape change).
- Acceptance: cursor motion visually smooth; no stutter tied to frame cadence.

## Risks
- **Compose recomposition at 60 Hz** for cursor state is acceptable but watch for jank; if needed, switch to `drawWithContent` reading a mutable state without full recomposition.
- **Coordinate drift** if monitor origin changes (multi-monitor/rotation); cursor uses the same origin capture uses. Re-verify after `display-orientation-sync` work.
- **Cursor shape size** — `CursorShapeCapture` renders at native handle size; DPI scaling of the drawn cursor on Windows is dropped in this path (position only). If cursor appears too small/large on high-DPI phones, scale by `monitorDpi/96` when sending shape or on Android. Decide during implementation; keep minimal (no scaling) first.
- **Race: shape before position** — Android caches shape; renders nothing until first `CursorPos`. Fine.
- **Removal of compositing is a one-way visual change** — keep `CursorDrawer` code (don't delete) so rollback is a one-line restore.

## Verification checklist
- [ ] Cursor visible on Android and tracks Windows mouse movement.
- [ ] Cursor motion is smooth even while video is ~15 fps / during heavy capture.
- [ ] No double cursor.
- [ ] Cursor hides when leaving the virtual display.
- [ ] Cursor shape changes (e.g., text caret) propagate.
- [ ] Touch mapping still correct.
- [ ] `dotnet build` + tests pass; Android builds.

## Files likely touched
- `windows/Screenly.Engine/Program.cs`
- `windows/Screenly.Core/Capture/GdiCapture.cs`
- `windows/Screenly.Core/Capture/DxgiCapture.cs`
- `android/app/src/main/kotlin/com/screenly/MainActivity.kt`
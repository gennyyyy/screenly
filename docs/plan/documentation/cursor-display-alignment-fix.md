# Cursor-Display Alignment — Troubleshooting Guide

## Problem Summary

The cursor displayed on the Android second screen did not match the cursor position on the Windows host. The offset was approximately 1 inch (visually noticeable). The issue persisted through multiple fix attempts.

## Root Causes (in order of discovery)

### 1. Double hotspot offset in DrawIconEx (Windows)

**What happened:** `DrawIconEx(hdc, x, y, hCursor, ...)` with a cursor handle automatically uses the cursor's hotspot as the origin — meaning `x,y` specifies where the hotspot goes, and the system draws the icon offset by `(-hotspotX, -hotspotY)`. The code was ALSO manually subtracting the hotspot (`cx - hx, cy - hy`), causing a double offset.

**What NOT to do:**
```csharp
// WRONG — double offset
int hx = ii.xHotspot, hy = ii.yHotspot;
DrawIconEx(hdcMem, cx - hx, cy - hy, hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
```

**What to do:**
```csharp
// CORRECT — DrawIconEx handles hotspot automatically
DrawIconEx(hdcMem, cx, cy, hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
```

### 2. Scaling coordinates by encode resolution mismatch (Windows)

**What happened:** The capture was at native resolution (e.g., 1920×1080) but `_encW`/`_encH` defaulted to 1280×720. The cursor position was scaled by `scaleX = _encW / _width = 0.667`, placing the cursor at `(cx * 0.667, cy * 0.667)` instead of `(cx, cy)`. But `hdcMem` was at native resolution (`_width × _height`), so the cursor was drawn at the wrong position.

**What NOT to do:**
```csharp
// WRONG — scaling cursor to encode resolution when hdcMem is at capture resolution
double scaleX = (double)_encW / _width;
int scx = (int)(cx * scaleX);
DrawIconEx(hdcMem, scx, scy, hCursor, ...);
```

**What to do:**
```csharp
// CORRECT — no scaling when capture and encode are same resolution
DrawIconEx(hdcMem, cx, cy, hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
```

### 3. Non-uniform aspect ratio scaling (Android)

**What happened:** The Android app scaled the 1920×1080 frame to fill a 1280×709 SurfaceView using `Rect(0, 0, width, height)`. The horizontal scale (0.667) and vertical scale (0.656) were different, distorting the image and shifting the cursor position.

**What NOT to do:**
```kotlin
// WRONG — non-uniform stretch distorts cursor position
canvas.drawBitmap(bmp, null, Rect(0, 0, surfaceView.width, surfaceView.height), null)
```

**What to do:**
```kotlin
// CORRECT — aspect-ratio-preserving letterbox
val scale = minOf(vw.toFloat() / bw, vh.toFloat() / bh)
val dw = (bw * scale).toInt()
val dh = (bh * scale).toInt()
val dx = (vw - dw) / 2
val dy = (vh - dh) / 2
canvas.drawColor(Color.BLACK)
canvas.drawBitmap(bmp, null, Rect(dx, dy, dx + dw, dy + dh), null)
```

### 4. Cursor baked into video frame (latency)

**What happened:** The cursor was drawn onto the captured frame bitmap before JPEG encoding. This meant the cursor position was captured at frame time but displayed later (encoding + network + decode latency). The cursor appeared to lag behind the actual position.

**What NOT to do:**
- Draw cursor onto the frame bitmap in `GdiCapture.TryGetFrame()`
- Send frames with baked-in cursor

**What to do:**
- Remove cursor compositing from `GdiCapture` (frames are cursor-free)
- Send cursor position as separate `CursorPos` message (~30 Hz)
- Android renders cursor as overlay on top of the video frame
- Use `DisplayGeometry` shared utility for consistent coordinate mapping

### 5. Monitor origin not verified

**What happened:** The monitor origin (`_monitorX`, `_monitorY`) is set via `EnumDisplayMonitors` callback. If the callback matches the wrong monitor, the cursor position calculation (`cx = ptScreenPos.x - _monitorX`) would be wrong.

**What to verify:**
- `FindVirtualDisplayDevice()` returns the correct Parsec device name
- `MonitorCallback` matches `mi.szDevice == _deviceName`
- `_monitorX` and `_monitorY` match the actual display position
- Log: `Capturing display at origin (X, Y)` should show the VDD display's position

### 6. No DPI awareness (Windows)

**What happened:** Without DPI awareness, Windows scales coordinates on high-DPI displays, causing `GetCursorInfo` to return scaled positions while `EnumDisplayMonitors` returns physical bounds.

**What to do:**
- Add `app.manifest` with `<dpiAwareness>PerMonitorV2</dpiAwareness>`
- Call `SetProcessDpiAwareness(2)` at startup as fallback
- Ensure all coordinate calculations use physical pixels

### 7. VDD display resolution

**What happened:** The VDD display defaulted to 1280×720 instead of 1920×1080. `ChangeDisplaySettingsEx` was attempted with an incorrect `DEVMODE` struct (missing `dmPositionX`, `dmDisplayOrientation`, etc.), returning -2 (bad mode).

**What to do:**
- Use the correct `DEVMODE` struct (with `dmPositionX`, `dmPositionY`, `dmDisplayOrientation`, `dmDisplayFixedOutput`, etc.)
- Call `EnumDisplaySettings(devName, -1, ...)` first to get current mode, then modify fields
- Use `CDS_UPDATEREGISTRY` flag
- Add custom resolution registry keys: `HKLM\SOFTWARE\Parsec\vdd\0` with `w`, `h`, `hz` DWORD values

### 8. Android SurfaceView size mismatch

**What happened:** SurfaceView size (1280×709) didn't match frame size (1920×1080). Non-uniform scaling distorted cursor position.

**What to do:**
- Use `DisplayGeometry.contentRect()` for aspect-ratio-preserving letterbox
- Use `DisplayGeometry.streamToView()` for cursor overlay positioning
- Use `DisplayGeometry.viewToStream()` for touch input mapping
- All three (video, cursor, touch) use the same content rectangle

## Architecture: Cursor Overlay Approach

### Protocol
- New message type: `CURSOR_POS = 0x0B`
- Payload: `[4 bytes: x (int32)] [4 bytes: y (int32)] [1 byte: visible (0/1)] [8 bytes: timestamp_us (uint64)]`
- Sent at ~30 Hz from engine to all connected clients
- Coordinates are in stream/display space (0..width, 0..height)

### Windows side
- `GdiCapture` no longer composites cursor (frames are cursor-free)
- Engine tracks cursor position via `GetCursorInfo` in a separate loop (~30 Hz)
- Sends `CursorPos` message to all connected sessions
- `capture.MonitorX`/`MonitorY` used for coordinate transform

### Android side
- `ServerEvent.CursorPos` event added to `ScreenlyClient`
- `MainActivity` tracks `cursorX`, `cursorY`, `cursorVisible` state
- Cursor drawn as overlay on SurfaceView after each video frame
- `DisplayGeometry.streamToView()` maps cursor coordinates to view coordinates
- Cursor state cleared on disconnect

## Files Modified

| File | Change |
|---|---|
| `windows/Screenly.Core/Capture/GdiCapture.cs` | Removed cursor compositing from TryGetFrame |
| `windows/Screenly.Core/Streaming/Protocol.cs` | Added `CursorPos = 0x0B` |
| `windows/Screenly.Engine/Program.cs` | Added cursor tracking loop, sends CursorPos messages |
| `android/.../net/Protocol.kt` | Added `CURSOR_POS: Byte = 0x0B` |
| `android/.../net/ScreenlyClient.kt` | Added `CursorPos` event, dispatch handling |
| `android/.../MainActivity.kt` | Added cursor state, overlay drawing, DisplayGeometry usage |
| `android/.../video/DisplayGeometry.kt` | New file: shared geometry utilities |
| `android/.../input/TouchCapture.kt` | Uses DisplayGeometry for touch mapping |
| `docs/plan/api-contract.md` | Added CURSOR_POS message spec |

## Common Pitfalls

1. **DrawIconEx hotspot**: When drawing a cursor handle, `DrawIconEx` automatically uses the hotspot as origin. Do NOT subtract the hotspot manually.
2. **Coordinate spaces**: Engine sends cursor in display coordinates (0..width). Android scales to view coordinates. Both must use the same frame dimensions.
3. **Monitor origin**: VDD display may be at non-zero origin (e.g., -1920, 483). Always subtract monitor origin from global cursor position.
4. **DPI scaling**: Without DPI awareness, `GetCursorInfo` returns scaled positions. Declare `PerMonitorV2` DPI awareness.
5. **Frame dimensions**: Use actual frame dimensions (from `EnumDisplaySettings`), not config values. The VDD display resolution may differ from config.
6. **Aspect ratio**: Always use aspect-ratio-preserving scaling (letterbox) for both video and cursor. Non-uniform scaling distorts cursor position.
7. **SurfaceView size**: SurfaceView dimensions may differ from screen dimensions. Use `surfaceView.width/height`, not screen dimensions.
8. **Frame-skip**: Don't use `available()` for frame skipping — it's unreliable. Use a bounded latest-frame queue instead.

## Verification Steps

1. Move cursor to center of VDD display → Android cursor should be at center of displayed image
2. Move cursor to each corner → Android cursor should be at corresponding corner
3. Move cursor rapidly → Android cursor should track with minimal lag (cursor updates at 30 Hz, frames at ~15 fps)
4. Test at different Windows scaling (100%, 125%, 150%, 200%) → cursor should remain accurate
5. Test at different resolutions (1280×720, 1920×1080) → cursor should remain accurate
6. Test portrait/landscape on Android → cursor should remain accurate
7. Disconnect/reconnect → cursor state should reset and re-sync

## If It Breaks Again

1. Check Windows logs for cursor position: `GetCursorInfo` should return `pos=(X, Y)` in global coords
2. Verify monitor origin: `monitor=(X, Y)` should match VDD display position
3. Verify local coords: `local=(X, Y)` should be within `0..width, 0..height`
4. Check Android logs for `CursorPos` events being received
5. Verify Android cursor overlay is being drawn (check `cursorVisible` state)
6. Check that frame dimensions match between engine and Android
7. Verify `DisplayGeometry.contentRect()` produces correct scaling
8. Ensure DPI awareness is enabled (check manifest and runtime call)
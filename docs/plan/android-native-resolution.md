# Plan: Use the Android device's native resolution for the streamed display

Goal: make the streamed display match the Android device's native resolution so the video fills the screen without letterboxing and the cursor area is correct. The app is USB-mode single-client, so dynamic resolution is feasible but requires recreating the Parsec virtual display (which has a fixed mode at creation).

## Options

### Option A — Keep 1920×1080 fixed (current)
- Android letterboxes. Simplest. No virtual display recreation.
- Already works; no code changes.

### Option B — Recreate the virtual display at the client's resolution on first Hello (recommended)
- Single-client only. On `Hello`, read `screenWidth`/`screenHeight`, recreate the virtual display at that resolution, reconfigure capture, send updated dimensions.
- Adds ~8s connection latency on resolution change (display teardown + creation + mode set).
- Cursor is composited into the frame, so no cursor-specific changes.

### Option C — Manual config (already available)
- User sets `VirtualDisplay.Width/Height` in `config.json` to match the phone.
- No code changes; no latency.

### Option D — Dynamic resolution at any time via `Config` message
- Most flexible, most complex. Defer until B is proven.

## Recommendation
Implement **Option B** for the first client, with **Option C** as the fallback if recreation fails. Defer Option D.

## Root causes found during testing (must fix)

### 1. Parsec VDD ADD ioctl buffer (CRITICAL — root cause of wrong resolution)
- **Symptom:** display is always created at 1920×1080 regardless of the requested resolution. `ChangeDisplaySettingsEx` returns -2 (DISP_CHANGE_BADMODE).
- **Root cause:** The `VDD_IOCTL_ADD` input buffer has width/height/refresh at offsets 0/2/4, but the driver may not apply them. Additionally, `ChangeDisplaySettingsEx` is called with `dwflags = CDS_UPDATEREGISTRY (0x1)` which fails for VDD displays, leaving the display at the default resolution. The resolution 1280×739 is not a multiple of 8, which may cause `ChangeDisplaySettingsEx` to reject it.
- **Fix:**
  - Put width/height/refresh in the ADD ioctl buffer (already done).
  - **Remove the `ChangeDisplaySettingsEx` call entirely.** The ADD ioctl should set the resolution.
  - **Round the resolution to a multiple of 8** before passing it to the VDD driver. 1280×739 → 1280×736 (nearest multiple of 8).
  - If the display is created at the wrong resolution (detected by checking `capture.Width`/`capture.Height` after creation), log a warning and fall back to 1920×1080.
  - Do NOT fall back to primary screen capture.

### 2. Multiple virtual displays accumulate
- **Symptom:** 3 displays created, 1 deleted, 2 remain after repeated connections.
- **Root cause:** `RemoveAsync` fails silently when `_active` is null (e.g., if `CreateAsync` threw). The VDD driver still created the display, but it's untracked. Also, `vdd-index.txt` only tracks the last created display, so orphaned displays from previous runs are not cleaned up. `ChangeDisplaySettingsEx` failing may also leave the display in a bad state.
- **Fix:**
  - Track **all** created display indices in `vdd-indices.txt` (one per line), not just the last one.
  - On startup, remove **all** persisted indices, not just one.
  - Make `RemoveAsync` robust: retry up to 3 times with increasing timeout. If still failing, log the error and persist the orphaned index for cleanup on next startup.
  - **Stop the ping thread before `RemoveAsync`** (the ping thread sends UPDATE ioctls that might interfere).
  - **Use synchronous I/O for REMOVE**: open a separate handle without `FILE_FLAG_OVERLAPPED`. Use synchronous `DeviceIoControl` (pass `IntPtr.Zero` for OVERLAPPED).
  - After `RecreateTo`, verify the old display was actually removed.
  - Add a `displayCount` tracker in `DisplayManager` to detect accumulation.

### 3. No diagnostic logging
- **Symptom:** failures are invisible because the engine runs with `CreateNoWindow`.
- **Fix:** log `clientW`/`clientH`, `FindVirtualDisplayDevice` result, monitor origin, capture size, `RemoveAsync` result, and `CreateAsync` result to `engine.log`.

### 4. Recursive Log method (stack overflow)
- **Symptom:** engine crashes on startup with stack overflow.
- **Root cause:** the diagnostic `Log` method called itself recursively.
- **Fix:** `Log` must write to the file once and return. Use `Console.WriteLine` or file append only, never self-call. A recursive `Log` causes a stack overflow that crashes the engine on startup.

### 5. Fallback to primary screen on failure
- **Symptom:** after `RemoveAsync` fails, `CreateAsync` fails because the VDD driver is in a bad state, and the engine falls back to primary screen capture (mirror mode).
- **Fix:** if `CreateAsync` fails, retry with the default 1920×1080 resolution. Do NOT fall back to primary screen capture. If the VDD driver is in a bad state, log the error and keep retrying.

## Design
- The engine starts with a default 1920×1080 display and captures it.
- On `Hello`, parse `screenWidth`/`screenHeight`. If valid and different from the current capture resolution, recreate the virtual display at the client's resolution.
- `SetModeAsync` is a no-op for Parsec VDD, so recreation = `RemoveAsync` + `CreateAsync` (no `ChangeDisplaySettingsEx`).
- Send a new `HelloAck` (or `Config`) with the updated dimensions. Android updates `streamW`/`streamH` and the `SurfaceView` resizes.
- Only the first client's resolution is used; subsequent clients letterbox.
- If recreation fails, keep the default resolution and letterbox.
- **Dynamic resolution can be disabled** via `AppConfig.VirtualDisplay.DynamicResolution` (default `true`). When `false`, the engine skips recreation and uses the configured `Width`/`Height` (Option C behavior).
- **Resolution cap**: if the client's resolution exceeds the safe maximum (default 3840×2160, configurable via `AppConfig.VirtualDisplay.MaxWidth`/`MaxHeight`), scale down to the largest resolution that fits within the cap while strictly preserving the client's aspect ratio. For example, a 4000×2000 phone (2:1) with a 3840×2160 cap becomes 3840×1920 (still 2:1). The scaling uses `min(capW / clientW, capH / clientH)` and rounds to even dimensions.
- **Aspect ratio preservation for VDD driver**: round the resolution to a multiple of 8 before passing it to the VDD driver. 1280×739 → 1280×736. The Android app letterboxes slightly, but the cursor and touch mapping use the actual stream dimensions.
- **Per-device resolution memory**: Android sends a stable device ID in the `Hello` message (`deviceId` field, added to the protocol). Recommended source: `Settings.Secure.ANDROID_ID` (per-app, per-device, survives reinstalls, no permissions needed). The engine uses this as the map key. This way, connecting phone A then phone B then phone A again uses each phone's remembered resolution without re-prompting. The 8s delay only happens on the first-ever connection for a new device or when the resolution changes.

## Should the engine wait for the first client before creating the display?

**Elaboration (answered: yes, defer)**

Currently the engine flow is:
1. Parsec VDD creates a 1920×1080 virtual display immediately (~5s).
2. Sets resolution via `ChangeDisplaySettingsEx`.
3. Force-extends topology.
4. Starts capture on that display.
5. Starts the TCP server and listens.

If we defer display creation until the first client connects:
- The engine starts the TCP server immediately (no 5s delay before the phone can connect).
- On first `Hello`, read the client's screen size, create the virtual display at that resolution, configure capture, then send `HelloAck`.
- The client waits ~8s for the display to be ready before seeing video.
- If no client ever connects, no virtual display is created (cleaner, no stray display).

**Trade-offs:**
- **Pro**: No stray 1920×1080 display when no phone is connected. Faster perceived startup (server ready instantly). Display matches the phone from the start.
- **Con**: First connection takes ~8s instead of ~5s. Startup code needs restructuring: separate "server start" from "display create". Capture loop starts after display creation.
- **Risk**: If display creation fails, the client must handle it (fallback to primary screen capture or error).

**Recommendation: defer.** The app is USB-mode single-client, so the 5s startup delay is wasted when no phone is connected. The extra ~3s on first connection is acceptable. The engine blocks on `Hello` until the display is ready, then sends a single `HelloAck` with the correct dimensions. The Android app's existing 5s handshake timeout must increase to 15s. The loading screen plan already handles "still connecting..." after 10s.

## Implementation steps (Windows)
1. Store the active `DisplayHandle` and capture reference in `Program.cs` (already stored in `display` and `capture`).
2. Add a helper `LoadPersistedResolutions()` that reads `vdd-resolutions.json` from `%APPDATA%\Screenly\`. Returns a `Dictionary<string, (int w, int h)>`. Returns empty dict if missing/invalid.
3. Add a helper `SavePersistedResolutions(Dictionary<string, (int w, int h)>)` that writes the map to `vdd-resolutions.json`.
4. On startup, no display is created yet (deferred). The TCP server starts immediately.
5. On `Hello`, parse `deviceId`, `screenWidth`, `screenHeight` from the JSON payload. Validate dimensions (e.g., 720×1280 to 3840×2160, aspect ratio sane).
6. Look up `deviceId` in the persisted map:
   - If found and valid: if the display is not yet created, create it at the persisted resolution. If already created at that resolution, skip. Send `HelloAck` with current dimensions.
   - If not found: if the display is not yet created, create it at the default 1920×1080 first (fast path), then recreate at the client's resolution if different. Store the mapping. Send `HelloAck` with the new dimensions.
7. If valid and different from `capture.Width`/`capture.Height`:
   - Set a `displayRecreating` flag.
   - Stop the capture loop.
   - Stop the ping thread.
   - `display.RemoveAsync(vddHandle)`. **Retry up to 3 times with increasing timeout. If still failing, try synchronous I/O (open handle without `FILE_FLAG_OVERLAPPED`). If still failing, log the error and persist the orphaned index. Do NOT set `_active = null` if removal fails.**
   - `display.CreateAsync(width, height, refreshHz)`. **Put width (uint16 at offset 0), height (uint16 at offset 2), and refresh rate (uint16 at offset 4) in the `inBuf` for `VDD_IOCTL_ADD`.**
   - **Do NOT call `ChangeDisplaySettingsEx`.** The ADD ioctl should set the resolution.
   - Re-run `FindVirtualDisplayDevice` to get the new device name. **Log the result.**
   - Recreate `GdiCapture`/`DxgiCapture` with the new device name and monitor origin.
   - Update `capture` reference.
   - Update the persisted map entry for this `deviceId`.
   - Save the map to `vdd-resolutions.json`.
   - Clear `displayRecreating`.
   - Send `HelloAck` with the new dimensions.
   - Restart the capture loop and ping thread.
8. If invalid or same, send `HelloAck` with current dimensions.
9. Handle `Config` message: if it contains `width`/`height`, treat as a resolution change request (same path, update the map entry).
10. Add a max-resolution cap (default 3840×2160, configurable) with aspect-ratio-preserving fallback.
11. Add `DynamicResolution` config flag (default `true`); when `false`, skip recreation and use configured `Width`/`Height`.
12. **Fix display accumulation:**
    - Track **all** created display indices in `vdd-indices.txt` (one per line), not just the last one.
    - On startup, remove **all** persisted indices, not just one.
    - Make `RemoveAsync` robust: retry up to 3 times with increasing timeout, then try synchronous I/O. If still failing, log the error and persist the orphaned index for cleanup on next startup.
    - After `RecreateTo`, verify the old display was actually removed.
13. On `RemoveAsync` (clean shutdown), delete all persisted indices so the next run starts fresh. (Stale values are validated on load anyway.)
14. **Add diagnostic logging:** log `clientW`/`clientH`, `FindVirtualDisplayDevice` result, monitor origin, capture size, `RemoveAsync` result, `CreateAsync` result to a file (`engine.log`) since the engine runs with `CreateNoWindow`.
15. **Fix `Log` method:** must NOT call itself recursively. Use `Console.WriteLine` or file append only, never self-call. A recursive `Log` causes a stack overflow that crashes the engine on startup.

## Implementation steps (Android)
1. Send `deviceId` in the `Hello` message. Recommended source: `Settings.Secure.ANDROID_ID` (per-app, per-device, survives reinstalls, no permissions needed). Add a `deviceId: String` field to the `Hello` data class.
2. Handle `ServerEvent.Configured` by updating `streamW`/`streamH` (currently falls to `else -> {}`).
3. Handle a resolution change mid-stream: the `BoxWithConstraints` recomposes when `streamW`/`streamH` change. The `SurfaceView` is recreated via `AndroidView` (factory runs once, but modifier updates). Ensure the `SurfaceView` dimensions update.
4. For H.264, reconfigure the `VideoDecoder` on dimension change.
5. `TouchCapture` already reads `streamW`/`streamH` via lambdas, so touch mapping adapts.
6. Cursor is composited into frames, so no cursor-specific changes.
7. Increase handshake timeout from 5s to 15s to accommodate display creation on the first-ever connection. Subsequent connections are fast if the persisted resolution matches.

## Risks
- Virtual display recreation takes ~8s. Connection latency increases significantly (only on resolution change).
- If recreation fails, the client must handle a fallback resolution (keep default).
- Multiple clients: only the first client's resolution is used; subsequent clients letterbox.
- The capture device name may change after recreation; re-run `FindVirtualDisplayDevice`.
- Android `SurfaceView` may flicker during resolution change.
- Parsec VDD driver may not tolerate rapid add/remove; test.
- The `HelloAck` is currently sent inside the `MessageHandler` switch; the recreation must happen before sending it, which blocks the handler. Consider moving recreation to a background task and sending `HelloAck` after.
- Stale persisted resolution: if the file contains an invalid value, validate and fall back to default. Delete the file on clean shutdown.
- Per-device map: if the map grows large (many devices), consider pruning entries older than N days. Not a concern for USB single-client use.
- **Multiple display accumulation**: if `RemoveAsync` fails silently, displays accumulate. The fix tracks all indices and cleans up on startup. Also try synchronous I/O for REMOVE.
- **Log recursion**: the diagnostic `Log` method must NOT call itself. Use `Console.WriteLine` or file append only, never self-call. A recursive `Log` causes a stack overflow that crashes the engine on startup.
- **VDD ADD ioctl**: the width/height/refresh must be in the buffer. `ChangeDisplaySettingsEx` is NOT used (returns -2 for VDD displays). Round to multiple of 8 for safety.

## Validation
- First-ever connection (no persisted resolution): display created at client resolution, ~8s wait, video matches phone.
- Second connection (persisted resolution matches): no recreation, fast connect (< 2s), video matches.
- Connect with a phone of a different resolution than persisted: recreation happens, new resolution persisted, ~8s wait.
- Connect with a second client of a different resolution; verify it letterboxes (no crash).
- Disconnect and reconnect; verify resolution is re-applied.
- Verify touch mapping works after resolution change.
- Verify no regression at 1920×1080.
- Verify the connection latency is acceptable (< 15s on first connect, < 2s on subsequent).
- Verify only ONE virtual display exists after connect/disconnect/reconnect cycles.
- Check `engine.log` for `FindVirtualDisplayDevice` result and capture size.
- Verify the cursor is visible in the stream after resolution change.

## Open questions
- None blocking. Option C (manual config) remains the fallback if Option B proves too fragile.